namespace Rocket.Engine;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

// TODO Organize this, separte socket, acceptor and reactor vars, remove builder pattern?
public sealed partial class RocketEngine {
    private const int c_bufferRingGID = 1;
    
    // Socket
    private const string c_ip = "0.0.0.0";
    private static ushort s_port = 8080;
    private static int s_backlog = 65535;
    
    // Reactor
    private static uint PRingFlags = 0;
    private static int sqThreadCpu = -1;
    private static uint sqThreadIdleMs = 100;
    private static int s_reactorRingEntries =  8 * 1024;
    private static int s_reactorRecvBufferSize  = 32 * 1024;
    private static int s_reactorBufferRingEntries = 16 * 1024;     // power-of-two
    private static int s_reactorBatchCQES = 4096;
    private static int s_nReactors;
    private static int s_maxConnectionsPerReactor = 8 * 1024;
    private static Func<int>? s_calculateNumberReactors;
    
    // Acceptor
    private static uint s_acceptorFlags = 0;             
    private static int s_acceptorSqThreadCpu = -1;       
    private static uint s_acceptorSqThreadIdleMs = 100;  
    private static uint s_acceptorRingEntries = 256;     
    
    public static RocketBuilder CreateBuilder() => new RocketBuilder();
    public sealed class RocketBuilder {
        private readonly RocketEngine _engine;
        public RocketBuilder() => _engine = new RocketEngine();
        public RocketEngine Build() { s_nReactors = s_calculateNumberReactors?.Invoke() ?? Environment.ProcessorCount / 2; return _engine; }
        public RocketBuilder Backlog(int backlog) { s_backlog = backlog; return this; }
        public RocketBuilder Port(ushort port) { s_port = port; return this; }
        public RocketBuilder SetRingEntries(int ringEntries) { s_reactorRingEntries = ringEntries; return this; }
        public RocketBuilder SetBufferRingEntries(int bufferRingEntries) { s_reactorBufferRingEntries = bufferRingEntries; return this; }
        public RocketBuilder BatchCQES(int batchCQES) { s_reactorBatchCQES = batchCQES; return this; }
        public RocketBuilder RecvBufferSize(int recvBufferSize) { s_reactorRecvBufferSize = recvBufferSize; return this; }
        public RocketBuilder ReactorQuant(Func<int>? calculateNumberReactors) { s_calculateNumberReactors = calculateNumberReactors; return this; }
    }
}