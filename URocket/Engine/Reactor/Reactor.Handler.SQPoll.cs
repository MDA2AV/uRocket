using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using URocket;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class RocketEngine {
    private static unsafe void ReactorHandlerSQPoll(int reactorId) {
        Dictionary<int, Connection> connections = Connections[reactorId];
        Reactor reactor = s_Reactors[reactorId];
        ConcurrentQueue<int> myQueue = ReactorQueues[reactorId]; // new FDs from acceptor
        io_uring_cqe*[] cqes = new io_uring_cqe*[reactor.Config.BatchCqes];

        const long WaitTimeoutNs = 1_000_000; // 1 ms
        __kernel_timespec ts; ts.tv_sec = 0; ts.tv_nsec = WaitTimeoutNs;

        // Optional: if your shim exposes this, cache whether SQPOLL is enabled for this ring
        // (purely for metrics / readability; submit logic should still key off NEED_WAKEUP).
        uint ringSetupFlags = reactor.Ring != null ? shim_get_ring_flags(reactor.Ring) : 0;
        bool isSqPoll = (ringSetupFlags & IORING_SETUP_SQPOLL) != 0;

        try {
            while (!StopAll) {
                // Track whether we queued any SQEs this iteration.
                bool queuedSqe = false;

                // 1) Drain acceptor queue and arm multishot recv for each new fd.
                while (myQueue.TryDequeue(out int newFd)) {
                    ArmRecvMultishot(reactor.Ring, newFd, c_bufferRingGID);
                    queuedSqe = true;
                }

                // 2) Submit only if we actually queued work (or if your ring says SQEs are pending).
                //    (If your Arm* methods can fail to get an SQE and you defer, keep shim_sq_ready too.)
                if (queuedSqe || shim_sq_ready(reactor.Ring) > 0) {
                    // IMPORTANT for SQPOLL:
                    // shim_submit() MUST do the equivalent of:
                    // - if (IORING_SQ_NEED_WAKEUP) -> io_uring_enter(..., IORING_ENTER_SQ_WAKEUP)
                    // If your shim doesn't, replace this call with a shim_submit_wakeup() that does.
                    shim_submit(reactor.Ring);
                }

                // 3) Wait for at least 1 CQE (1ms timeout), then drain the CQ in a batch.
                io_uring_cqe* cqe;
                int rc = shim_wait_cqes(reactor.Ring, &cqe, 1u, &ts);

                if (rc is -62 or < 0) { reactor.Counter++; continue; }

                int got;
                fixed (io_uring_cqe** pC = cqes) got = shim_peek_batch_cqe(reactor.Ring, pC, (uint)reactor.Config.BatchCqes);
                
                for (int i = 0; i < got; i++) {
                    cqe = cqes[i];

                    ulong ud = shim_cqe_get_data64(cqe);
                    UdKind kind = UdKindOf(ud);
                    int res = cqe->res;

                    if (kind == UdKind.Recv) {
                        int fd = UdFdOf(ud);

                        bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                        bool hasMore = (cqe->flags & IORING_CQE_F_MORE) != 0;

                        if (res <= 0) {
                            // Return buffer to ring if kernel provided one.
                            if (hasBuffer) {
                                ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                byte* addr = reactor.BufferRingSlab + (nuint)bufferId * (nuint)reactor.Config.RecvBufferSize;
                                shim_buf_ring_add(
                                    reactor.BufferRing,
                                    addr,
                                    (uint)reactor.Config.RecvBufferSize,
                                    bufferId,
                                    (ushort)reactor.BufferRingMask,
                                    reactor.BufferRingIndex++);
                                shim_buf_ring_advance(reactor.BufferRing, 1);
                            }

                            // IMPORTANT: remove from dictionary BEFORE returning to pool / closing fd.
                            // This prevents stale CQEs (already produced) from finding a recycled Connection.
                            if (connections.TryGetValue(fd, out Connection? connection)) {
                                connections.Remove(fd);

                                ConnectionPool.Return(connection);
                                close(fd);
                            }
                        }else {
                            ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);

                            if (connections.TryGetValue(fd, out Connection? connection)) {
                                connection.HasBuffer = hasBuffer;
                                connection.BufferId = bufferId;
                                connection.InPtr = reactor.BufferRingSlab + (nuint)bufferId * (nuint)reactor.Config.RecvBufferSize;
                                connection.InLength = res;

                                // Wake consumer
                                connection.SignalReadReady();

                                // If multishot stopped (no MORE flag), re-arm.
                                if (!hasMore) {
                                    ArmRecvMultishot(reactor.Ring, fd, c_bufferRingGID);
                                    queuedSqe = true;
                                }
                            } else {
                                // Defensive: if we got a recv for an fd we don't track,
                                // return its buffer so we don't leak ring entries.
                                if (hasBuffer) {
                                    byte* addr = reactor.BufferRingSlab + (nuint)bufferId * (nuint)reactor.Config.RecvBufferSize;
                                    shim_buf_ring_add(
                                        reactor.BufferRing,
                                        addr,
                                        (uint)reactor.Config.RecvBufferSize,
                                        bufferId,
                                        (ushort)reactor.BufferRingMask,
                                        reactor.BufferRingIndex++);
                                    shim_buf_ring_advance(reactor.BufferRing, 1);
                                }
                            }
                        }
                    } else if (kind == UdKind.Send) {
                        int fd = UdFdOf(ud);

                        if (connections.TryGetValue(fd, out Connection? connection)) {
                            if (res <= 0) {
                                // Treat send errors like close. Remove first to avoid stale use.
                                connections.Remove(fd);

                                // If this connection held a buffer ring entry, return it.
                                // (Only if your send path can happen while HasBuffer is true.)
                                if (connection.HasBuffer) {
                                    reactor.ReturnBufferRing(connection.InPtr, connection.BufferId);
                                    connection.HasBuffer = false;
                                }

                                ConnectionPool.Return(connection);
                                close(fd);
                            } else {
                                connection.OutHead += (nuint)res;

                                if (connection.OutHead < connection.OutTail)
                                {
                                    SubmitSend(
                                        reactor.Ring,
                                        connection.Fd,
                                        connection.OutPtr,
                                        connection.OutHead,
                                        connection.OutTail);
                                    queuedSqe = true;
                                }
                            }
                        }
                    }
                    shim_cqe_seen(reactor.Ring, cqe);
                }

                // 4) If we queued SQEs while processing CQEs (re-arms / continued sends), submit once.
                if (queuedSqe || shim_sq_ready(reactor.Ring) > 0) {
                    // Same SQPOLL note as above: must wake on NEED_WAKEUP.
                    shim_submit(reactor.Ring);
                }
            }
        } finally {
            // Close any remaining connections
            CloseAll(connections);

            // Free buffer ring BEFORE destroying the ring
            if (reactor.Ring != null && reactor.BufferRing != null) {
                shim_free_buf_ring(reactor.Ring, reactor.BufferRing, (uint)reactor.Config.BufferRingEntries, c_bufferRingGID);
                reactor.BufferRing = null;
            }

            // Destroy ring (unregisters CQ/SQ memory mappings)
            if (reactor.Ring != null) {
                shim_destroy_ring(reactor.Ring);
                reactor.Ring = null;
            }

            // Free slab memory used by buf ring
            if (reactor.BufferRingSlab != null) {
                NativeMemory.AlignedFree(reactor.BufferRingSlab);
                reactor.BufferRingSlab = null;
            }

            Console.WriteLine($"[w{reactorId}] Shutdown complete. (SQPOLL={isSqPoll})");
        }
    }
}