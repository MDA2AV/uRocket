using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using URocket;
using URocket.Engine;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

namespace Overdrive;

[SkipLocalsInit]
internal static class Program {
    internal static async Task Main() {
        InitOk();
        var engine = new Engine();
        
        _ = Task.Run(() => engine.Run());

        try {
            while (true) {
                var conn = await engine.AcceptAsync();
                Console.WriteLine($"Connection: {conn.Fd}");

                _ = HandleAsync(conn);
            }
        }
        finally {
            FreeOk();
        }
    }
    
    private static async ValueTask HandleAsync(Connection connection) {
        try {
            var reactor = connection.Reactor;
            while (true) {
                await connection.ReadAsync();
                unsafe {
                    var span = new ReadOnlySpan<byte>(connection.InPtr, connection.InLength);
                    //var s = Encoding.UTF8.GetString(span);
                }
                
                unsafe {
                    if (connection.HasBuffer) {
                        reactor.ReturnBufferRing(connection.InPtr, connection.BufferId);
                    }
                }
                connection.ResetRead();
                unsafe {
                    connection.OutPtr  = OK_PTR;
                    connection.OutHead = 0;
                    connection.OutTail = OK_LEN;

                    // TODO: SubmitSend to reactor class?
                    Engine.SubmitSend(
                        reactor.Ring,
                        connection.Fd,
                        connection.OutPtr,
                        connection.OutHead,
                        connection.OutTail);
                }
            }
        }catch (Exception e) { Console.WriteLine(e); }
        Console.WriteLine("end");
    }
    
    public static unsafe byte* OK_PTR;
    public static nuint OK_LEN;

    private static unsafe void InitOk() {
        var s = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: keep-alive\r\nContent-Type: text/plain\r\n\r\nHello, World!";
        var a = Encoding.UTF8.GetBytes(s);
        OK_LEN = (nuint)a.Length;
        OK_PTR = (byte*)NativeMemory.Alloc(OK_LEN);
        for (int i = 0; i < a.Length; i++)
            OK_PTR[i] = a[i];
    }

    private static unsafe void FreeOk() {
        if (OK_PTR != null) {
            NativeMemory.Free(OK_PTR);
            OK_PTR = null;
            OK_LEN = 0;
        }
    }
}