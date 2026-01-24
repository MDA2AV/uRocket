using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using URocket.Engine.Configs;
using URocket.Utils;
using URocket.Utils.MultiProducerSingleConsumer;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine 
{
    /// <summary>
    /// Object pool for Connection instances.
    /// Avoids per-connection allocations and allows reuse across reactors.
    /// </summary>
    private readonly ObjectPool<Connection.Connection> ConnectionPool =
        new DefaultObjectPool<Connection.Connection>(new ConnectionPoolPolicy(), 1024 * 32);
    /// <summary> Pool policy for Connection objects. </summary>
    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection.Connection> 
    {
        /// <summary>Create a new connection instance when the pool is empty.</summary>
        public override Connection.Connection Create() => new();
        /// <summary> Reset and return the connection to the pool. </summary>
        public override bool Return(Connection.Connection connection)
        {
            connection.Clear(); 
            return true; 
        }
    }
    
    /// <summary>
    /// A reactor owns one io_uring instance and processes:
    ///  - recv/send CQEs
    ///  - connection state
    ///  - buffer ring lifecycle
    /// Each reactor runs on its own thread.
    /// </summary>
    public partial class Reactor 
    {
        /// <summary>
        /// Counter used for leak diagnostics of buf_ring usage.
        /// </summary>
        private int _ringCounter;
        /// <summary>
        /// Kernel-registered io_uring buffer ring for multishot recv.
        /// </summary>
        private io_uring_buf_ring* _bufferRing;
        /// <summary>
        /// Slab backing all provided recv buffers.
        /// Each buffer is RecvBufferSize bytes.
        /// </summary>
        private byte* _bufferRingSlab;
        /// <summary>
        /// Next position in the buffer ring.
        /// </summary>
        private uint _bufferRingIndex;
        /// <summary>
        /// Mask for buffer ring indexing (power-of-two entries).
        /// </summary>
        private uint _bufferRingMask;
        /// <summary>
        /// Back-reference to owning engine.
        /// </summary>
        private readonly Engine _engine;
        /// <summary>
        /// Tracks which connections need flushing after batched writes.
        /// </summary>
        private readonly HashSet<int> _flushableFds = [];

        public Reactor(int id, ReactorConfig config, Engine engine) 
        {
            Id = id; 
            Config = config; 
            _engine = engine;
        }
        
        public Reactor(int id, Engine engine) : this(id, new ReactorConfig(), engine) { }
        
        /// <summary>Reactor ID (index into engine arrays).</summary>
        public int Id { get; }
        
        /// <summary>Configuration for this reactor.</summary>
        public ReactorConfig Config { get; }
        
        /// <summary>io_uring instance owned by this reactor.</summary>
        public io_uring* io_uring_instance { get; private set; }

        /// <summary>
        /// Creates the io_uring instance and registers the buffer ring.
        /// Also allocates and registers all recv buffers.
        /// </summary>
        public void InitRing() 
        {
            io_uring_instance = CreateRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, out int err, Config.RingEntries);
            if (io_uring_instance == null || err != 0) 
            {
                Console.WriteLine($"create_ring failed: {err}");
                return; // or throw; but do NOT continue using ring
            }

            uint ringFlags = shim_get_ring_flags(io_uring_instance);
            Console.WriteLine($"[w{Id}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");
            if (io_uring_instance == null || err < 0)
            {
                Console.Error.WriteLine($"[w{Id}] create_ring failed: {err}"); 
                return; 
            }
            
            _bufferRing = shim_setup_buf_ring(io_uring_instance, (uint)Config.BufferRingEntries, c_bufferRingGID, 0, out var ret);
            if (_bufferRing == null || ret < 0) 
                throw new Exception($"setup_buf_ring failed: ret={ret}");

            _bufferRingMask = (uint)(Config.BufferRingEntries - 1);
            nuint slabSize = (nuint)(Config.BufferRingEntries * Config.RecvBufferSize);
            _bufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

            for (ushort bid = 0; bid < Config.BufferRingEntries; bid++) 
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            }
            shim_buf_ring_advance(_bufferRing, (uint)Config.BufferRingEntries);
        }
        /// <summary>
        /// Returns a previously used recv buffer back to the kernel buf_ring.
        /// </summary>
        private void ReturnBufferRing(byte* addr, ushort bid) 
        {
            _ringCounter++;
            shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            shim_buf_ring_advance(_bufferRing, 1);
        }
        /// <summary>
        /// MPSC queue of buffer IDs waiting to be returned to buf_ring.
        /// </summary>
        private readonly MpscUshortQueue _returnQ = new(1 << 16); // 65536 slots (power-of-two)
        /// <summary>
        /// Enqueue a buffer ID to be returned to the buf_ring.
        /// May spin briefly under contention.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueReturnQ(ushort bid) 
        {
            if (!_returnQ.TryEnqueue(bid)) 
            {
                SpinWait sw = default;
                while (!_returnQ.TryEnqueue(bid)) 
                {
                    sw.SpinOnce();
                    if (sw.Count > 50) 
                    {
                        if (!_engine.ServerRunning) 
                            return; // only bail out after trying
                        Thread.Yield();
                        sw.Reset();
                    }
                }
            }
        }
        /// <summary>
        /// Drains the return queue and re-adds buffers to the buf_ring.
        /// </summary>
        private void DrainReturnQ() 
        {
            while (_returnQ.TryDequeue(out ushort bid)) 
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid);
            }
        }
        /// <summary>
        /// Same as DrainReturnQ but returns how many buffers were recycled.
        /// </summary>
        private int DrainReturnQCounted() 
        {
            int count = 0;
            while (_returnQ.TryDequeue(out ushort bid)) 
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid); // queues 1 SQE (buf_ring_add + advance)
                count++;
            }
            return count;
        }
        /// <summary>
        /// MPSC queue for outbound writes coming from application threads.
        /// </summary>
        private readonly MpscWriteItem _write = new(capacityPow2: 1024);

        public bool TryEnqueueWrite(WriteItem item) => _write.TryEnqueue(item);

        public bool TryDequeueWrite(out WriteItem item) => _write.TryDequeue(out item);
        /// <summary>
        /// Drain write queue, copy buffers into connection write slabs,
        /// and issue send SQEs for all affected connections.
        /// </summary>
        private void DrainWriteQ() 
        {
            _flushableFds.Clear();
            
            while (_write.TryDequeue(out WriteItem item))
            {
                if (_engine.Connections[Id].TryGetValue(item.ClientFd, out var connection))
                {
                    if (connection.CanWrite)
                    {
                        // Write buffer and free it
                        connection.Write(item.Buffer.Ptr, item.Buffer.Length);
                        item.Buffer.Free();
                        
                        _flushableFds.Add(connection.ClientFd);
                    }
                }
            }

            foreach (int fd in _flushableFds)
            {
                if (_engine.Connections[Id].TryGetValue(fd, out var connection))
                {
                    connection.CanWrite = false; // Reset write flag for each drained connection
                    
                    if(connection.CanFlush)
                        Send(connection.ClientFd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)connection.WriteTail);
                }
            }
        }
        /// <summary>
        /// Enqueue a send SQE for this reactor's ring.
        /// </summary>
        private void Send(int clientFd, byte* buf, nuint off, nuint len) 
        {
            io_uring_sqe* sqe = SqeGet(io_uring_instance);
            shim_prep_send(sqe, clientFd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, clientFd)); 
        }
        /// <summary>
        /// Static helper to enqueue a send on an arbitrary ring.
        /// </summary>
        private static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len) 
        {
            io_uring_sqe* sqe = SqeGet(pring);
            shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
        }
        /// <summary>
        /// Cancels an outstanding multishot recv for a given fd.
        /// </summary>
        private static void SubmitCancelRecv(io_uring* ring, int fd) 
        {
            io_uring_sqe* sqe = shim_get_sqe(ring);
            if (sqe == null) return; // or handle SQ full

            ulong target = PackUd(UdKind.Recv, fd);

            shim_prep_cancel64(sqe, target, /*flags*/ 0 /* or IORING_ASYNC_CANCEL_ALL */);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Cancel, fd));
        }
        /// <summary>
        /// Closes all remaining connections during shutdown and
        /// returns them to the pool.
        /// </summary>
        private void CloseAll(Dictionary<int, Connection.Connection> connections) 
        {
            Console.WriteLine($"Reactor[{Id}] Connection leakage -- [{connections.Count}] " +
                              $"Ring leakage -- [{_ringCounter + Config.BufferRingEntries - _bufferRingIndex}]");
            
            foreach (var kv in connections) 
            {
                var conn = kv.Value;

                // Mark closed to wake any waiter.
                conn.MarkClosed(error: 0);

                // Remove mapping first (so late CQEs won't find it).
                // Already doing remove on res<=0 path; doing same here if needed.

                // Close fd
                try
                {
                    close(conn.ClientFd); 
                } catch { /* ignore */ }

                // Pool it. Safe only because:
                //   - ReadAsync uses generation/closed => will return Closed for stale handlers
                //   - We did NOT return any recv buffers here
                _engine.ConnectionPool.Return(conn);
            }

            connections.Clear();
        }
    }
}