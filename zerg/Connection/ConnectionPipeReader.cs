using System.Buffers;
using System.IO.Pipelines;
using zerg.Utils;

namespace zerg;

/// <summary>
/// Zero-copy <see cref="PipeReader"/> adapter over <see cref="Connection"/>.
///
/// Unlike <see cref="ConnectionStream"/> which copies received bytes into a caller-provided
/// buffer on every <c>ReadAsync</c>, this adapter hands the io_uring kernel buffers directly
/// to the consumer as a <see cref="ReadOnlySequence{T}"/>. Buffers are only returned to the
/// reactor pool when the consumer advances past them via <see cref="AdvanceTo"/>.
///
/// This gives two key advantages over the Stream path:
/// <list type="bullet">
///   <item>No per-read copy — data stays in the original unmanaged receive buffers.</item>
///   <item>Partial consumption — the consumer can examine data, consume only a prefix
///         (e.g. one complete message), and leave the rest for the next <c>ReadAsync</c>.</item>
/// </list>
///
/// Standard PipeReader contract applies:
/// <code>
/// var reader = new ConnectionPipeReader(connection);
/// while (true)
/// {
///     var result = await reader.ReadAsync();
///     var buffer = result.Buffer;
///     // parse...
///     reader.AdvanceTo(consumed, examined);
///     if (result.IsCompleted) break;
/// }
/// reader.Complete();
/// </code>
/// </summary>
public sealed class ConnectionPipeReader : PipeReader
{
    private readonly Connection _inner;

    /// <summary>
    /// Ring buffers dequeued from the connection but not yet fully consumed.
    /// Each entry holds a (possibly sliced) memory region and the kernel buffer ID
    /// needed to return it to the reactor pool.
    /// </summary>
    private readonly List<HeldBuffer> _held = new(4);

    /// <summary>
    /// The last <see cref="ReadOnlySequence{T}"/> returned to the consumer.
    /// Kept so <see cref="AdvanceTo"/> can compute consumed byte count from positions.
    /// </summary>
    private ReadOnlySequence<byte> _lastSequence;

    private bool _completed;
    private bool _cancelRequested;
    private bool _connectionClosed;

    public ConnectionPipeReader(Connection inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    // -----------------------------------------------------------------
    // ReadAsync
    // -----------------------------------------------------------------

    public override async ValueTask<ReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ReadResult(BuildSequence(), isCanceled: true, isCompleted: _connectionClosed);
        }

        // Unconsumed data from a previous partial AdvanceTo — return it immediately.
        if (_held.Count > 0)
            return new ReadResult(BuildSequence(), isCanceled: false, isCompleted: _connectionClosed);

        if (_connectionClosed)
            return new ReadResult(default, isCanceled: false, isCompleted: true);

        // Await new data from the reactor.
        var result = await _inner.ReadAsync();

        if (result.IsClosed)
        {
            _connectionClosed = true;
            return new ReadResult(BuildSequence(), isCanceled: false, isCompleted: true);
        }

        DrainSnapshot(result);
        _inner.ResetRead();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ReadResult(BuildSequence(), isCanceled: true, isCompleted: false);
        }

        return new ReadResult(BuildSequence(), isCanceled: false, isCompleted: false);
    }

    // -----------------------------------------------------------------
    // TryRead
    // -----------------------------------------------------------------

    public override bool TryRead(out ReadResult result)
    {
        ThrowIfCompleted();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            result = new ReadResult(BuildSequence(), isCanceled: true, isCompleted: _connectionClosed);
            return true;
        }

        if (_held.Count > 0)
        {
            result = new ReadResult(BuildSequence(), isCanceled: false, isCompleted: _connectionClosed);
            return true;
        }

        if (_connectionClosed)
        {
            result = new ReadResult(default, isCanceled: false, isCompleted: true);
            return true;
        }

        result = default;
        return false;
    }

    // -----------------------------------------------------------------
    // AdvanceTo
    // -----------------------------------------------------------------

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (_held.Count == 0)
            return;

        long consumedBytes = _lastSequence.Slice(0, consumed).Length;

        while (_held.Count > 0 && consumedBytes > 0)
        {
            var seg = _held[0];
            int available = seg.Memory.Length;

            if (consumedBytes >= available)
            {
                // Fully consumed — return kernel buffer to reactor pool.
                _inner.ReturnRing(seg.BufferId);
                _held.RemoveAt(0);
                consumedBytes -= available;
            }
            else
            {
                // Partially consumed — keep the remainder.
                _held[0] = new HeldBuffer(seg.Memory.Slice((int)consumedBytes), seg.BufferId);
                consumedBytes = 0;
            }
        }
    }

    // -----------------------------------------------------------------
    // Cancel / Complete
    // -----------------------------------------------------------------

    public override void CancelPendingRead()
        => _cancelRequested = true;

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
            return;

        _completed = true;

        // Return all held kernel buffers back to the reactor pool.
        foreach (var seg in _held)
            _inner.ReturnRing(seg.BufferId);

        _held.Clear();
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Dequeue all ring items from the current snapshot into <see cref="_held"/>.
    /// </summary>
    private void DrainSnapshot(RingSnapshot result)
    {
        var rings = _inner.GetAllSnapshotRingsAsUnmanagedMemory(result);
        foreach (var ring in rings)
            _held.Add(new HeldBuffer(ring.Memory, ring.BufferId));
    }

    /// <summary>
    /// Build a <see cref="ReadOnlySequence{T}"/> over all held (unconsumed) segments.
    /// </summary>
    private ReadOnlySequence<byte> BuildSequence()
    {
        if (_held.Count == 0)
        {
            _lastSequence = default;
            return _lastSequence;
        }

        if (_held.Count == 1)
        {
            _lastSequence = new ReadOnlySequence<byte>(_held[0].Memory);
            return _lastSequence;
        }

        var head = new RingSegment(_held[0].Memory, _held[0].BufferId);
        var tail = head;

        for (int i = 1; i < _held.Count; i++)
            tail = tail.Append(_held[i].Memory, _held[i].BufferId);

        _lastSequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        return _lastSequence;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException(
                "Reading is not allowed after the reader was completed.");
    }

    /// <summary>
    /// A held receive buffer that may have been partially consumed (sliced).
    /// </summary>
    private readonly record struct HeldBuffer(ReadOnlyMemory<byte> Memory, ushort BufferId);
}
