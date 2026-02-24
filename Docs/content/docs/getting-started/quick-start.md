---
title: Quick Start
weight: 2
---

This guide shows you how to create a minimal TCP server with zerg, accept connections, read data, and send a response.

## Minimal Server

```csharp
using zerg.Engine;
using zerg.Engine.Configs;

// 1. Create the engine with configuration
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = Environment.ProcessorCount
});

// 2. Start listening -- spawns acceptor + reactor threads
engine.Listen();

// 3. Graceful shutdown on Enter key
var cts = new CancellationTokenSource();
_ = Task.Run(() =>
{
    Console.ReadLine();
    engine.Stop();
    cts.Cancel();
});

// 4. Accept loop
try
{
    while (engine.ServerRunning)
    {
        var connection = await engine.AcceptAsync(cts.Token);
        if (connection is null) continue;

        // Fire-and-forget handler per connection
        _ = HandleConnectionAsync(connection);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}
```

## Connection Handler

Each accepted connection gives you a `Connection` object. The read/write cycle follows this pattern:

1. **ReadAsync** -- wait for inbound data
2. **Process** -- inspect received buffers
3. **ReturnRing** -- return kernel buffers after processing
4. **Write** -- stage response bytes
5. **FlushAsync** -- send staged bytes to the kernel
6. **ResetRead** -- prepare for the next read cycle

```csharp
static async Task HandleConnectionAsync(Connection connection)
{
    while (true)
    {
        // Wait for data from the client
        var result = await connection.ReadAsync();
        if (result.IsClosed)
            break;

        // Get all received buffers as managed memory
        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

        // Build a ReadOnlySequence for parsing
        ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();

        // ... parse the request from sequence ...

        // Return buffers to the kernel buffer ring
        rings.ReturnRingBuffers(connection.Reactor);

        // Write a response
        connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8);

        // Flush to kernel and wait for send completion
        await connection.FlushAsync();

        // Ready for the next read
        connection.ResetRead();
    }
}
```

## What Happens Under the Hood

When you call `engine.Listen()`:

1. An **acceptor thread** starts with its own `io_uring` instance and arms multishot accept on the listening socket
2. **N reactor threads** start, each with their own `io_uring` instance and pre-allocated buffer ring
3. As clients connect, the acceptor distributes file descriptors to reactors in round-robin order
4. Each reactor arms multishot recv with buffer selection for its connections
5. `AcceptAsync` returns connections as they are fully registered in a reactor

When you call `connection.ReadAsync()`:

1. If data is already queued in the connection's SPSC ring, it returns immediately
2. Otherwise, the calling task parks until the reactor delivers data via a CQE completion
3. The returned `ReadResult` contains a snapshot boundary so you drain exactly the data that was available at that point

When you call `connection.FlushAsync()`:

1. The write tail is captured as the flush target
2. The connection is enqueued to the reactor's flush queue
3. The reactor issues a `send` SQE and handles partial sends automatically
4. The `ValueTask` completes when all staged bytes have been sent

## Next Steps

- [Configuration](../configuration/) -- tune buffer sizes, reactor count, and ring flags
- [Architecture](../../architecture/reactor-pattern/) -- understand the acceptor + reactor model
- [Zero-Allocation Guide](../../guides/zero-allocation/) -- allocation-free read and write patterns
