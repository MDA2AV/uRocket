---
title: ConnectionStream
weight: 4
---

`ConnectionStream` is a thin `Stream` adapter that bridges zerg's native connection API to BCL pipeline APIs. It provides zero-copy writes and single-copy reads for compatibility with code that expects a `System.IO.Stream`.

## Class Definition

```csharp
public sealed class ConnectionStream : Stream
```

## Constructor

```csharp
public ConnectionStream(Connection inner)
```

Wraps an existing `Connection` in a `Stream` interface. The underlying connection must already be registered with a reactor.

## Supported Operations

| Property | Value | Description |
|----------|-------|-------------|
| `CanRead` | `true` | Reading is supported via reactor receive rings |
| `CanWrite` | `true` | Writing appends to the connection's unmanaged slab |
| `CanSeek` | `false` | Network streams are not seekable |

## Write Operations

### Write(byte[], int, int)

```csharp
public override void Write(byte[] buffer, int offset, int count)
```

Synchronous write. Validates parameters and copies the buffer slice into the connection's unmanaged write slab.

### WriteAsync(ReadOnlyMemory\<byte\>, CancellationToken)

```csharp
public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
```

Fast async write with no allocation and no implicit flush. Returns a completed `ValueTask` if the buffer is empty. Otherwise copies the data into the write slab synchronously and returns completed.

**Note:** This does not flush. Call `FlushAsync()` to send data.

## Flush

### FlushAsync(CancellationToken)

```csharp
public override Task FlushAsync(CancellationToken token)
```

Flushes all previously written data. Delegates to `connection.FlushAsync().AsTask()`. The reactor controls the actual send -- the returned `Task` completes when all staged bytes have been transmitted.

### Flush()

```csharp
public override void Flush()
```

**Throws `NotSupportedException`.** Synchronous flush is not supported. Use `FlushAsync()`.

## Read Operations

### ReadAsync(Memory\<byte\>, CancellationToken)

```csharp
public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
```

Reads from the reactor receive rings and copies into `destination`.

**Steps:**
1. Awaits the next receive snapshot from the reactor (`connection.ReadAsync()`)
2. Returns 0 (EOF) if the connection is closed
3. Gathers all segments belonging to the snapshot
4. Copies once into the caller's buffer via `CopyFromRings()`
5. Returns each ring buffer to the reactor pool
6. Calls `ResetRead()` to prepare for the next cycle
7. Returns the number of bytes copied

### ReadAsync(byte[], int, int, CancellationToken)

```csharp
public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
```

Legacy array-based read. Delegates to the `Memory<byte>` overload.

### Read(byte[], int, int)

```csharp
public override int Read(byte[] buffer, int offset, int count)
```

**Throws `NotSupportedException`.** Synchronous reads are not supported on an async reactor-driven connection.

## Unsupported Operations

| Method | Behavior |
|--------|----------|
| `Read(byte[], int, int)` | Throws `NotSupportedException` |
| `Flush()` | Throws `NotSupportedException` |
| `Seek(long, SeekOrigin)` | Throws `NotSupportedException` |
| `SetLength(long)` | Throws `NotSupportedException` |
| `Length` (property) | Throws `NotSupportedException` |
| `Position` (property) | Throws `NotSupportedException` |

## Disposal

```csharp
protected override void Dispose(bool disposing)
```

Idempotent disposal using an interlocked guard. Disposes the underlying connection. Safe to call multiple times.

## Usage Example

```csharp
static async Task HandleWithStreamAsync(Connection connection)
{
    await using var stream = new ConnectionStream(connection);

    var buffer = new byte[4096];
    int bytesRead;

    while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
    {
        // Process data in buffer[..bytesRead]

        // Echo back
        await stream.WriteAsync(buffer.AsMemory(0, bytesRead));
        await stream.FlushAsync();
    }
}
```

## When to Use ConnectionStream

Use `ConnectionStream` when you need to integrate with APIs that require `Stream`:

- `System.Text.Json` serialization/deserialization
- `StreamReader`/`StreamWriter` for text protocols
- Third-party libraries that accept `Stream`

For zero-copy reads with partial consumption support, prefer `ConnectionPipeReader` instead. For maximum performance, prefer the native `Connection` API directly (`ReadAsync`/`Write`/`FlushAsync`). `ConnectionStream` adds one copy on reads (from kernel buffers into your destination buffer) and wraps `ValueTask` as `Task` for `FlushAsync`.

## Design Notes

- **No internal buffering:** Reads pull directly from reactor rings; writes go directly to the slab
- **No synchronization:** The reactor provides exclusivity guarantees
- **Single copy on read:** Data is copied from kernel-provided buffers into the caller's buffer
- **Zero-copy on write:** Data is copied into the slab (same as native `Write()`)
