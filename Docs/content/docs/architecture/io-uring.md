---
title: io_uring
weight: 2
---

`io_uring` is a Linux kernel interface for asynchronous I/O. zerg uses it as its sole I/O mechanism -- there are no `epoll`, `kqueue`, or `libuv` fallbacks.

## How io_uring Works

`io_uring` provides two ring buffers shared between userspace and the kernel:

- **Submission Queue (SQ)**: userspace writes I/O requests (SQEs) here
- **Completion Queue (CQ)**: kernel writes I/O results (CQEs) here

The typical flow is:

1. Acquire an SQE slot from the SQ
2. Prepare the SQE (what operation, which fd, which buffer)
3. Submit the SQ to the kernel
4. Wait for or peek at CQEs in the CQ
5. Process results and advance the CQ head

Because both queues are in shared memory, the kernel can process I/O without copying data back through syscall boundaries. In the best case (`SQPOLL` mode), even the submit step doesn't require a syscall.

## Features Used by zerg

### Multishot Accept

```c
shim_prep_multishot_accept(sqe, listenFd, SOCK_NONBLOCK);
```

A single submission arms the kernel to produce one CQE per accepted connection indefinitely. The acceptor thread never re-arms -- it just processes accept completions as they arrive.

Each CQE contains:
- `cqe->res`: the new client file descriptor (or negative errno)
- `cqe->flags & IORING_CQE_F_MORE`: indicates more completions will follow

### Multishot Recv with Buffer Selection

```c
shim_prep_recv_multishot_select(sqe, clientFd, bufferGroupId, 0);
```

A single submission arms the kernel to receive data for a connection. Each time data arrives, the kernel:

1. Picks an available buffer from the registered buffer ring
2. Copies received data into that buffer
3. Produces a CQE with the buffer ID in the flags

The reactor can identify which buffer was used via:

```c
bufferId = shim_cqe_buffer_id(cqe);
bufferPtr = slab + bufferId * bufferSize;
bytesReceived = cqe->res;
```

This eliminates per-recv buffer allocation -- the kernel selects from a pre-registered pool.

### Buffer Rings (Provided Buffers)

Buffer rings are a pool of pre-allocated buffers registered with the kernel:

```c
// Setup: create and register the ring
br = shim_setup_buf_ring(ring, entries, bgid, 0, &ret);

// Populate: add buffers to the ring
for (i = 0; i < entries; i++)
    shim_buf_ring_add(br, slab + i * size, size, i, mask, i);
shim_buf_ring_advance(br, entries);

// After consuming a buffer: return it
shim_buf_ring_add(br, slab + bid * size, size, bid, mask, idx);
shim_buf_ring_advance(br, 1);
```

See [Buffer Rings](../buffer-rings/) for the full lifecycle.

### SINGLE_ISSUER

```c
flags |= IORING_SETUP_SINGLE_ISSUER;
```

Tells the kernel that only one thread will submit to this ring. The kernel can skip locking on the SQ, improving submission throughput. This matches zerg's reactor model perfectly since each reactor thread is the sole submitter to its ring.

### DEFER_TASKRUN

```c
flags |= IORING_SETUP_DEFER_TASKRUN;
```

Defers kernel task_work execution until the next time userspace enters the ring. This reduces latency spikes from kernel work being done in interrupt context and improves `async/await` integration since completions arrive at predictable points.

### SQPOLL (Optional)

```c
flags |= IORING_SETUP_SQPOLL | IORING_SETUP_SQ_AFF;
```

Creates a kernel thread that continuously polls the SQ for new submissions. This eliminates the `io_uring_enter()` syscall for submitting work. The tradeoff is a dedicated CPU core spinning on the SQ.

Enable SQPOLL when:
- You have spare CPU cores
- You need the absolute lowest submission latency
- Your workload has consistent, high-frequency submissions

### Submit-and-Wait

zerg's reactor loop uses a combined submit-and-wait call:

```c
shim_submit_and_wait_timeout(ring, cqes, waitNr, &timeout);
```

This is a single syscall that:
1. Submits all pending SQEs
2. Waits for at least one CQE (or timeout)

Combining submit + wait into one call reduces syscall overhead compared to separate `submit()` + `wait_cqe()` calls.

### CQE Batching

```c
count = shim_peek_batch_cqe(ring, cqes, maxBatch);
// process all CQEs...
shim_cq_advance(ring, count);
```

Instead of processing one CQE at a time, the reactor peeks a batch and processes them all before advancing the CQ head. This amortizes the CQ head update across multiple completions.

## User Data Token Packing

zerg packs a kind tag and file descriptor into the 64-bit `user_data` field of each SQE:

```csharp
// Pack: upper 32 bits = kind, lower 32 bits = fd
ulong ud = PackUd(UdKind.Recv, clientFd);

// Unpack from CQE
UdKind kind = UdKindOf(cqe->user_data);  // Recv, Send, Accept, Cancel
int fd = UdFdOf(cqe->user_data);
```

This lets the reactor immediately dispatch CQEs to the right handler without any lookup table.

## Native Interop

All `io_uring` operations go through a C shim library (`liburingshim.so`) that wraps `liburing`. See [Native Interop](../../internals/native-interop/) for the full P/Invoke surface.
