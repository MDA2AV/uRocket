using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine {
    public void AcceptorHandler(Acceptor acceptor, int reactorCount) {
        try {
            int nextReactor = 0;
            int one = 1;
            Console.WriteLine($"[acceptor] Load balancing across {reactorCount} reactors");

            while (!StopAll) {
                int got;
                fixed (io_uring_cqe** pC = acceptor.Cqes)
                    got = shim_peek_batch_cqe(acceptor.Ring, pC, (uint)acceptor.Cqes.Length);

                if (got <= 0) {
                    io_uring_cqe* oneCqe = null;
                    if (shim_wait_cqe(acceptor.Ring, &oneCqe) != 0) continue;
                    acceptor.Cqes[0] = oneCqe;
                    got = 1;
                }

                for (int i = 0; i < got; i++) {
                    io_uring_cqe* cqe = acceptor.Cqes[i];
                    ulong ud = shim_cqe_get_data64(cqe);
                    UdKind kind = UdKindOf(ud);
                    int res = cqe->res;

                    if (kind == UdKind.Accept) {
                        if (res >= 0) {
                            int clientFd = res;
                            
                            // TCP_NODELAY
                            setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

                            // Round-robin to next reactor
                            // TODO: This is naive, not all connections are the same,
                            // TODO: should balance considering each connection's weight
                            // TODO: Allow user to inject balancing logic and provide multiple algorithms he can choose from
                            int targetReactor = nextReactor;
                            nextReactor = (nextReactor + 1) % reactorCount;

                            ReactorQueues[targetReactor].Enqueue(clientFd);
                            Connections[targetReactor][clientFd] = ConnectionPool.Get()
                                .SetFd(clientFd)
                                .SetReactorId(targetReactor)
                                .SetReactor(Reactors[targetReactor]);
                            
                            bool connectionAdded = ConnectionQueues.Writer.TryWrite(new ConnectionItem(targetReactor, clientFd));
                            if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                            
                        }else { Console.WriteLine($"[acceptor] Accept error: {res}"); }
                    }
                    shim_cqe_seen(acceptor.Ring, cqe);
                }
                if (shim_sq_ready(acceptor.Ring) > 0) { Console.WriteLine("Submitting3"); shim_submit(acceptor.Ring); }
            }
        }
        finally {
            // close listener and ring even on exception/StopAll
            if (acceptor.ListenFd >= 0) close(acceptor.ListenFd);
            if (acceptor.Ring != null) shim_destroy_ring(acceptor.Ring);
            Console.WriteLine($"[acceptor] Shutdown complete.");
        }
    }
}