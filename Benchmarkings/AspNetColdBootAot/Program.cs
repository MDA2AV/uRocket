using System.Diagnostics;
using System.Runtime.CompilerServices;

internal static class Boot
{
    internal static long StartTs;

    [ModuleInitializer]
    internal static void Init()
        => StartTs = Stopwatch.GetTimestamp();
}

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var app = builder.Build();
        app.MapGet("/", () => "Hello");
        await app.StartAsync();
        
        var elapsed = Stopwatch.GetElapsedTime(Boot.StartTs);
        Console.WriteLine($"Process-start → first response: {elapsed.TotalMilliseconds:F3} ms");
    }
}