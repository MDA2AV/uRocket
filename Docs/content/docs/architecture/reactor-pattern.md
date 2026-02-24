---
title: Reactor Pattern
weight: 1
---

zerg implements a classic **reactor pattern** with a split-architecture design: one dedicated acceptor thread and N independent reactor threads.

## Overview

```
Clients ──TCP──▶ Acceptor Thread (1 io_uring, multishot accept)
                      │
                      │ round-robin fd distribution
                      ▼
              ┌───────┼───────┐
              ▼       ▼       ▼
          Reactor 0  Reactor 1  ...  Reactor N
          (io_uring) (io_uring)      (io_uring)
          (buf_ring) (buf_ring)      (buf_ring)
          (conn map) (conn map)      (conn map)
```

Every thread in the system owns its own `io_uring` instance. There is no shared ring, and no lock contention on the I/O path.

## Acceptor Thread

The acceptor is responsible for one job: accepting new TCP connections.

1. Creates a listening socket (IPv4 or IPv6 dual-stack, configurable via `IPVersion`)
2. Binds and listens with the configured `Backlog`
3. Sets up its own `io_uring` and arms a **multishot accept** SQE
4. Enters an event loop that:
   - Peeks a batch of CQEs (accepted file descriptors)
   - Sets `TCP_NODELAY` on each accepted socket
   - Distributes fds to reactors in round-robin order via lock-free `ConcurrentQueue<int>` (one per reactor)
   - Sleeps in `io_uring_wait_cqes()` when idle

Multishot accept means a single submission produces a CQE for every incoming connection without re-arming. The acceptor never allocates per-connection -- it just hands off integer file descriptors.

### Acceptor Event Loop

```
loop:
    cqeCount = peek_batch_cqe(ring, cqes, batchSize)
    if cqeCount == 0:
        submit_and_wait_timeout(ring, timeout)
        continue

    for each cqe in cqes:
        if cqe.res < 0:
            log error, continue
        clientFd = cqe.res
        setsockopt(clientFd, TCP_NODELAY)
        reactorQueues[nextReactor++ % reactorCount].Enqueue(clientFd)

    cq_advance(ring, cqeCount)
```

## Reactor Threads

Each reactor thread owns:

- Its own `io_uring` instance (created with `SINGLE_ISSUER | DEFER_TASKRUN` by default)
- A **buffer ring** for zero-copy receive operations
- A `Dictionary<int, Connection>` mapping file descriptors to connection objects
- Lock-free queues for receiving new fds from the acceptor and flush requests from handlers

### Reactor Event Loop

Each reactor runs a tight loop:

```
loop:
    // 1. Drain newly accepted connections
    while reactorQueue.TryDequeue(out clientFd):
        connection = pool.Get() or new Connection()
        connection.SetFd(clientFd).SetReactor(this)
        connections[clientFd] = connection
        arm multishot_recv_select(clientFd, bufferGroupId)
        notify application via Channel

    // 2. Drain buffer returns
    while returnQ.TryDequeue(out bufferId):
        buf_ring_add(bufferRing, slab + bufferId * bufSize, bufSize, bufferId, mask, idx++)
    buf_ring_advance(bufferRing, returnCount)

    // 3. Drain flush requests
    while flushQ.TryDequeue(out flushFd):
        connection = connections[flushFd]
        prep_send(sqe, flushFd, connection.WriteBuffer, connection.WriteInFlight, 0)
        submit pending sends

    // 4. Process completions
    cqeCount = peek_batch_cqe(ring, cqes, batchSize)
    for each cqe:
        kind = UdKindOf(cqe.user_data)
        fd   = UdFdOf(cqe.user_data)

        if kind == Recv:
            if cqe.res <= 0: close connection, return buffer
            else: enqueue RingItem to connection, wake handler

        if kind == Send:
            advance WriteHead, resubmit if partial, signal flush complete

        if kind == Cancel:
            handle cancellation completion

    cq_advance(ring, cqeCount)
    submit_and_wait_timeout(ring, timeout)
```

## Connection Distribution

The acceptor distributes connections using a simple round-robin counter:

```
reactorIndex = acceptCount++ % reactorCount
```

Each reactor gets approximately equal load. The distribution is via `ConcurrentQueue<int>` -- one queue per reactor -- so the acceptor never blocks waiting for a reactor.

## Application Integration

After a reactor registers a new connection, it pushes a `ConnectionItem` (reactor ID + client fd) into an unbounded `Channel<ConnectionItem>`. The `Engine.AcceptAsync()` method reads from this channel, returning fully-registered `Connection` objects to the application.

This means by the time your handler receives a connection:
- The connection is already assigned to a reactor
- Multishot recv is already armed
- The buffer ring is ready to receive data
- You can immediately call `ReadAsync()`
