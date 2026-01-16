using System.Buffers;

namespace URocket.Utils;

/*
   unsafe
   {
       byte* ptr = /* from recv, buffer ring, slab, etc * /;
       int len   = /* received length * /;
   
       var manager = new UnmanagedMemoryManager(ptr, len);
   
       ReadOnlyMemory<byte> memory = manager.Memory; // ✅ zero allocation
   }
 */

public sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte>
{
    public byte* Ptr { get; }
    public int Length { get; }
    public ushort BufferId { get; }

    public UnmanagedMemoryManager(byte* ptr, int length, ushort bufferId)
    {
        Ptr = ptr;
        Length = length;
        BufferId = bufferId;
    }

    public override Span<byte> GetSpan()
        => new Span<byte>(Ptr, Length);

    public override MemoryHandle Pin(int elementIndex = 0)
        => new MemoryHandle(Ptr + elementIndex);

    public override void Unpin() { }

    protected override void Dispose(bool disposing) { }
}