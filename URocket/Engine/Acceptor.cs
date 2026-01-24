using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using URocket.Engine.Configs;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine 
{
    public class Acceptor 
    {
        private io_uring* _io_uring;
        private io_uring_sqe* _sqe;
        private readonly Engine _engine;
        private readonly AcceptorConfig _acceptorConfig;
        private readonly io_uring_cqe*[] _cqes;
        private readonly int _listenFd;

        public Acceptor(Engine engine) : this(new AcceptorConfig(), engine) { }

        public Acceptor(AcceptorConfig acceptorConfig, Engine engine) 
        {
            _acceptorConfig = acceptorConfig; 
            _engine = engine;
            _listenFd = CreateListenerSocket(_engine.Ip, _engine.Port); 
            _cqes = new io_uring_cqe*[_acceptorConfig.BatchSqes];
        }

        public void InitRing() 
        {
            _io_uring = CreateRing(_acceptorConfig.RingFlags, _acceptorConfig.SqCpuThread, _acceptorConfig.SqThreadIdleMs, out int err, _acceptorConfig.RingEntries);
            CheckRingFlags(shim_get_ring_flags(_io_uring));
            if (_io_uring == null || err < 0) { Console.Error.WriteLine($"[acceptor] create_ring failed: {err}"); return; }
            // Start multishot accept
            _sqe = SqeGet(_io_uring);
            shim_prep_multishot_accept(_sqe, _listenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(_sqe, PackUd(UdKind.Accept, _listenFd));
            shim_submit(_io_uring);
            Console.WriteLine("[acceptor] Multishot accept armed");
        }

        private void CheckRingFlags(uint flags) 
        {
            Console.WriteLine($"[acceptor] ring flags = 0x{flags:x} " +
                              $"(SQPOLL={(flags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(flags & IORING_SETUP_SQ_AFF) != 0})");
        }
        
        private int CreateListenerSocket(string ip, ushort port)
        {
            int lfd = socket(AF_INET, SOCK_STREAM, 0);
            if (lfd < 0) ThrowErrno("socket");

            try
            {
                int one = 1;

                if (setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(SO_REUSEADDR)");

                // Linux only; great for multi-reactor accept, but fails on some platforms/kernels/configs.
                if (setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(SO_REUSEPORT)");

                // TCP_NODELAY on a listening socket is not useful.
                // Remove it here; set it on accepted sockets instead.

                sockaddr_in addr = default;
                addr.sin_family = (ushort)AF_INET;
                addr.sin_port = Htons(port);

                // Better: use ASCII bytes, not UTF8.
                byte[] ipb = Encoding.ASCII.GetBytes(ip + "\0");
                fixed (byte* pip = ipb)
                {
                    int rc = inet_pton(AF_INET, (sbyte*)pip, &addr.sin_addr);
                    if (rc == 0) throw new ArgumentException($"Invalid IPv4 address: {ip}", nameof(ip));
                    if (rc < 0) ThrowErrno("inet_pton");
                }

                if (bind(lfd, &addr, (uint)sizeof(sockaddr_in)) < 0)
                    ThrowErrno("bind");

                if (listen(lfd, _engine.Backlog) < 0)
                    ThrowErrno("listen");

                int fl = fcntl(lfd, F_GETFL, 0);
                if (fl < 0) ThrowErrno("fcntl(F_GETFL)");

                if (fcntl(lfd, F_SETFL, fl | O_NONBLOCK) < 0)
                    ThrowErrno("fcntl(F_SETFL,O_NONBLOCK)");

                return lfd;
            }
            catch
            {
                close(lfd);
                throw;
            }
        }
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowErrno(string op)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"{op} failed, errno={err}");
        }
        
        public void Handle(Acceptor acceptor, int reactorCount) 
        {
            try 
            {
                int nextReactor = 0;
                int one = 1;
                __kernel_timespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = _acceptorConfig.CqTimeout;
                Console.WriteLine($"[acceptor] Load balancing across {reactorCount} reactors");

                while (_engine.ServerRunning) 
                {
                    int got;
                    fixed (io_uring_cqe** pC = acceptor._cqes)
                        got = shim_peek_batch_cqe(acceptor._io_uring, pC, (uint)acceptor._cqes.Length);

                    if (got <= 0) 
                    {
                        io_uring_cqe* oneCqe = null;
                        //if (shim_wait_cqe(acceptor._io_uring, &oneCqe) != 0) continue;
                        if (shim_wait_cqe_timeout(acceptor._io_uring, &oneCqe, &ts) != 0) continue;
                        acceptor._cqes[0] = oneCqe;
                        got = 1;
                    }

                    for (int i = 0; i < got; i++) 
                    {
                        io_uring_cqe* cqe = acceptor._cqes[i];
                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Accept) 
                        {
                            if (res >= 0) {
                                int clientFd = res;
                                setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

                                // Round-robin to next reactor
                                // TODO: This is naive, not all connections are the same,
                                // TODO: should balance considering each connection's weight
                                // TODO: Allow user to inject balancing logic and provide multiple algorithms he can choose from
                                int targetReactor = nextReactor;
                                nextReactor = (nextReactor + 1) % reactorCount;

                                ReactorQueues[targetReactor].Enqueue(clientFd);
                                
                            }else { Console.WriteLine($"[acceptor] Accept error: {res}"); }
                        }
                        shim_cqe_seen(acceptor._io_uring, cqe);
                    }
                    if (shim_sq_ready(acceptor._io_uring) > 0) { Console.WriteLine("S3"); shim_submit(acceptor._io_uring); }
                }
            }
            finally 
            {
                // close listener and ring even on exception/StopAll
                if (acceptor._listenFd >= 0) close(acceptor._listenFd);
                if (acceptor._io_uring != null) shim_destroy_ring(acceptor._io_uring);
                Console.WriteLine($"[acceptor] Shutdown complete.");
            }
        }
    }
    
    private static io_uring* CreateRing(uint flags, int sqThreadCpu, uint sqThreadIdleMs, out int err, uint ringEntries) 
    {
        if(flags == 0)
            return shim_create_ring(ringEntries, out err);
        return shim_create_ring_ex(ringEntries, flags, sqThreadCpu, sqThreadIdleMs, out err);
    }

    // TODO: This seems to be causing segfault when sqe is null
    private static io_uring_sqe* SqeGet(io_uring* pring) 
    {
        io_uring_sqe* sqe = shim_get_sqe(pring);
        if (sqe == null) {
            Console.WriteLine("S4");
            shim_submit(pring); 
            sqe = shim_get_sqe(pring); 
        }
        return sqe;
    }

    private static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid) 
    {
        io_uring_sqe* sqe = SqeGet(pring);
        shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
    }
}