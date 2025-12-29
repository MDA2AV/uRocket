// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed partial class Engine {
    private const int c_bufferRingGID = 1;
    
    // Socket
    private const string c_ip = "0.0.0.0";
    private static ushort s_port = 8080;
    private static int s_backlog = 65535;
    
    // Reactor
    private static int s_nReactors;
    private static Func<int>? s_calculateNumberReactors;
    
    public static RocketBuilder CreateBuilder() => new RocketBuilder();
    public sealed class RocketBuilder {
        private readonly Engine _engine;
        public RocketBuilder() => _engine = new Engine();
        public Engine Build() { s_nReactors = s_calculateNumberReactors?.Invoke() ?? Environment.ProcessorCount / 2; return _engine; }
        public RocketBuilder Backlog(int backlog) { s_backlog = backlog; return this; }
        public RocketBuilder Port(ushort port) { s_port = port; return this; }
        public RocketBuilder ReactorQuant(Func<int>? calculateNumberReactors) { s_calculateNumberReactors = calculateNumberReactors; return this; }
    }
}