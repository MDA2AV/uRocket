using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Rocket.Engine;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

public sealed partial class RocketEngine {
    private static volatile bool StopAll = false;
    // Lock-free queues for passing accepted fds to workers
    private static ConcurrentQueue<int>[] WorkerQueues = null!;
    // Stats tracking
    private static long[] WorkerConnectionCounts = null!;
    private static long[] WorkerRequestCounts = null!;
    public struct ConnectionItem {
        public readonly int WorkerIndex;
        public readonly int ClientFd;
        public ConnectionItem(int workerIndex, int clientFd) {
            WorkerIndex = workerIndex;
            ClientFd = clientFd;
        }
    }

    private static readonly Channel<ConnectionItem> ConnectionQueues =
        Channel.CreateUnbounded<ConnectionItem>(new UnboundedChannelOptions());
    
    public async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default) {
        var item = await ConnectionQueues.Reader.ReadAsync(cancellationToken);
        return Connections[item.WorkerIndex][item.ClientFd];
    }
    public void Run() {
        Console.CancelKeyPress += (_, __) => StopAll = true;
        InitOk();

        // Create lock-free queues for fd distribution
        WorkerQueues = new ConcurrentQueue<int>[s_nWorkers];
        WorkerConnectionCounts = new long[s_nWorkers];
        WorkerRequestCounts = new long[s_nWorkers];
        
        s_Workers = new Worker[s_nWorkers];
        Connections = new Dictionary<int, Connection>[s_nWorkers];
        
        for (var i = 0; i < s_nWorkers; i++) {
            WorkerQueues[i] = new ConcurrentQueue<int>();
            WorkerConnectionCounts[i] = 0;
            WorkerRequestCounts[i] = 0;
            
            Connections[i] = new Dictionary<int, Connection>(1024);
            
            s_Workers[i] = new Worker(i);
            s_Workers[i].InitPRing();
        }
        
        var workerThreads = new Thread[s_nWorkers];
        for (int i = 0; i < s_nWorkers; i++) {
            int wi = i;
            workerThreads[i] = new Thread(() => {
                    try { WorkerLoop(wi); }
                    catch (Exception ex) { Console.Error.WriteLine($"[w{wi}] crash: {ex}"); }
                })
                { IsBackground = true, Name = $"uring-w{wi}" };
            workerThreads[i].Start();
        }
        
        //Thread.Sleep(100);
        Console.WriteLine($"Server started with {s_nWorkers} workers + 1 acceptor");
        
        try { AcceptorLoop(c_ip, s_port, s_nWorkers); }
        catch (Exception ex) { Console.Error.WriteLine($"[acceptor] crash: {ex}"); }
        
        foreach (var t in workerThreads) t.Join();
        FreeOk();
    }
}