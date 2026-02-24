---
title: Architecture
weight: 2
---

This section explains the core architectural decisions behind zerg: how threads are organized, how `io_uring` is used, how buffers flow through the system, and how connections are managed.

{{< cards >}}
  {{< card link="reactor-pattern" title="Reactor Pattern" subtitle="Acceptor + N reactor threads, event loop design" >}}
  {{< card link="io-uring" title="io_uring" subtitle="Linux async I/O primer and features used by zerg" >}}
  {{< card link="buffer-rings" title="Buffer Rings" subtitle="Provided buffers lifecycle and zero-copy receive" >}}
  {{< card link="connection-lifecycle" title="Connection Lifecycle" subtitle="Accept, use, close, and return to pool" >}}
  {{< card link="threading-model" title="Threading Model" subtitle="Thread layout, memory ordering, and synchronization" >}}
{{< /cards >}}
