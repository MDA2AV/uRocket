---
title: Documentation
next: /docs/getting-started/
---

Welcome to the zerg documentation. zerg is a high-performance TCP server framework for C# built directly on Linux `io_uring`.

## What is zerg?

zerg is a low-level networking library that gives you direct control over sockets, buffers, queues, and scheduling through the Linux `io_uring` async I/O interface. It implements the reactor pattern with one acceptor thread and N reactor threads, each owning their own `io_uring` instance for zero-contention I/O processing.

## Key Features

- **io_uring multishot accept** -- single submission produces a CQE for every new connection
- **io_uring multishot recv with buffer selection** -- kernel picks from a pre-registered buffer pool per packet
- **Zero-allocation hot path** -- unmanaged memory, `ValueTask`, lock-free queues
- **Reactor-per-thread model** -- each reactor independently manages its connections
- **Flexible read API** -- from raw `RingItem` pointers to `ReadOnlySequence<byte>`
- **IBufferWriter write path** -- direct span writes with async flush to kernel
- **Stream adapter** -- `ConnectionStream` for BCL/pipeline compatibility

## Where to Start

{{< cards >}}
  {{< card link="getting-started/installation" title="Installation" subtitle="NuGet package, native dependencies, and build from source" >}}
  {{< card link="getting-started/quick-start" title="Quick Start" subtitle="Minimal server with a connection handler in under 30 lines" >}}
  {{< card link="architecture/reactor-pattern" title="Architecture" subtitle="Understand the acceptor + reactor threading model" >}}
{{< /cards >}}
