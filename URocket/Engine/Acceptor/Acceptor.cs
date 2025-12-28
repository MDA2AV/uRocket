using URocket.Engine.Builder;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class RocketEngine {
    public class Acceptor {
        public io_uring* Ring;
        public AcceptorConfig Config { get; }
        public int ListenFd { get; }

        public Acceptor() : this(new  AcceptorConfig()) { }
        public Acceptor(AcceptorConfig config) { Config = config; ListenFd = CreateListen(c_ip, s_port); }

        public void InitRing() {
            Ring = CreatePRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, out int err, Config.RingEntries);
            uint ringFlags = shim_get_ring_flags(Ring);
            Console.WriteLine($"[acceptor] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");
            if (Ring == null || err < 0) { Console.Error.WriteLine($"[acceptor] create_ring failed: {err}"); return; }
            // Start multishot accept
            io_uring_sqe* sqe = SqeGet(Ring);
            shim_prep_multishot_accept(sqe, ListenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Accept, ListenFd));
            shim_submit(Ring);
            Console.WriteLine("[acceptor] Multishot accept armed");
        }
    }
}