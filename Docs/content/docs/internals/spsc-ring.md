---
title: SPSC Recv Ring
weight: 1
---

The `SpscRecvRing` is the per-connection inbound data queue. It's a lock-free, single-producer single-consumer ring buffer optimized for the reactor-to-handler data path.

## Design

```csharp
public sealed class SpscRecvRing
{
    private RingItem[] _items;   // power-of-2 capacity array
    private int _mask;           // capacity - 1
    private long _tail;          // producer write position
    private long _head;          // consumer read position
}
```

- **Capacity:** 1024 items (hardcoded in connection construction)
- **Producer:** Reactor thread (enqueues `RingItem` on recv CQE)
- **Consumer:** Handler task (drains items in snapshot batches)

## Why SPSC?

Each connection is owned by exactly one reactor. The reactor is the sole producer, and the handler awaiting `ReadAsync()` is the sole consumer. This single-producer/single-consumer invariant means:

- **No atomics needed** on `_tail` or `_head` for their respective owners
- **Volatile reads/writes** are sufficient for cross-thread visibility
- No CAS loops, no contention, no backoff
- Maximum throughput with minimal overhead

## API

### Producer (Reactor Thread)

```csharp
public bool TryEnqueue(in RingItem item)
```

Enqueues one item. Returns `false` if the ring is full.

1. Volatile-reads `_head` to check capacity (`_tail - _head >= capacity` → full)
2. Stores item at `_items[_tail & _mask]`
3. Volatile-writes `_tail = _tail + 1` (release semantics: ensures item is visible before tail advances)

### Consumer (Handler Thread)

```csharp
public long SnapshotTail()
```

Volatile-reads `_tail` to capture the current batch boundary. The handler can drain items up to this position without observing partially-written state.

```csharp
public bool TryDequeueUntil(long tailSnapshot, out RingItem item)
```

Dequeues one item, bounded by the snapshot. Returns `false` when `_head >= tailSnapshot`.

1. Compares `_head` against `tailSnapshot`
2. Reads `_items[_head & _mask]`
3. Advances `_head` (plain write -- only consumer writes `_head`)

```csharp
public RingItem DequeueSingle()
```

Unconditional dequeue. Assumes the ring is not empty. Direct read and advance.

### Inspection

```csharp
public bool IsEmpty()           // volatile reads head and tail
public long GetTailHeadDiff()   // approximate count (tail - head)
public void Clear()             // volatile reset both to 0
```

## Memory Ordering

The SPSC ring relies on two key ordering guarantees:

### Producer Side (Release)

```csharp
_items[_tail & _mask] = item;        // store item
Volatile.Write(ref _tail, _tail + 1); // publish tail (release fence)
```

The volatile write to `_tail` ensures that the item store is visible to the consumer before the tail advance is visible. The consumer will never see an advanced tail with a stale item.

### Consumer Side (Acquire)

```csharp
long tail = Volatile.Read(ref _tail);  // acquire fence
var item = _items[_head & _mask];      // load item
```

The volatile read of `_tail` ensures the consumer sees all stores made by the producer up to that tail position.

### Single-Writer Optimization

Since only the consumer writes `_head` and only the producer writes `_tail`, these fields don't need atomic operations. Plain reads from the owning thread and volatile reads from the other thread are sufficient.

## Snapshot-Based Batching

The snapshot pattern prevents the consumer from chasing a moving tail:

```csharp
// Handler side:
ReadResult result = await connection.ReadAsync();
long snapshot = result.TailSnapshot;  // captured once

// Drain exactly what was available at ReadAsync time
while (connection.TryGetRing(snapshot, out RingItem ring))
{
    // process ring...
    connection.ReturnRing(ring.BufferId);
}
// Guaranteed to terminate: snapshot is fixed, head advances toward it
```

This is important because the reactor may enqueue more items while the handler is processing. Without a snapshot boundary, the handler could spin indefinitely.

## Ring Full Behavior

When the SPSC ring is full (1024 items waiting to be consumed by the handler):

1. The reactor's `EnqueueRingItem()` detects the ring is full
2. The connection is marked as closed (`_closed = 1`)
3. If the handler is armed, it's woken with a close signal
4. If no handler is armed, `_pending` is set

This is a safety measure -- a handler that falls behind and doesn't drain its ring will eventually have its connection closed rather than consuming unbounded kernel buffers.

## Performance Characteristics

| Operation | Cost | Allocation |
|-----------|------|------------|
| `TryEnqueue` | ~5 ns | None |
| `TryDequeueUntil` | ~3 ns | None |
| `SnapshotTail` | ~1 ns | None |
| `IsEmpty` | ~2 ns | None |

All operations are `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for JIT inlining.
