using System.Runtime.CompilerServices;
using System.Text;
using Rocket.Engine;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

[SkipLocalsInit]
internal static class Program
{
    internal static async Task Main()
    {
        var builder = RocketEngine
            .CreateBuilder()
            .SetWorkersSolver(() => 32)
            .SetBacklog(16 * 1024)
            .SetPort(8080)
            .SetRecvBufferSize(32 * 1024);
        
        var engine = builder.Build();
        _ = Task.Run(() => engine.Run());
        
        while (true)
        {
            var conn = await engine.AcceptAsync();
            Console.WriteLine($"Connection: {conn.Fd}");

            _ = HandleAsync(conn);
        }
    }
    
    internal static async ValueTask HandleAsync(Connection connection)
    {
        try
        {
            while (true)
            {
                // Read request
                await connection.ReadAsync();
                //connection.Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Tcs = new(); // reset tcs

                unsafe
                {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    var s = Encoding.UTF8.GetString(span);
                    //Console.WriteLine(s[0]);
                }

                // Flush response
                unsafe
                {
                    if (connection.HasBuffer)
                    {
                        var worker = RocketEngine.s_Workers[connection.WorkerIndex];
                        worker.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }

                    var okPtr = RocketEngine.OK_PTR;
                    var okLen = RocketEngine.OK_LEN;
                        
                    connection.OutPtr  = okPtr;
                    connection.OutHead = 0;
                    connection.OutTail = okLen;
                    connection.Sending = true;
                    
                    RocketEngine.SubmitSend(
                        RocketEngine.s_Workers[connection.WorkerIndex].PRing,
                        connection.Fd,
                        connection.OutPtr,
                        connection.OutHead,
                        connection.OutTail);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        Console.WriteLine("end");
    }
}