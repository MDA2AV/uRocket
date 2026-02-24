---
title: zerg
layout: hextra-home
---

{{< hextra/hero-badge link="https://www.nuget.org/packages/zerg" >}}
  NuGet v0.3.12
{{< /hextra/hero-badge >}}

<div style="margin-top: 1.5rem; margin-bottom: 1.25rem;">
{{< hextra/hero-headline >}}
  High-Performance io_uring Networking for C#
{{< /hextra/hero-headline >}}
</div>

<div style="margin-bottom: 2rem;">
{{< hextra/hero-subtitle >}}
  A zero-allocation, reactor-pattern TCP server framework&nbsp;<br class="sm:hx-block hx-hidden" />built on Linux io_uring with multishot accept, multishot recv, and provided buffers.
{{< /hextra/hero-subtitle >}}
</div>

<div style="margin-bottom: 1.5rem;">
{{< hextra/hero-button text="Get Started" link="docs/getting-started/installation/" >}}
</div>

<div style="margin-top: 2rem;"></div>

{{< hextra/feature-grid >}}
  {{< hextra/feature-card
    title="io_uring Native"
    subtitle="Built directly on Linux io_uring via a thin C shim. Multishot accept, multishot recv, buffer rings, SQPOLL, DEFER_TASKRUN, and SINGLE_ISSUER out of the box."
    link="docs/architecture/io-uring/"
  >}}
  {{< hextra/feature-card
    title="Zero-Allocation Hot Path"
    subtitle="Unmanaged memory slabs, ValueTask-based async, lock-free SPSC/MPSC queues, and object pooling eliminate GC pauses on the critical path."
    link="docs/guides/zero-allocation/"
  >}}
  {{< hextra/feature-card
    title="Reactor Pattern"
    subtitle="One acceptor thread distributes connections round-robin across N reactor threads. Each reactor owns its own io_uring instance and connection map with zero contention."
    link="docs/architecture/reactor-pattern/"
  >}}
  {{< hextra/feature-card
    title="Scalable"
    subtitle="Scale from a single reactor to dozens. Each reactor independently manages thousands of concurrent connections with configurable buffer rings and CQE batching."
    link="docs/getting-started/configuration/"
  >}}
  {{< hextra/feature-card
    title="Flexible API"
    subtitle="High-level ReadOnlySequence APIs for easy parsing, low-level RingItem access for maximum control, IBufferWriter for pipelined writes, and a Stream adapter for BCL compatibility."
    link="docs/api-reference/"
  >}}
  {{< hextra/feature-card
    title="Production Ready"
    subtitle="Includes a TechEmpower benchmark HTTP server, AOT-compatible, ships bundled native libraries for glibc and musl, and is available on NuGet."
    link="docs/guides/http-server/"
  >}}
{{< /hextra/feature-grid >}}
