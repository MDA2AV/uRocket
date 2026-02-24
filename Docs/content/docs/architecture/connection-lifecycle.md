---
title: Connection Lifecycle
weight: 4
---

A connection in zerg goes through a well-defined lifecycle: accept, register, use, close, and optionally return to pool.

## Lifecycle Stages

```
                    Accept (Acceptor)
                         │
                         ▼
                  ┌──────────────┐
                  │  Distribute  │  round-robin to reactor
                  │  via queue   │
                  └──────┬───────┘
                         │
                         ▼
                  ┌──────────────┐
                  │   Register   │  arm multishot recv
                  │  in reactor  │  add to connections dict
                  └──────┬───────┘
                         │
                         ▼
                  ┌──────────────┐
                  │   Notify     │  push to Channel
                  │  application │  AcceptAsync() returns
                  └──────┬───────┘
                         │
                         ▼
                  ┌──────────────┐
              ┌──▶│  ReadAsync   │◀─┐
              │   │  + process   │  │  read/write loop
              │   │  + Write     │  │
              │   │  + FlushAsync│──┘
              │   └──────┬───────┘
              │          │
              │          ▼ (IsClosed or error)
              │   ┌──────────────┐
              │   │    Close     │  connection removed
              │   │   + cleanup  │  from reactor dict
              │   └──────┬───────┘
              │          │
              │          ▼
              │   ┌──────────────┐
              └───│  Pool/Reuse  │  Clear(), return to pool
                  │  (optional)  │  generation incremented
                  └──────────────┘
```

## Accept Phase

1. The acceptor's `io_uring` delivers a CQE with the new client fd
2. `TCP_NODELAY` is set on the socket
3. The fd is enqueued to the target reactor's `ConcurrentQueue<int>`

## Registration Phase

On its next loop iteration, the reactor:

1. Dequeues the fd from its queue
2. Creates or retrieves a `Connection` from the pool
3. Calls `connection.SetFd(clientFd).SetReactor(this)` which:
   - Assigns the file descriptor
   - Clears the `_closed` flag
   - Resets `_pending` and `_armed` flags
   - Resets the `_readSignal` completion source
   - Clears the SPSC receive ring
4. Stores the connection in `connections[clientFd]`
5. Arms multishot recv with buffer selection for the fd
6. Pushes a `ConnectionItem` to the `Channel<ConnectionItem>` for `AcceptAsync()`

## Active Phase

The connection is now active. The handler can:

- **ReadAsync()** -- park until data arrives, then drain ring items
- **Write()** -- stage bytes into the unmanaged write slab
- **FlushAsync()** -- tell the reactor to send staged bytes
- **ResetRead()** -- prepare for the next read cycle

See [Connection Read](../../api-reference/connection-read/) and [Connection Write](../../api-reference/connection-write/) for API details.

## Close Phase

A connection closes when:

- **Client disconnects**: recv CQE arrives with `res == 0` (EOF) or `res < 0` (error)
- **Ring overflow**: the SPSC recv ring is full (1024 items) -- the connection is force-closed as a safety measure
- **Application closes**: the handler exits the read loop

When the reactor detects a close (recv CQE with `res <= 0`):

1. Returns any buffer used by the final CQE to the buffer ring
2. Removes the connection from the reactor's `connections` dictionary
3. Marks the connection as closed (`_closed = 1`)
4. Wakes any waiting `ReadAsync()` so the handler sees `IsClosed == true`

## Pooling and Reuse

Connections can be pooled to avoid repeated allocation. The `Connection` class supports two reset methods:

### `Clear()` -- Safe Reset

- Increments `_generation` to invalidate in-flight `ValueTask` tokens
- Publishes `_closed = 1`
- Cancels any waiting read or flush waiter with `OperationCanceledException`
- Resets all write buffer state (WriteHead, WriteTail = 0)
- Resets both `_readSignal` and `_flushSignal`
- Clears the SPSC receive ring

### `Clear2()` -- Fast Reset

- Increments `_generation`
- Publishes `_closed = 1`
- Clears the receive ring and resets completion state
- Does **not** cancel waiters (assumes they've already exited)
- Faster than `Clear()` for hot-path pooling

### Generation Counter

The `_generation` counter (incremented on every reuse) serves as the `ValueTask` token. If a stale `ReadAsync()` completes after the connection has been reused, `GetResult()` detects the mismatched token and returns `ReadResult.Closed()` instead of delivering stale data. This prevents use-after-free bugs in the async machinery.

## Connection Object Layout

```csharp
partial class Connection : IBufferWriter<byte>, IValueTaskSource<ReadResult>, IValueTaskSource, IDisposable
{
    // Identity
    int ClientFd;
    Engine.Reactor Reactor;
    int _generation;

    // Read state
    SpscRecvRing _recv;           // capacity: 1024
    ManualResetValueTaskSourceCore<ReadResult> _readSignal;
    int _armed, _pending, _closed;

    // Write state
    byte* WriteBuffer;            // 64-byte aligned unmanaged slab
    int WriteHead, WriteTail, WriteInFlight;
    int SendInflight;             // reactor-owned flag
    ManualResetValueTaskSourceCore<bool> _flushSignal;
    int _flushArmed, _flushInProgress;
}
```
