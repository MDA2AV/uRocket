---
title: "Connection: Read"
weight: 2
---

The read API provides async waiting for inbound data, snapshot-based batch draining, and buffer management.

## Core Read Cycle

Every read follows this pattern:

```csharp
// 1. Wait for data
ReadResult result = await connection.ReadAsync();

// 2. Check for close
if (result.IsClosed) return;

// 3. Drain and process buffers
// 4. Return buffers to kernel
// 5. Reset for next read
connection.ResetRead();
```

## ReadAsync

```csharp
public ValueTask<ReadResult> ReadAsync()
```

Waits for at least one received buffer to be available, or for the connection to close.

**Returns:** A `ReadResult` containing a tail snapshot and close status.

**Fast paths (returns synchronously):**
- Connection already closed → `ReadResult.Closed()`
- Data already pending in the receive ring → immediate result with snapshot
- `_pending` flag set from previous produce → immediate result

**Slow path:**
- Parks the calling task until the reactor enqueues data or marks the connection closed

**Rules:**
- Only **one** outstanding `ReadAsync` per connection at a time (single waiter)
- After processing the batch, call `ResetRead()` before the next `ReadAsync()`

## ReadResult

```csharp
public readonly struct ReadResult
{
    public long TailSnapshot { get; }
    public bool IsClosed { get; }
}
```

| Property | Description |
|----------|-------------|
| `TailSnapshot` | Logical position in the receive ring at the time of read. Defines the batch boundary -- you can drain items up to this position. |
| `IsClosed` | `true` if the connection was closed (EOF, error, or reuse). |

## ResetRead

```csharp
public void ResetRead()
```

Prepares the connection for the next read cycle. Must be called after draining a batch and before the next `ReadAsync()`.

Internally:
- Resets the `ManualResetValueTaskSourceCore` (it's single-use)
- Checks if new data arrived during processing and sets `_pending = 1` if so

## High-Level Drain APIs

These methods dequeue all items in the current snapshot batch. Call one of these after `ReadAsync()` returns successfully.

### GetAllSnapshotRingsAsUnmanagedMemory

```csharp
public UnmanagedMemoryManager[] GetAllSnapshotRingsAsUnmanagedMemory(ReadResult readResult)
```

Returns an array of `UnmanagedMemoryManager` instances, one per received buffer in the snapshot. Each wraps a native pointer with a managed `Memory<byte>` view.

Use with `ToReadOnlySequence()` for parsing:

```csharp
var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();
// parse sequence...
rings.ReturnRingBuffers(connection.Reactor);
```

### GetAllSnapshotRings

```csharp
public RingItem[] GetAllSnapshotRings(ReadResult readResult)
```

Returns raw `RingItem` values from the snapshot. Each item has `Ptr`, `Length`, and `BufferId`.

```csharp
RingItem[] items = connection.GetAllSnapshotRings(result);
foreach (var item in items)
{
    ReadOnlySpan<byte> data = item.AsSpan();
    // process...
    connection.ReturnRing(item.BufferId);
}
```

### TryDynamicallyGetAllSnapshotRingsAsReadOnlySequence

```csharp
public bool TryDynamicallyGetAllSnapshotRingsAsReadOnlySequence(
    ReadResult readResult,
    out List<UnmanagedMemoryManager> rings,
    out ReadOnlySequence<byte> sequence)
```

Builds a zero-copy `ReadOnlySequence<byte>` over all segments. Returns `false` if no data is available.

### TryDynamicallyGetAllSnapshotRingsAsUnmanagedMemory

```csharp
public bool TryDynamicallyGetAllSnapshotRingsAsUnmanagedMemory(
    ReadResult readResult,
    out List<UnmanagedMemoryManager> rings)
```

Dequeues all segments as a list of `UnmanagedMemoryManager`. Returns `false` if no data.

### TryDynamicallyGetAllSnapshotRings

```csharp
public bool TryDynamicallyGetAllSnapshotRings(
    ReadResult readResult,
    out List<RingItem> rings)
```

Dequeues all segments as raw `RingItem` values. Returns `false` if no data.

## Low-Level Drain APIs

For fine-grained control over individual ring items.

### TryGetRing

```csharp
public bool TryGetRing(long tailSnapshot, out RingItem item)
```

Dequeue one item from the receive ring, bounded by the snapshot. Returns `false` when the batch is exhausted.

```csharp
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ReadOnlySpan<byte> data = ring.AsSpan();
    // process one buffer...
    connection.ReturnRing(ring.BufferId);
}
```

### GetRing

```csharp
public RingItem GetRing()
```

Dequeue one item unconditionally. Assumes the ring is not empty. Use only when you know items are available.

## Buffer Return

### ReturnRing

```csharp
public void ReturnRing(ushort bufferId)
```

Returns a consumed buffer to the reactor's buffer ring. The reactor will add it back to the kernel buffer ring on its next loop iteration.

**Must be called** for every buffer obtained via `TryGetRing`, `GetRing`, `GetAllSnapshotRings`, etc.

### ReturnRingBuffers (Extension Method)

```csharp
public static void ReturnRingBuffers(this UnmanagedMemoryManager[] managers, Engine.Engine.Reactor reactor)
```

Batch return of buffer IDs from an array of `UnmanagedMemoryManager`:

```csharp
var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
// process...
rings.ReturnRingBuffers(connection.Reactor);
```

## RingItem

```csharp
public readonly unsafe struct RingItem(byte* ptr, int length, ushort bufferId)
{
    public byte* Ptr { get; }
    public int Length { get; }
    public ushort BufferId { get; }

    public ReadOnlySpan<byte> AsSpan();
    public UnmanagedMemoryManager AsUnmanagedMemoryManager();
}
```

A lightweight struct wrapping a received data buffer from the kernel. The pointer is valid until `ReturnRing(BufferId)` is called.

## Properties

### TotalRingCount

```csharp
public long TotalRingCount { get; }
```

Current number of items in the receive ring (approximate, for diagnostics).

### SnapshotRingCount

```csharp
public int SnapshotRingCount { get; }
```

Number of items in the current snapshot batch. Set when `ReadAsync()` captures the tail.

## Reactor-Side Producer API

These methods are called by the reactor thread, not by user code:

### EnqueueRingItem (Internal)

```csharp
public void EnqueueRingItem(byte* ptr, int length, ushort bufferId)
```

Called by the reactor when a recv CQE completes. Enqueues a `RingItem` into the connection's SPSC ring and wakes the handler if armed.

- If the ring is full (1024 items), the connection is closed
- If the handler is armed (`_armed == 1`), it's woken immediately
- If no handler is armed, `_pending` is set for the next `ReadAsync()` fast-path

### MarkClosed (Internal)

Sets `_closed = 1` and wakes any armed handler so it receives `ReadResult.Closed()`.
