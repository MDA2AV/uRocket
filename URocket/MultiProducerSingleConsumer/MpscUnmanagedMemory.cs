using System.Runtime.CompilerServices;
using URocket.Utils;

namespace URocket.MultiProducerSingleConsumer;

public sealed unsafe class MpscUnmanagedMemory
{
    private readonly UnmanagedMemoryManager[] _items;
    private readonly int _mask;
    
    private long _tail; // producer-reserved count
    private long _head; // consumer position

    public MpscUnmanagedMemory(int capacityPow2) {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("capacityPow2 must be a power of two", nameof(capacityPow2));

        _items = new UnmanagedMemoryManager[capacityPow2];
        _mask  = capacityPow2 - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in UnmanagedMemoryManager item) {
        // Fast full check (approx) using current head/tail
        long head = Volatile.Read(ref _head); 
        long tail = Volatile.Read(ref _tail);
        if (tail - head >= _items.Length) return false; // full

        // Reserve a unique slot
        long slot = Interlocked.Increment(ref _tail) - 1;

        // Store item
        _items[slot & _mask] = item;

        // Interlocked.Increment is a full fence; consumer reading _tail sees publish.
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long SnapshotTail() => Volatile.Read(ref _tail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(long tailSnapshot, out UnmanagedMemoryManager? item) {
        long head = _head;
        if (head >= tailSnapshot)
        {
            item = null;
            return false;
        }

        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
        => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);

    public void Clear() {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
}