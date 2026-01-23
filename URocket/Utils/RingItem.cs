namespace URocket.Utils;

public readonly unsafe struct RingItem {
    public readonly byte* Ptr;
    public readonly int Length;
    public readonly ushort BufferId;

    public RingItem(byte* ptr, int length, ushort bufferId) {
        Ptr = ptr;
        Length = length;
        BufferId = bufferId;
    }

    public ReadOnlySpan<byte> AsSpan() => new(Ptr, Length);
    
    public UnmanagedMemoryManager.UnmanagedMemoryManager AsUnmanagedMemoryManager() => new(Ptr, Length,  BufferId);
}