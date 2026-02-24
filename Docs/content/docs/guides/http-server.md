---
title: Building an HTTP Server
weight: 2
---

This guide walks through building an HTTP/1.1 server on zerg, based on the TechEmpower benchmark implementation included in the repository.

## Server Setup

```csharp
using zerg.Engine;
using zerg.Engine.Configs;

var engine = new Engine(new EngineOptions
{
    Ip = "0.0.0.0",
    Port = 8080,
    ReactorCount = 12
});

engine.Listen();

while (engine.ServerRunning)
{
    var connection = await engine.AcceptAsync();
    if (connection is null) continue;
    _ = new ConnectionHandler().HandleConnectionAsync(connection);
}
```

Each connection gets its own `ConnectionHandler` instance which manages per-connection state.

## Request Parsing

HTTP/1.1 requests are delimited by `\r\n\r\n`. The core parsing strategy:

1. Search for the `\r\n\r\n` delimiter in received data
2. If found: extract method, path, headers, and process
3. If not found: buffer data and wait for more

### Single-Buffer Fast Path

Most HTTP requests fit in a single kernel buffer (< 32 KB with default `RecvBufferSize`):

```csharp
private bool HandleSingleRing(Connection connection, ReadOnlySpan<byte> data)
{
    int totalAdvanced = 0;
    bool flushable = false;

    while (totalAdvanced < data.Length)
    {
        var remaining = data[totalAdvanced..];
        int endOfHeaders = remaining.IndexOf("\r\n\r\n"u8);

        if (endOfHeaders < 0)
            break; // incomplete request

        var request = remaining[..(endOfHeaders + 4)];
        ProcessHttpRequest(connection, request);
        totalAdvanced += endOfHeaders + 4;
        flushable = true;
    }

    return flushable;
}
```

### Pipelined Requests

HTTP/1.1 allows multiple requests in a single TCP segment (pipelining). The `while` loop above handles this naturally -- it processes each request until no more complete requests remain.

## Response Generation

### Static Response

For a plaintext response:

```csharp
private static ReadOnlySpan<byte> PlaintextResponse =>
    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 13\r\n\r\nHello, World!"u8;

private void HandlePlaintext(Connection connection)
{
    connection.Write(PlaintextResponse);
}
```

Using `u8` string literals (UTF-8) avoids allocation entirely -- the data lives in the assembly's read-only data section.

### Dynamic Response with IBufferWriter

For dynamic content, use the `IBufferWriter<byte>` interface:

```csharp
private void HandleJson(Connection connection)
{
    // Write headers
    connection.Write("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"u8);

    // Write body using Utf8JsonWriter
    var span = connection.GetSpan(256);

    // Reserve space for Content-Length header (write it after body)
    connection.Write("Content-Length:     \r\n\r\n"u8);
    int bodyStart = /* track position */;

    var writer = new Utf8JsonWriter(/* buffer */);
    writer.WriteStartObject();
    writer.WriteString("message", "Hello, World!");
    writer.WriteEndObject();
    writer.Flush();

    // Patch Content-Length with actual body size
    // ...

    connection.Advance(writer.BytesCommitted);
}
```

### Date Header Caching

HTTP responses require a `Date` header. Generating this per-request is wasteful. Cache it with a timer:

```csharp
internal static class DateHelper
{
    private static byte[] _headerBytes;
    private static readonly Timer STimer = new(_ =>
    {
        SetDateValues(DateTimeOffset.UtcNow);
    }, null, 1000, 1000);

    public static ReadOnlySpan<byte> HeaderBytes => _headerBytes;

    private static void SetDateValues(DateTimeOffset now)
    {
        // Format: "Date: Thu, 01 Jan 2025 00:00:00 GMT\r\n"
        // Update _headerBytes atomically
    }
}
```

The date header is updated once per second and served as a pre-formatted `ReadOnlySpan<byte>`.

## Connection Handler Pattern

A basic connection handler using the read/write cycle:

```csharp
internal sealed class ConnectionHandler
{
    internal async Task HandleConnectionAsync(Connection connection)
    {
        while (true)
        {
            var result = await connection.ReadAsync();
            if (result.IsClosed) break;

            var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
            var sequence = rings.ToReadOnlySequence();

            bool flushable = ProcessRequests(connection, sequence);

            rings.ReturnRingBuffers(connection.Reactor);

            if (flushable)
                await connection.FlushAsync();

            connection.ResetRead();
        }
    }
}
```

## Performance Considerations

1. **UTF-8 literals** (`u8` suffix) -- compile-time constant, zero allocation
2. **Single flush per batch** -- batch all responses before flushing
3. **Return buffers early** -- don't hold kernel buffers longer than needed
4. **Pipelined processing** -- handle multiple requests per read when available
5. **Date caching** -- format the Date header once per second, not per request
