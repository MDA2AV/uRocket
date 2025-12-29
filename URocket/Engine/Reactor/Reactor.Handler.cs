using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class RocketEngine {
    private static void ReactorHandler(int reactorId) {
        Dictionary<int,Connection> connections = Connections[reactorId];
        Reactor reactor   = s_Reactors[reactorId];
        ConcurrentQueue<int> myQueue = ReactorQueues[reactorId];     // new FDs from acceptor
        
        io_uring_cqe*[] cqes = new io_uring_cqe*[reactor.Config.BatchCqes];
        const long WaitTimeoutNs = 1_000_000; // 1 ms

        try {
            while (!StopAll) {
                // TODO: Move multishot to the Reactor logic?
                while (myQueue.TryDequeue(out int newFd)) { ArmRecvMultishot(reactor.Ring, newFd, c_bufferRingGID); }
                if (shim_sq_ready(reactor.Ring) > 0) shim_submit(reactor.Ring);
                io_uring_cqe* cqe; __kernel_timespec ts; ts.tv_sec  = 0; ts.tv_nsec = WaitTimeoutNs; // 1 ms timeout
                int rc = shim_wait_cqes(reactor.Ring, &cqe, (uint)1, &ts); int got;
                
                if (rc is -62 or < 0) { reactor.Counter++; continue; }

                fixed (io_uring_cqe** pC = cqes) got = shim_peek_batch_cqe(reactor.Ring, pC, (uint)reactor.Config.BatchCqes);

                for (int i = 0; i < got; i++) {
                    cqe = cqes[i];
                    ulong ud = shim_cqe_get_data64(cqe);
                    UdKind kind = UdKindOf(ud);
                    int res  = cqe->res;

                    if (kind == UdKind.Recv) {
                        int fd = UdFdOf(ud);
                        bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                        bool hasMore   = (cqe->flags & IORING_CQE_F_MORE) != 0;

                        if (res <= 0) {
                            Console.WriteLine($"{reactor.ReactorId} {reactor.Counter}");
                            if (hasBuffer) {
                                ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                byte* addr = reactor.BufferRingSlab + (nuint)bufferId * (nuint)reactor.Config.RecvBufferSize;
                                shim_buf_ring_add(reactor.BufferRing, addr, (uint)reactor.Config.RecvBufferSize, bufferId, (ushort)reactor.BufferRingMask, reactor.BufferRingIndex++);
                                shim_buf_ring_advance(reactor.BufferRing, 1);
                            }
                            if (connections.TryGetValue(fd, out var connection)) {
                                ConnectionPool.Return(connection);
                                close(fd);
                            }
                        } else {
                            var bufferId = (ushort)shim_cqe_buffer_id(cqe);

                            if (connections.TryGetValue(fd, out var connection)) {
                                connection.HasBuffer = hasBuffer;
                                connection.BufferId = bufferId;
                                connection.InPtr = reactor.BufferRingSlab + (nuint)connection.BufferId * (nuint)reactor.Config.RecvBufferSize;
                                connection.InLength = res;
                                connection.SignalReadReady();
                                
                                if (!hasMore) ArmRecvMultishot(reactor.Ring, fd, c_bufferRingGID);
                            }
                        }
                    }
                    else if (kind == UdKind.Send) {
                        int fd = UdFdOf(ud);
                        if (connections.TryGetValue(fd, out var connection)) {
                            // Advance send progress.
                            connection.OutHead += (nuint)res;
                            if (connection.OutHead < connection.OutTail)
                                SubmitSend(reactor.Ring, connection.Fd, connection.OutPtr, connection.OutHead, connection.OutTail);
                        }
                    }
                    shim_cqe_seen(reactor.Ring, cqe);
                }
            }
        }
        finally
        {
            // Close any remaining connections
            CloseAll(connections);
            // Free buffer ring BEFORE destroying the ring
            if (reactor.Ring != null && reactor.BufferRing != null) {
                shim_free_buf_ring(reactor.Ring, reactor.BufferRing, (uint)reactor.Config.BufferRingEntries, c_bufferRingGID);
                reactor.BufferRing = null;
            }
            // Destroy ring (unregisters CQ/SQ memory mappings)
            if (reactor.Ring != null) { shim_destroy_ring(reactor.Ring); reactor.Ring = null; }
            // Free slab memory used by buf ring
            if (reactor.BufferRingSlab != null) { NativeMemory.AlignedFree(reactor.BufferRingSlab); reactor.BufferRingSlab = null; }
            Console.WriteLine($"[w{reactorId}] Shutdown complete.");
        }
    }
}