using Examples.PipeReader;
using Examples.Stream;
using Examples.ZeroAlloc.Basic;
using zerg;
using zerg.Engine;
using zerg.Engine.Configs;

namespace Examples;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

internal class Program
{
    public static async Task Main(string[] args)
    {
        // Similar to Sockets, create an object and initialize it
        // By default set to IPv4 TCP
        // (More examples on how to configure the engine coming up)
        var engine = new Engine(new EngineOptions
        {
            Port = 8080,
            ReactorCount = 12
        });
        engine.Listen();

        var cts = new CancellationTokenSource();
        _ = Task.Run(() => {
            Console.ReadLine();
            engine.Stop();
            cts.Cancel();
        }, cts.Token);

        // Pick the handler to benchmark:
        //   "raw"        — zero-copy, manual ring management (fastest)
        //   "pipereader"  — zero-copy via PipeReader adapter
        //   "stream"      — copy-per-read via Stream adapter
        var mode = args.Length > 0 ? args[0] : "pipereader";

        Func<Connection, Task> handler = mode switch
        {
            "raw"        => Rings_as_ReadOnlySpan.HandleConnectionAsync,
            "pipereader" => PipeReaderExample.HandleConnectionAsync,
            "stream"     => StreamExample.HandleConnectionAsync,
            _            => PipeReaderExample.HandleConnectionAsync,
        };

        Console.WriteLine($"Running with handler: {mode}");

        try
        {
            // Loop to handle new connections, fire and forget approach
            while (engine.ServerRunning)
            {
                var connection = await engine.AcceptAsync(cts.Token);
                if (connection is null) continue;
                _ = handler(connection);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Signaled to stop");
        }

        cts.Dispose();
        Console.WriteLine("Main loop finished.");
    }
}
