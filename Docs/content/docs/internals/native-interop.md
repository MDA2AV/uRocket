---
title: Native Interop
weight: 3
---

zerg communicates with the Linux kernel's `io_uring` subsystem through a C shim library (`liburingshim.so`) that wraps `liburing`. All native calls are made via P/Invoke from the `ABI.URing` class.

## Architecture

```
C# (zerg)  ──P/Invoke──▶  liburingshim.so  ──calls──▶  liburing  ──syscall──▶  Linux kernel
```

The shim library serves two purposes:
1. Provides a flat C ABI that P/Invoke can call (liburing uses inline functions and macros)
2. Bundles liburing statically so users don't need to install it

## Bundled Native Libraries

The NuGet package includes pre-built shim libraries:

| Runtime | Path |
|---------|------|
| `linux-x64` (glibc) | `native/linux-x64/liburingshim.so` |
| `linux-musl-x64` (Alpine) | `native/linux-musl-x64/liburingshim.so` |

These are automatically copied to the output directory by MSBuild.

## Opaque Types

The C# side uses fixed-size structs as opaque handles for kernel types:

### io_uring

```csharp
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct io_uring
{
    private fixed ulong _opaque[128];  // 1024 bytes
}
```

Managed equivalent of `struct io_uring` from liburing. Never directly manipulated -- passed to/from shim functions by pointer.

### io_uring_sqe

```csharp
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct io_uring_sqe
{
    private fixed ulong _opaque[8];    // 64 bytes
}
```

Submission Queue Entry. Populated via `shim_prep_*` helpers. Never directly filled from C#.

### io_uring_cqe

```csharp
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct io_uring_cqe
{
    public ulong user_data;   // 64-bit token from SQE
    public int res;           // result (bytes transferred or -errno)
    public uint flags;        // CQE flags (buffer selection, multishot)
}
```

Completion Queue Entry. Read directly from C# after peek/wait.

### io_uring_buf_ring

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct io_uring_buf_ring { }
```

Opaque token for the buffer ring. All manipulation via shim functions.

### __kernel_timespec

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct __kernel_timespec
{
    public long tv_sec;     // seconds
    public long tv_nsec;    // nanoseconds
}
```

Matches the Linux kernel's `struct __kernel_timespec` (16 bytes). Used for timeout operations.

## P/Invoke Surface

### Ring Lifecycle

| Function | Description |
|----------|-------------|
| `shim_create_ring(entries, out err)` | Create ring with SQ/CQ size |
| `shim_create_ring_ex(entries, flags, cpu, idle_ms, out err)` | Create ring with flags (SQPOLL, etc.) |
| `shim_destroy_ring(ring)` | Release all native resources |
| `shim_get_ring_flags(ring)` | Get ring setup flags |

### Submission

| Function | Description |
|----------|-------------|
| `shim_get_sqe(ring)` | Acquire fresh SQE slot (null if full) |
| `shim_submit(ring)` | Submit pending SQEs to kernel |
| `shim_submit_and_wait(ring, waitNr)` | Submit + wait for CQEs (single syscall) |
| `shim_submit_and_wait_timeout(ring, cqes, waitNr, ts)` | Submit + wait with timeout |
| `shim_enter(ring, toSubmit, minComplete, flags, ts)` | Direct `io_uring_enter(2)` |

### Completion

| Function | Description |
|----------|-------------|
| `shim_wait_cqe(ring, cqe)` | Blocking wait for one CQE |
| `shim_wait_cqe_timeout(ring, cqe, ts)` | Wait with timeout |
| `shim_wait_cqes(ring, cqe, waitNr, ts)` | Wait for N CQEs |
| `shim_peek_batch_cqe(ring, cqes, count)` | Non-blocking batch peek |
| `shim_cqe_seen(ring, cqe)` | Mark one CQE consumed |
| `shim_cq_advance(ring, count)` | Mark N CQEs consumed (batch) |
| `shim_cq_ready(ring)` | Check CQE count (no syscall) |
| `shim_sq_ready(ring)` | Check available SQE slots |

### SQE Preparation

| Function | Description |
|----------|-------------|
| `shim_prep_multishot_accept(sqe, lfd, flags)` | Multishot accept on listening fd |
| `shim_prep_recv_multishot_select(sqe, fd, buf_group, flags)` | Multishot recv with buffer selection |
| `shim_prep_send(sqe, fd, buf, nbytes, flags)` | Send data from buffer |
| `shim_prep_cancel64(sqe, user_data, flags)` | Cancel operation by user_data |

### User Data

| Function | Description |
|----------|-------------|
| `shim_sqe_set_data64(sqe, data)` | Set 64-bit token on SQE |
| `shim_cqe_get_data64(cqe)` | Get token from CQE |

### Buffer Ring

| Function | Description |
|----------|-------------|
| `shim_setup_buf_ring(ring, entries, bgid, flags, out ret)` | Create and register buffer ring |
| `shim_free_buf_ring(ring, br, entries, bgid)` | Unregister and free |
| `shim_buf_ring_add(br, addr, len, bid, mask, idx)` | Add buffer to ring |
| `shim_buf_ring_advance(br, count)` | Publish N buffers to kernel |
| `shim_cqe_has_buffer(cqe)` | Check if CQE used a provided buffer |
| `shim_cqe_buffer_id(cqe)` | Extract buffer ID from CQE flags |

## User Data Token Packing

zerg encodes the operation kind and file descriptor into the 64-bit `user_data` field:

```csharp
internal enum UdKind : uint
{
    Accept = 1,
    Recv   = 2,
    Send   = 3,
    Cancel = 4
}

static ulong PackUd(UdKind k, int fd)
    => ((ulong)k << 32) | (uint)fd;

static UdKind UdKindOf(ulong ud)
    => (UdKind)(ud >> 32);

static int UdFdOf(ulong ud)
    => (int)(ud & 0xFFFFFFFF);
```

This allows the reactor to dispatch CQEs to the correct handler (recv, send, cancel) in a single branch on the extracted kind.

## Socket Operations

The `ABI.LinuxSocket` class provides raw socket syscall wrappers:

| Function | Description |
|----------|-------------|
| `socket(domain, type, proto)` | Create socket |
| `bind(fd, addr, len)` | Bind to address (IPv4 and IPv6 overloads) |
| `listen(fd, backlog)` | Mark as listening |
| `setsockopt(fd, level, optname, optval, optlen)` | Set socket option |
| `close(fd)` | Close file descriptor |
| `fcntl(fd, cmd, arg)` | File control (flags) |
| `inet_pton(af, src, dst)` | Text to binary IP address |

## CPU Affinity

The `ABI.Affinity` class provides thread-to-core pinning:

```csharp
Affinity.PinCurrentThreadToCpu(int cpu)
```

Uses `sched_setaffinity(2)` with a CPU bitmask. Best-effort -- failures are logged but not fatal.
