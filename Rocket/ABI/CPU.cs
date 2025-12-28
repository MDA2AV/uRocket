using System.Runtime.InteropServices;

namespace Rocket.ABI;

public static partial class ABI {
    // ------------------------------------------------------------------------------------
    //  CPU AFFINITY PINNING
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Helpers to pin the current Linux thread to a specific CPU core.
    /// <para>
    /// Useful for deterministic benchmarking or to reduce scheduler migrations.
    /// Non-fatal if pinning fails (the call is best-effort).
    /// </para>
    /// </summary>
    internal static class Affinity {
        private const long SYS_gettid = 186; // Linux gettid syscall number (x86_64)
        [DllImport("libc")] private static extern long syscall(long n);
        /// <summary>
        /// Sets the CPU affinity mask for a given thread id.
        /// </summary>
        [DllImport("libc")] private static extern int sched_setaffinity(int pid, nuint cpusetsize, byte[] mask);
        /// <summary>
        /// Pins the calling thread to <paramref name="cpu"/> (zero-based).
        /// <para>
        /// Builds a minimal CPU set and invokes <c>sched_setaffinity</c>. Errors are ignored intentionally.
        /// </para>
        /// </summary>
        public static void PinCurrentThreadToCpu(int cpu) {
            int tid   = (int)syscall(SYS_gettid);
            int bytes = (Environment.ProcessorCount + 7) / 8;
            var mask  = new byte[Math.Max(bytes, 8)]; // ensure minimal size for safety
            mask[cpu / 8] |= (byte)(1 << (cpu % 8));
            _ = sched_setaffinity(tid, (nuint)mask.Length, mask);
        }
    }
}