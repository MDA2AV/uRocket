// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace URocket.Engine;

public sealed partial class Engine {
    
    private const int c_bufferRingGID = 1;
    private int _nReactors;
    
    public bool ServerRunning { get; private set; }
    
    // Lock-free queues for passing accepted fds to reactors
    private static ConcurrentQueue<int>[] ReactorQueues = null!; // TODO: Use Channels?
    // Stats tracking
    private static long[] ReactorConnectionCounts = null!;
    private static long[] ReactorRequestCounts = null!;
    
    // Socket
    public string Ip { get; private set; } = "0.0.0.0";
    public ushort Port { get; private set; } = 8080;
    public int Backlog { get; private set; } = 65535;
    
    
    private readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());
    
    public async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default) {
        var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken);
        return Connections[item.ReactorId][item.ClientFd];
    }
    
    public Engine() {
        _nReactors = 16;

        ReactorQueues = new ConcurrentQueue<int>[_nReactors];
        ReactorConnectionCounts = new long[_nReactors];
        ReactorRequestCounts = new long[_nReactors];
    }
    
    public struct ConnectionItem {
        public readonly int ReactorId;
        public readonly int ClientFd;
        public ConnectionItem(int reactorId, int clientFd) {
            ReactorId = reactorId;
            ClientFd = clientFd;
        }
    }
    
    public void Listen() {
        ServerRunning = true;
        // Init Acceptor
        SingleAcceptor = new Acceptor(this); // TODO: How to pass a config
        SingleAcceptor.InitRing();
        
        // Init Reactors
        Reactors = new Reactor[_nReactors];
        Connections = new Dictionary<int, Connection>[_nReactors];
        for (var i = 0; i < _nReactors; i++) {
            ReactorQueues[i] = new ConcurrentQueue<int>();
            ReactorConnectionCounts[i] = 0;
            ReactorRequestCounts[i] = 0;
            
            Reactors[i] = new Reactor(i,this); // TODO: How to pass a config
            Reactors[i].InitRing();
            Connections[i] = new Dictionary<int, Connection>(Reactors[i].Config.MaxConnectionsPerReactor);
        }
        
        var reactorThreads = new Thread[_nReactors];
        for (int i = 0; i < _nReactors; i++) {
            int wi = i;
            reactorThreads[i] = new Thread(() => {
                try { Reactors[wi].Handle(); }
                catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
            })
            { IsBackground = true, Name = $"uring-w{wi}" };
            reactorThreads[i].Start();
        }

        var acceptorThread = new Thread(() => {
            try { SingleAcceptor.Handle(SingleAcceptor, _nReactors); }
            catch (Exception ex) { Console.Error.WriteLine($"[acceptor] crash: {ex}"); }
        });
        acceptorThread.Start();
        Console.WriteLine($"Server started with {_nReactors} reactors + 1 acceptor");
    }
    
    public void Stop() => ServerRunning = false;
}