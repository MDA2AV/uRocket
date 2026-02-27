using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Xunit;
using zerg;
using zerg.Engine.Configs;
using zerg.Utils.UnmanagedMemoryManager;

namespace Tests;

/// <summary>
/// Runs E2E tests with IncrementalBufferConsumption enabled.
/// These mirror the core tests from EndToEndTests, StreamTests, and PipeReaderTests
/// to verify the feature works transparently across all APIs.
/// </summary>
public class IncrementalBufferTests
{
    private static readonly ReactorConfig IncrementalConfig = new(
        IncrementalBufferConsumption: true
    );

    // ========================================================================
    // Low-level API (ReadAsync + RingItem)
    // ========================================================================

    [Fact]
    public async Task Incremental_Echo_SingleMessage()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, incremental!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);

        Assert.Equal(sent, buf.AsSpan(0, n).ToArray());
    }

    [Fact]
    public async Task Incremental_Echo_MultipleMessages()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        for (int i = 0; i < 10; i++)
        {
            var sent = Encoding.UTF8.GetBytes($"inc-msg-{i}");
            await stream.WriteAsync(sent);
            await stream.FlushAsync();

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"inc-msg-{i}", Encoding.UTF8.GetString(buf, 0, n));
        }
    }

    [Fact]
    public async Task Incremental_Echo_ConcurrentConnections()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 2, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            var sent = Encoding.UTF8.GetBytes($"inc-conn-{i}");
            await stream.WriteAsync(sent);

            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            Assert.Equal($"inc-conn-{i}", Encoding.UTF8.GetString(buf, 0, n));
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Incremental_Echo_LargePayload()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = new byte[4096];
        Random.Shared.NextBytes(sent);
        await stream.WriteAsync(sent);

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        while (ms.Length < sent.Length)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }

        Assert.Equal(sent, ms.ToArray());
    }

    // ========================================================================
    // ConnectionStream API
    // ========================================================================

    [Fact]
    public async Task Incremental_Stream_Echo()
    {
        await using var server = new ZergTestServer(StreamEchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, incremental stream!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);

        Assert.Equal(sent, buf.AsSpan(0, n).ToArray());
    }

    // ========================================================================
    // ConnectionPipeReader API
    // ========================================================================

    [Fact]
    public async Task Incremental_PipeReader_Echo()
    {
        await using var server = new ZergTestServer(PipeReaderEchoHandler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        var sent = "Hello, incremental pipe!"u8.ToArray();
        await stream.WriteAsync(sent);

        var buf = new byte[1024];
        var n = await stream.ReadAsync(buf);

        Assert.Equal(sent, buf.AsSpan(0, n).ToArray());
    }

    // ========================================================================
    // Client disconnect
    // ========================================================================

    [Fact]
    public async Task Incremental_HandlesClientDisconnect()
    {
        var connectionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(Connection connection)
        {
            try
            {
                while (true)
                {
                    var result = await connection.ReadAsync();
                    if (result.IsClosed)
                    {
                        connectionClosed.TrySetResult();
                        break;
                    }

                    var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
                    foreach (var ring in rings)
                        connection.ReturnRing(ring.BufferId);
                    connection.ResetRead();
                }
            }
            catch
            {
                connectionClosed.TrySetResult();
            }
        }

        await using var server = new ZergTestServer(Handler, reactorConfig: IncrementalConfig);
        await Task.Delay(100);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync("hello"u8.ToArray());
            await Task.Delay(50);
        }

        var completed = await Task.WhenAny(connectionClosed.Task, Task.Delay(5000));
        Assert.Equal(connectionClosed.Task, completed);
    }

    // ========================================================================
    // Multi-reactor load
    // ========================================================================

    [Fact]
    public async Task Incremental_MultipleReactors_ConcurrentLoad()
    {
        await using var server = new ZergTestServer(EchoHandler, reactorCount: 4, reactorConfig: IncrementalConfig);
        await Task.Delay(150);

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            var stream = client.GetStream();

            for (int j = 0; j < 5; j++)
            {
                var msg = Encoding.UTF8.GetBytes($"inc-r{i}-m{j}");
                await stream.WriteAsync(msg);

                var buf = new byte[1024];
                var n = await stream.ReadAsync(buf);
                Assert.Equal($"inc-r{i}-m{j}", Encoding.UTF8.GetString(buf, 0, n));
            }
        });

        await Task.WhenAll(tasks);
    }

    // ========================================================================
    // Handlers
    // ========================================================================

    private static async Task EchoHandler(Connection connection)
    {
        try
        {
            while (true)
            {
                var result = await connection.ReadAsync();
                if (result.IsClosed) break;

                var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

                unsafe
                {
                    foreach (var ring in rings)
                    {
                        var span = new ReadOnlySpan<byte>(ring.Ptr, ring.Length);
                        connection.Write(span);
                        connection.ReturnRing(ring.BufferId);
                    }
                }

                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        catch { /* connection gone */ }
    }

    private static async Task StreamEchoHandler(Connection connection)
    {
        try
        {
            var stream = new ConnectionStream(connection);
            var buf = new byte[4096];

            while (true)
            {
                var n = await stream.ReadAsync(buf);
                if (n == 0) break;

                await stream.WriteAsync(buf.AsMemory(0, n));
                await stream.FlushAsync();
            }
        }
        catch { /* connection gone */ }
    }

    private static async Task PipeReaderEchoHandler(Connection connection)
    {
        try
        {
            var reader = new ConnectionPipeReader(connection);

            while (true)
            {
                var result = await reader.ReadAsync();
                if (result.IsCompleted) break;

                var buffer = result.Buffer;

                foreach (var segment in buffer)
                    connection.Write(segment.Span);

                reader.AdvanceTo(buffer.End);
                await connection.FlushAsync();
            }

            reader.Complete();
        }
        catch { /* connection gone */ }
    }
}
