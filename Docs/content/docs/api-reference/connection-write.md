---
title: "Connection: Write"
weight: 3
---

The write API provides a staged write buffer and async flush to the kernel. Data is written into an unmanaged slab, then flushed via `io_uring` send operations.

## Write/Flush Cycle

```csharp
// 1. Stage data into the write buffer
connection.Write("HTTP/1.1 200 OK\r\n"u8);
connection.Write("Content-Length: 13\r\n\r\n"u8);
connection.Write("Hello, World!"u8);

// 2. Flush staged data to kernel
await connection.FlushAsync();

// Write buffer is automatically reset after flush completes
```

## Write Methods

### Write(ReadOnlySpan\<byte\>)

```csharp
public void Write(ReadOnlySpan<byte> source)
```

Copies the span into the unmanaged write slab at the current tail position.

**Throws:**
- `InvalidOperationException` if a flush is in progress
- `InvalidOperationException` if the slab doesn't have enough space

### Write(ReadOnlyMemory\<byte\>)

```csharp
public void Write(ReadOnlyMemory<byte> source)
```

Same behavior as the span overload, using `source.Span`.

### Write(byte\*, int) (Unsafe)

```csharp
public void Write(byte* ptr, int length)
```

Copies from unmanaged memory into the write slab. Uses `Buffer.MemoryCopy` for native-to-native copy.

## IBufferWriter\<byte\>

The `Connection` class implements `IBufferWriter<byte>`, allowing direct writes into the slab without copying:

### GetSpan

```csharp
public Span<byte> GetSpan(int sizeHint = 0)
```

Returns a writable `Span<byte>` over the remaining slab space. Write directly into this span, then call `Advance()`.

```csharp
Span<byte> span = connection.GetSpan(256);
int written = Encoding.UTF8.GetBytes("Hello", span);
connection.Advance(written);
```

### GetMemory

```csharp
public Memory<byte> GetMemory(int sizeHint = 0)
```

Returns a writable `Memory<byte>` over the remaining slab space. Useful for APIs that require `Memory<byte>`.

### Advance

```csharp
public void Advance(int count)
```

Advances the write tail by `count` bytes. Call after writing into the span/memory returned by `GetSpan`/`GetMemory`.

**Throws:**
- `InvalidOperationException` if a flush is in progress

## FlushAsync

```csharp
public ValueTask FlushAsync()
```

Arms a flush and returns a `ValueTask` that completes when the reactor has sent all staged bytes.

**Behavior:**

1. Captures `WriteTail` as the flush target (`WriteInFlight`)
2. Sets `_flushInProgress = 1` (blocks further writes)
3. Enqueues the connection's fd to the reactor's flush queue
4. The reactor issues a `send` SQE with the staged bytes
5. If partial send, the reactor resubmits for the remaining bytes
6. When fully sent, the reactor calls `CompleteFlush()` which:
   - Clears `_flushInProgress`
   - Resets `WriteHead` and `WriteTail` to 0
   - Completes the `ValueTask`

**Fast path:** Returns `default(ValueTask)` (completed) if nothing was written (`WriteTail == 0`).

**Throws:**
- `InvalidOperationException` if a flush is already in progress
- `InvalidOperationException` if a flush is already armed

## Write Buffer Internals

### Slab Layout

```
┌──────────────────────────────────────────┐
│           Unmanaged Write Slab            │
│  (64-byte aligned, default 16 KB)        │
│                                          │
│  ┌─────────┬──────────────┬──────────┐   │
│  │  Sent   │  Staged data │   Free   │   │
│  │ (Head)  │              │  space   │   │
│  └─────────┴──────────────┴──────────┘   │
│  ^WriteHead  ^WriteTail                   │
└──────────────────────────────────────────┘
```

| Field | Description |
|-------|-------------|
| `WriteBuffer` | Pointer to the start of the 64-byte aligned slab |
| `WriteHead` | Start of valid staged data (currently always 0) |
| `WriteTail` | End of written data -- advanced by `Write()` and `Advance()` |
| `WriteInFlight` | Snapshot of `WriteTail` captured by `FlushAsync()` |
| `SendInflight` | Reactor-owned flag: 1 if a send SQE is outstanding |

### Write Slab Size

The default write slab is **16 KB** per connection, allocated as 64-byte aligned unmanaged memory:

```csharp
var connection = new Connection(writeSlabSize: 1024 * 16);
```

If your responses are larger than 16 KB, increase this value. The entire response must fit in the slab before flushing.

## Flush Completion

The reactor handles the flush-to-kernel process:

1. **Drain flush queue:** `flushQ.TryDequeue(out clientFd)`
2. **Prepare send:** `shim_prep_send(sqe, fd, writeBuffer + writeHead, writeInFlight - writeHead, 0)`
3. **Submit and process CQE:**
   - `cqe->res` = bytes sent
   - Advance `WriteHead` by bytes sent
   - If `WriteHead < WriteInFlight`: resubmit for remaining bytes
   - If complete: call `CompleteFlush()` on the connection

```
Handler                Reactor
   │                      │
   │── Write(data) ──────▶│
   │── Write(data) ──────▶│
   │── FlushAsync() ─────▶│ enqueue fd to flushQ
   │   (awaiting)         │
   │                      │── drain flushQ
   │                      │── prep_send(fd, buf, len)
   │                      │── submit
   │                      │   ...
   │                      │── CQE: send complete
   │◀── CompleteFlush() ──│ resume ValueTask
   │                      │
   │── Write(next) ──────▶│ slab reset, ready for next cycle
```

## Thread Safety

- `Write()`, `GetSpan()`, `Advance()` must be called from a single thread (the handler)
- `FlushAsync()` enqueues to an MPSC queue (safe from any thread)
- `CompleteFlush()` is called by the reactor thread, which may resume the handler inline
- The `_flushInProgress` flag prevents writes during an active flush
