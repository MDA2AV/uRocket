---
title: ConnectionPipeReader
weight: 5
---

`ConnectionPipeReader` is a zero-copy `PipeReader` adapter over zerg's native connection API. Unlike `ConnectionStream` which copies received bytes into a caller-provided buffer, this adapter hands the io_uring kernel buffers directly to the consumer as a `ReadOnlySequence<byte>`. Buffers are only returned to the reactor pool when the consumer advances past them.

## Class Definition

```csharp
public sealed class ConnectionPipeReader : PipeReader
```

## Constructor

```csharp
public ConnectionPipeReader(Connection inner)
```

Wraps an existing `Connection` in a `PipeReader` interface. The underlying connection must already be registered with a reactor.

## Key Advantages Over ConnectionStream

| | ConnectionPipeReader | ConnectionStream |
|---|---|---|
| **Copies** | Zero — data stays in kernel buffers | One copy per read |
| **Partial consumption** | Yes — consume a prefix, examine more | No — all data consumed at once |
| **Buffer lifetime** | Held until `AdvanceTo` releases them | Returned immediately after copy |
| **API model** | `ReadOnlySequence<byte>` | `byte[]` / `Memory<byte>` |

## Usage

```csharp
static async Task HandleConnectionAsync(Connection connection)
{
    var reader = new ConnectionPipeReader(connection);

    while (true)
    {
        var result = await reader.ReadAsync();
        if (result.IsCompleted)
            break;

        var buffer = result.Buffer;

        // Parse the buffer...
        // For example, find a delimiter:
        SequencePosition? pos = buffer.PositionOf((byte)'\n');

        if (pos != null)
        {
            // Consume up to and including the delimiter
            var line = buffer.Slice(0, pos.Value);
            ProcessLine(line);

            reader.AdvanceTo(buffer.GetPosition(1, pos.Value), buffer.End);
        }
        else
        {
            // Not enough data yet — examine everything, consume nothing
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    reader.Complete();
}
```

## ReadAsync

```csharp
public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
```

Waits for data from the reactor and returns it as a `ReadResult` (from `System.IO.Pipelines`) containing a `ReadOnlySequence<byte>` backed by the original io_uring kernel buffers.

**Behavior:**

- If unconsumed data from a previous partial `AdvanceTo` exists, returns it immediately
- If the connection is closed, returns with `IsCompleted = true`
- Otherwise, awaits the next reactor delivery via `connection.ReadAsync()`
- Drains all ring items from the snapshot into an internal held-buffer list
- Calls `connection.ResetRead()` internally (the consumer does not need to call it)

**Returns:** A `System.IO.Pipelines.ReadResult` with:

| Property | Description |
|----------|-------------|
| `Buffer` | `ReadOnlySequence<byte>` over held kernel buffers |
| `IsCanceled` | `true` if `CancelPendingRead()` was called |
| `IsCompleted` | `true` if the connection was closed |

## TryRead

```csharp
public override bool TryRead(out ReadResult result)
```

Non-blocking read. Returns `true` if there is held data, a pending cancellation, or the connection is closed. Returns `false` if no data is available (does not wait).

## AdvanceTo

```csharp
public override void AdvanceTo(SequencePosition consumed)
public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
```

Advances the reader past consumed data. Kernel buffers that are fully consumed are returned to the reactor pool. Partially consumed buffers are sliced and retained for the next `ReadAsync`.

**This is where buffer lifecycle is controlled.** Unlike the raw Connection API where you manually call `ReturnRing()`, the PipeReader adapter handles buffer returns automatically based on the consumed position.

```csharp
// Consume everything
reader.AdvanceTo(buffer.End);

// Consume a prefix, keep the rest
reader.AdvanceTo(consumed: prefixEnd, examined: buffer.End);

// Consume nothing, just examine (wait for more data)
reader.AdvanceTo(buffer.Start, buffer.End);
```

## CancelPendingRead

```csharp
public override void CancelPendingRead()
```

Requests cancellation of the current or next `ReadAsync`. The next `ReadAsync` will return with `IsCanceled = true`.

## Complete

```csharp
public override void Complete(Exception? exception = null)
```

Marks the reader as completed. All held kernel buffers are returned to the reactor pool. Further reads will throw `InvalidOperationException`. Safe to call multiple times.

## When to Use ConnectionPipeReader

Use `ConnectionPipeReader` when:

- You need **partial consumption** (e.g., parsing framed protocols where a recv boundary splits a message)
- You want **zero-copy reads** with a standard `PipeReader` API
- You're integrating with libraries that accept `PipeReader` (e.g., ASP.NET Core, Bedrock)
- You need `ReadOnlySequence<byte>` for multi-segment parsing with `SequenceReader<byte>`

For maximum control, prefer the native `Connection` API directly (`ReadAsync`/`TryGetRing`/`ReturnRing`). For `Stream`-based API compatibility, use `ConnectionStream`.

## Design Notes

- **No internal buffering:** Data comes directly from the reactor's kernel-provided buffers
- **Automatic buffer management:** `AdvanceTo` handles `ReturnRing` calls — no manual buffer returns needed
- **Automatic `ResetRead`:** Called internally after draining a snapshot — the consumer does not call it
- **Single reader:** Only one `ReadAsync` can be outstanding at a time (inherited from the underlying Connection contract)
