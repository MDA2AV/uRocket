---
title: MPSC Queues
weight: 2
---

zerg uses three MPSC (multi-producer, single-consumer) queue implementations for cross-thread communication where multiple handler threads need to send messages to a single reactor thread.

## Overview

| Queue | Item Type | Used For | Algorithm |
|-------|-----------|----------|-----------|
| `MpscUshortQueue` | `ushort` | Buffer ID returns | Sequence-per-slot (Vyukov variant) |
| `MpscIntQueue` | `int` | Flush requests (client fds) | Sequence-per-slot (Vyukov) |
| `MpscRecvRing` | `RingItem` | Multi-producer recv enqueue | Interlocked tail increment |
| `MpscWriteItem` | `WriteItem` | Write item queue | Interlocked tail increment |

## MpscUshortQueue

Used by handlers to return buffer IDs to the reactor after processing received data.

### Design

```csharp
public sealed class MpscUshortQueue
{
    private int _capacity;         // power of 2
    private int _mask;             // capacity - 1
    private long[] _seq;           // per-slot sequence numbers
    private ushort[] _data;        // actual values
    private long _tail;            // producer position (contended)
    private long _head;            // consumer position (single reader)
}
```

### Algorithm: Sequence-Per-Slot

This implements a variant of Dmitry Vyukov's bounded MPSC queue. Each slot has a sequence number that coordinates ownership between producers and the consumer.

**Producer (`TryEnqueue`):**

```
1. ticket = Interlocked.Increment(ref _tail) - 1    // reserve unique slot
2. idx = ticket & _mask                               // compute array index
3. if _seq[idx] != ticket: return false               // slot not ready (full)
4. _data[idx] = value                                 // store value
5. Volatile.Write(ref _seq[idx], ticket + 1)          // publish (release)
```

The key insight: each producer gets a unique ticket via `Interlocked.Increment`. If the slot's sequence matches the ticket, the slot is free. After storing the value, the producer advances the sequence by 1 to signal "ready for consumption."

**Consumer (`TryDequeue`):**

```
1. head = _head                                       // plain read (single consumer)
2. idx = head & _mask
3. if _seq[idx] != head + 1: return false             // not ready yet
4. value = _data[idx]                                 // extract value
5. Volatile.Write(ref _seq[idx], head + _capacity)    // mark free for next wrap
6. _head = head + 1                                   // advance (plain write)
```

The consumer checks if the sequence is `head + 1` (one ahead of its position, meaning a producer has filled it). After reading, it resets the sequence to `head + capacity` so the slot is ready for the next cycle.

**Blocking variant (`EnqueueSpin`):**

```csharp
public void EnqueueSpin(ushort value)
```

Spins with `SpinWait` backoff until `TryEnqueue` succeeds. Used when buffer returns must not be dropped.

### Drain

```csharp
public int Drain(Action<ushort> consume, int max = int.MaxValue)
```

Dequeues up to `max` items, calling the action for each. Returns count drained.

## MpscIntQueue

Same algorithm as `MpscUshortQueue`, but stores `int` values. Used for flush requests (handler enqueues `clientFd` to the reactor's flush queue).

### Structure

```csharp
public sealed class MpscIntQueue
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct PaddedLong { [FieldOffset(0)] public long Value; }

    struct Cell { long Sequence; int Value; }

    private Cell[] _buffer;
    private PaddedLong _enqueuePos;   // 64-byte padded to prevent false sharing
    private PaddedLong _dequeuePos;   // 64-byte padded to prevent false sharing
}
```

The `PaddedLong` struct ensures the enqueue and dequeue positions are on separate cache lines (64 bytes), preventing false sharing between producer and consumer threads.

### API

```csharp
public bool TryEnqueue(int item)     // CAS-based multi-producer enqueue
public bool TryDequeue(out int item) // single-consumer dequeue
public int CountApprox { get; }      // approximate queue depth
```

The `TryEnqueue` implementation uses `Interlocked.CompareExchange` in a loop to claim a slot, making it lock-free but not wait-free under contention.

## MpscRecvRing

A simpler MPSC ring buffer using `Interlocked.Increment` for slot reservation. Used when multiple producers need to enqueue `RingItem` structs.

### Design

```csharp
public sealed unsafe class MpscRecvRing
{
    private RingItem[] _items;
    private int _mask;
    private long _tail;    // producers: Interlocked.Increment
    private long _head;    // consumer: plain read/write
}
```

### Algorithm: Interlocked Tail

```
Producer:
1. Volatile read head and tail for fast full-check
2. slot = Interlocked.Increment(ref _tail) - 1   // full fence, reserve slot
3. _items[slot & _mask] = item                     // store
4. return true

Consumer:
1. snapshot = Volatile.Read(ref _tail)
2. while _head < snapshot:
     item = _items[_head & _mask]
     _head++
```

This is simpler than the sequence-per-slot algorithm: producers just increment the tail atomically, and the consumer trusts the tail as an upper bound.

**Trade-off:** There's a small window where `_tail` has been incremented but the item isn't stored yet. The consumer relies on the `SnapshotTail()` / `TryDequeueUntil()` pattern to avoid reading partially-written slots.

### API

```csharp
public bool TryEnqueue(in RingItem item)
public long SnapshotTail()
public bool TryDequeueUntil(long tailSnapshot, out RingItem item)
public RingItem DequeueSingle()
public bool IsEmpty()
public void Clear()
```

## MpscWriteItem

Same structure as `MpscRecvRing` but stores `WriteItem` structs (buffer + client fd pairs).

## Choosing Between Algorithms

| Algorithm | Pros | Cons | Used When |
|-----------|------|------|-----------|
| Sequence-per-slot (Vyukov) | No torn reads, exact capacity | CAS contention under high producer count | Small value types (`ushort`, `int`) |
| Interlocked tail | Simpler, faster under low contention | Small visibility window | Struct items (`RingItem`, `WriteItem`) |

## Cache Line Considerations

The `MpscIntQueue` pads both `_enqueuePos` and `_dequeuePos` to 64 bytes to prevent false sharing. When a producer writes to `_enqueuePos` and the consumer reads `_dequeuePos`, they should be on different cache lines to avoid invalidation traffic.

The ring-based MPSC queues (`MpscRecvRing`, `MpscWriteItem`) don't pad their head/tail fields separately, relying on the item array itself to provide spatial separation.
