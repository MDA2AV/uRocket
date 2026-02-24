---
title: Zero-Allocation Patterns
weight: 1
---

zerg is designed for allocation-free operation on the hot path. This guide explains the read/write patterns and their allocation characteristics.

## Reading with ReadOnlySpan

The simplest approach: iterate ring items one at a time using `TryGetRing` and process each as a `ReadOnlySpan<byte>`. Zero allocation.

```csharp
static async Task HandleConnectionAsync(Connection connection)
{
    while (true)
    {
        var result = await connection.ReadAsync();
        if (result.IsClosed) break;

        while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
        {
            ReadOnlySpan<byte> data = ring.AsSpan();

            // Parse and respond in-place -- no allocation
            ProcessRequest(connection, data);

            // Return buffer immediately
            connection.ReturnRing(ring.BufferId);
        }

        await connection.FlushAsync();
        connection.ResetRead();
    }
}
```

Key patterns:
- Use `TryGetRing` to iterate one buffer at a time
- Use `ring.AsSpan()` for zero-copy access to the data
- Return each buffer immediately after processing
- No arrays, lists, or sequences allocated

## Reading with ReadOnlySequence

When data spans multiple kernel buffers, use `GetAllSnapshotRingsAsUnmanagedMemory` to build a `ReadOnlySequence<byte>` for `SequenceReader<byte>` based parsing:

```csharp
static async Task HandleConnectionAsync(Connection connection)
{
    while (true)
    {
        var result = await connection.ReadAsync();
        if (result.IsClosed) break;

        // Get all received buffers as managed memory
        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

        // Build a ReadOnlySequence for multi-segment parsing
        ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();
        var reader = new SequenceReader<byte>(sequence);

        // Parse the request...
        ProcessSequence(connection, ref reader);

        // Return all buffers
        rings.ReturnRingBuffers(connection.Reactor);

        await connection.FlushAsync();
        connection.ResetRead();
    }
}
```

This allocates one `UnmanagedMemoryManager[]` array and one `RingSegment` per buffer, but the data itself is never copied.

## Writing Responses

### Direct Span Write (Zero Allocation)

```csharp
connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 13\r\n\r\nHello, World!"u8);
await connection.FlushAsync();
```

Using `u8` string literals produces compile-time constant UTF-8 data in the assembly's read-only section. No allocation.

### IBufferWriter (Zero Allocation)

Write directly into the connection's slab without intermediate buffers:

```csharp
Span<byte> span = connection.GetSpan(256);
int written = FormatResponse(span);
connection.Advance(written);
await connection.FlushAsync();
```

## Choosing a Read Pattern

| Scenario | Pattern | Allocation |
|----------|---------|------------|
| Single buffer, simple protocol | `TryGetRing()` + `AsSpan()` | None |
| Multi-buffer or complex parsing | `GetAllSnapshotRingsAsUnmanagedMemory()` + `ToReadOnlySequence()` | Array + segments |
| Need individual ring control | `TryGetRing()` loop | None |
| BCL compatibility | `ConnectionStream.ReadAsync()` | Copies to managed buffer |

## Allocation-Free Patterns Summary

| Pattern | Allocation? | When to Use |
|---------|-------------|-------------|
| `ring.AsSpan()` | None | Reading a single buffer in-place |
| `TryGetRing()` loop | None | Iterating buffers one at a time |
| `GetAllSnapshotRingsAsUnmanagedMemory()` | Array | When you need `ReadOnlySequence` for multi-segment parsing |
| `TryDynamicallyGetAllSnapshotRings()` | List (if data) | When ring count varies and you want `out` semantics |
| `connection.Write(span)` | None | Staging response bytes |
| `connection.GetSpan()` + `Advance()` | None | Direct-write via IBufferWriter |

## Real-World Example

The TechEmpower benchmark handler in the zerg repository demonstrates these patterns in a production HTTP server. See [Building an HTTP Server](../http-server/) for a walkthrough.
