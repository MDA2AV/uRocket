---
title: Internals
weight: 5
---

Deep dive into zerg's internal data structures, native interop layer, and memory management.

{{< cards >}}
  {{< card link="spsc-ring" title="SPSC Recv Ring" subtitle="Lock-free single-producer single-consumer ring buffer" >}}
  {{< card link="mpsc-queues" title="MPSC Queues" subtitle="MpscUshortQueue, MpscIntQueue, and MpscRecvRing" >}}
  {{< card link="native-interop" title="Native Interop" subtitle="liburingshim, P/Invoke bindings, and ABI layer" >}}
  {{< card link="memory-management" title="Memory Management" subtitle="Unmanaged slabs, pooling, and UnmanagedMemoryManager" >}}
{{< /cards >}}
