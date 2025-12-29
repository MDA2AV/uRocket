using System.Text;
using URocket.Engine.Builder;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine {
    public Acceptor SingleAcceptor { get; set; } = null!;
    public class Acceptor {
        private io_uring_sqe* _sqe;
        private readonly AcceptorConfig _config;
        internal io_uring_cqe*[] Cqes { get; }
        public io_uring* Ring { get; private set; }
        public int ListenFd { get; }

        public Acceptor() : this(new  AcceptorConfig()) { }

        public Acceptor(AcceptorConfig config) {
            _config = config; 
            ListenFd = CreateListenerSocket(c_ip, s_port); 
            Cqes = new io_uring_cqe*[_config.BatchSqes];
        }

        public void InitRing() {
            Ring = CreateRing(_config.RingFlags, _config.SqCpuThread, _config.SqThreadIdleMs, out int err, _config.RingEntries);
            CheckRingFlags(shim_get_ring_flags(Ring));
            if (Ring == null || err < 0) { Console.Error.WriteLine($"[acceptor] create_ring failed: {err}"); return; }
            // Start multishot accept
            _sqe = SqeGet(Ring);
            shim_prep_multishot_accept(_sqe, ListenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(_sqe, PackUd(UdKind.Accept, ListenFd));
            shim_submit(Ring);
            Console.WriteLine("[acceptor] Multishot accept armed");
        }

        private void CheckRingFlags(uint flags) {
            Console.WriteLine($"[acceptor] ring flags = 0x{flags:x} " +
                              $"(SQPOLL={(flags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(flags & IORING_SETUP_SQ_AFF) != 0})");
        }
    }
    
    private static io_uring* CreateRing(uint flags, int sqThreadCpu, uint sqThreadIdleMs, out int err, uint ringEntries) {
        if(flags == 0)
            return shim_create_ring(ringEntries, out err);
        return shim_create_ring_ex(ringEntries, flags, sqThreadCpu, sqThreadIdleMs, out err);
    }

    // TODO: This seems to be causing Segmentation fault (core dumped) when sqe is null
    private static io_uring_sqe* SqeGet(io_uring* pring) {
        io_uring_sqe* sqe = shim_get_sqe(pring);
        if (sqe == null) {
            Console.WriteLine("S4");
            shim_submit(pring); 
            sqe = shim_get_sqe(pring); 
        }
        return sqe;
    }

    public static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len) {
        io_uring_sqe* sqe = SqeGet(pring);
        shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
    }

    private static void ArmRecvMultishot(io_uring* pring, int fd, uint bgid) {
        io_uring_sqe* sqe = SqeGet(pring);
        shim_prep_recv_multishot_select(sqe, fd, bgid, 0);
        shim_sqe_set_data64(sqe, PackUd(UdKind.Recv, fd));
    }
    
    private static int CreateListenerSocket(string ip, ushort port) {
        int lfd = socket(AF_INET, SOCK_STREAM, 0);
        int one = 1;

        setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int));
        setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family = (ushort)AF_INET;
        addr.sin_port = Htons(port);

        byte[] ipb = Encoding.UTF8.GetBytes(ip + "\0");
        fixed (byte* pip = ipb) inet_pton(AF_INET, (sbyte*)pip, &addr.sin_addr);

        bind(lfd, &addr, (uint)sizeof(sockaddr_in));
        listen(lfd, s_backlog);

        int fl = fcntl(lfd, F_GETFL, 0);
        fcntl(lfd, F_SETFL, fl | O_NONBLOCK);

        return lfd;
    }
}