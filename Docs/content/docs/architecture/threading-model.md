---
title: Threading Model
weight: 5
---

zerg uses a fixed set of dedicated threads with strict ownership rules. Every piece of mutable state is owned by exactly one thread, and all cross-thread communication happens through lock-free queues with well-defined memory ordering.

## Thread Layout

```
┌─────────────────┐
│  Acceptor Thread │  1 thread
│  (io_uring)      │  Accepts connections, distributes fds
└────────┬────────┘
         │ ConcurrentQueue<int> per reactor
         ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│  Reactor 0    │  │  Reactor 1    │  │  Reactor N    │  N threads
│  (io_uring)   │  │  (io_uring)   │  │  (io_uring)   │  Event loops
│  (buf_ring)   │  │  (buf_ring)   │  │  (buf_ring)   │
│  (conn dict)  │  │  (conn dict)  │  │  (conn dict)  │
└───────┬───────┘  └───────┬───────┘  └───────┬───────┘
        │                  │                  │
        ▼                  ▼                  ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│  Handler Tasks│  │  Handler Tasks│  │  Handler Tasks│  Task pool
│  (async/await)│  │  (async/await)│  │  (async/await)│  User code
└───────────────┘  └───────────────┘  └───────────────┘
```

### Thread Count

Total threads = 1 (acceptor) + N (reactors), where N = `EngineOptions.ReactorCount`.

Handler tasks run on the .NET thread pool and are not dedicated threads. Multiple handler tasks may be active per reactor, but each connection's `ReadAsync`/`FlushAsync` enforces single-waiter semantics.

## Ownership Rules

| State | Owner | Accessed By |
|-------|-------|-------------|
| Acceptor `io_uring` | Acceptor thread | Acceptor only |
| Reactor `io_uring` | Reactor thread | Reactor only |
| Buffer ring | Reactor thread | Reactor (add/advance), handler (ReturnRing via MPSC queue) |
| `connections` dict | Reactor thread | Reactor only |
| Connection read state (`_recv`, `_armed`, etc.) | Split | Reactor produces, handler consumes |
| Connection write slab | Handler | Handler writes, reactor reads during flush |
| `SendInflight` flag | Reactor thread | Reactor writes (Volatile), handler reads |

## Cross-Thread Communication

All cross-thread data flow uses lock-free queues:

### Acceptor → Reactor: New Connections

```
ConcurrentQueue<int> ReactorQueues[reactorId]
```

The acceptor enqueues integer file descriptors. Each reactor drains its queue at the start of every loop iteration. `.NET ConcurrentQueue` provides full thread-safety.

### Handler → Reactor: Buffer Returns

```
MpscUshortQueue returnQ
```

When a handler calls `connection.ReturnRing(bufferId)`, the `ushort` buffer ID is enqueued to the reactor's MPSC return queue. The reactor drains this queue and returns buffers to the kernel buffer ring.

### Handler → Reactor: Flush Requests

```
MpscIntQueue flushQ
```

When a handler calls `FlushAsync()`, the connection's client fd is enqueued to the reactor's flush queue. The reactor drains this and issues `send` SQEs.

### Reactor → Handler: Read Completion

```
SpscRecvRing _recv (per connection)
int _armed, _pending (atomics)
ManualResetValueTaskSourceCore<ReadResult> _readSignal
```

The reactor enqueues `RingItem`s to the connection's SPSC ring and wakes the handler via the ValueTask completion source.

### Reactor → Handler: Flush Completion

```
ManualResetValueTaskSourceCore<bool> _flushSignal
int _flushArmed, _flushInProgress
```

When all staged bytes are sent, the reactor completes the flush signal, resuming the handler's `await FlushAsync()`.

## Memory Ordering

zerg uses three levels of memory ordering:

### Volatile Read/Write

Used for single-word flags where only visibility is needed:

```csharp
Volatile.Write(ref _closed, 1);       // publish close
Volatile.Read(ref _pending);           // check pending flag
Volatile.Write(ref SendInflight, 0);   // clear in-flight flag
```

### Interlocked (Full Fence)

Used in MPSC queues where multiple producers contend:

```csharp
Interlocked.CompareExchange(ref _armed, 0, 1);  // atomically disarm
Interlocked.Increment(ref _tail);                // reserve MPSC slot
```

### Plain Reads (Single-Consumer)

The consumer side of SPSC/MPSC queues uses plain reads for `_head` since only one thread reads and writes it:

```csharp
var head = _head;                      // only consumer writes _head
_head = head + 1;                      // safe: single writer
```

## CPU Affinity

zerg provides optional CPU pinning for reactor threads via the `Affinity` class:

```csharp
Affinity.PinCurrentThreadToCpu(cpuId);
```

This uses the Linux `sched_setaffinity` syscall to bind a thread to a specific core. Pinning prevents the OS scheduler from migrating threads, which improves cache locality and reduces jitter.

CPU pinning is optional and best-effort -- if it fails (e.g., in containers with CPU limits), the thread continues on whatever core the scheduler assigns.

## Handler Task Execution

Handler tasks (`_ = HandleConnectionAsync(connection)`) run on the .NET thread pool. When a handler awaits `ReadAsync()` or `FlushAsync()`:

1. The handler task yields back to the thread pool
2. The reactor thread completes the ValueTask source when data/flush is ready
3. The handler resumes on a thread pool thread (not the reactor thread)

This means:
- Reactor threads never execute user handler code
- Handlers never block reactor threads
- Multiple handlers can be active simultaneously per reactor
- `DEFER_TASKRUN` ensures completions arrive at predictable points in the reactor loop
