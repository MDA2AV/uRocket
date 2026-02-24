---
title: Configuration Reference
weight: 5
---

Complete reference for all configuration types in zerg.

## EngineOptions

Top-level configuration passed to `new Engine(options)`.

```csharp
public class EngineOptions
{
    public int ReactorCount { get; init; } = 1;
    public string Ip { get; init; } = "0.0.0.0";
    public ushort Port { get; init; } = 8080;
    public int Backlog { get; init; } = 65535;
    public AcceptorConfig AcceptorConfig { get; init; } = new();
    public ReactorConfig[] ReactorConfigs { get; set; } = null!;
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ReactorCount` | `int` | `1` | Number of reactor event-loop threads. Each gets its own `io_uring`. |
| `Ip` | `string` | `"0.0.0.0"` | Bind address. `"0.0.0.0"` for all IPv4, `"::"` for all IPv6. |
| `Port` | `ushort` | `8080` | TCP listen port. |
| `Backlog` | `int` | `65535` | Kernel listen backlog (pending connection queue size). |
| `AcceptorConfig` | `AcceptorConfig` | `new()` | Acceptor ring configuration. |
| `ReactorConfigs` | `ReactorConfig[]` | `null` | Per-reactor configs. Auto-filled with defaults if null. |

## ReactorConfig

Per-reactor configuration. Sealed record with default values.

```csharp
public sealed record ReactorConfig(
    uint RingFlags = IORING_SETUP_SINGLE_ISSUER | IORING_SETUP_DEFER_TASKRUN,
    int SqCpuThread = -1,
    uint SqThreadIdleMs = 100,
    uint RingEntries = 8 * 1024,
    int RecvBufferSize = 32 * 1024,
    int BufferRingEntries = 16 * 1024,
    int BatchCqes = 4096,
    int MaxConnectionsPerReactor = 8 * 1024,
    long CqTimeout = 1_000_000
);
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RingFlags` | `uint` | `SINGLE_ISSUER \| DEFER_TASKRUN` | `io_uring` setup flags. Controls kernel behavior. |
| `SqCpuThread` | `int` | `-1` | CPU core for SQPOLL thread. `-1` = kernel decides. Only with `SQPOLL`. |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout (ms) before kernel thread sleeps. |
| `RingEntries` | `uint` | `8192` | SQ/CQ ring size. Max in-flight I/O operations. |
| `RecvBufferSize` | `int` | `32768` | Bytes per receive buffer. Larger = fewer buffers for big payloads. |
| `BufferRingEntries` | `int` | `16384` | Number of recv buffers in buffer ring. Must be power of 2. |
| `BatchCqes` | `int` | `4096` | Max CQEs processed per event loop iteration. |
| `MaxConnectionsPerReactor` | `int` | `8192` | Connection limit per reactor. Should be <= `RingEntries`. |
| `CqTimeout` | `long` | `1000000` | Wait timeout in nanoseconds (1 ms). Lower = lower latency, more CPU. |

## AcceptorConfig

Acceptor thread configuration. Sealed record with default values.

```csharp
public sealed record AcceptorConfig(
    uint RingFlags = 0,
    int SqCpuThread = -1,
    uint SqThreadIdleMs = 100,
    uint RingEntries = 8 * 1024,
    uint BatchSqes = 4096,
    long CqTimeout = 100_000_000,
    IPVersion IPVersion = IPVersion.IPv6DualStack
);
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RingFlags` | `uint` | `0` | `io_uring` setup flags for the acceptor ring. |
| `SqCpuThread` | `int` | `-1` | CPU core for SQPOLL thread. |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout (ms). |
| `RingEntries` | `uint` | `8192` | SQ/CQ size. Bounds accept CQE burst capacity. |
| `BatchSqes` | `uint` | `4096` | Max accept completions per loop iteration. |
| `CqTimeout` | `long` | `100000000` | Wait timeout in ns (100 ms). Higher than reactor since accepts are bursty. |
| `IPVersion` | `IPVersion` | `IPv6DualStack` | IP stack for the listening socket. |

## IPVersion

```csharp
public enum IPVersion
{
    IPv4Only,
    IPv6DualStack
}
```

| Value | Socket | Description |
|-------|--------|-------------|
| `IPv4Only` | `AF_INET` | IPv4 only. |
| `IPv6DualStack` | `AF_INET6` (`V6ONLY=0`) | Accepts both IPv4 and IPv6. IPv4 clients appear as `::ffff:a.b.c.d`. |

## io_uring Setup Flags

Available constants from the `ABI` class for use in `RingFlags`:

| Flag | Value | Description |
|------|-------|-------------|
| `IORING_SETUP_IOPOLL` | `1 << 0` | Polling-based I/O (not recommended for sockets). |
| `IORING_SETUP_SQPOLL` | `1 << 1` | Kernel thread polls SQ. Eliminates submit syscall. |
| `IORING_SETUP_SQ_AFF` | `1 << 2` | Pin SQPOLL thread to `SqCpuThread`. |
| `IORING_SETUP_CQSIZE` | `1 << 3` | Custom CQ size (separate from SQ). |
| `IORING_SETUP_CLAMP` | `1 << 4` | Clamp queue sizes to kernel limits. |
| `IORING_SETUP_SINGLE_ISSUER` | `1 << 12` | Single thread submits. Reduces kernel locking. |
| `IORING_SETUP_DEFER_TASKRUN` | `1 << 13` | Defer task_work. Reduces latency spikes. |
| `IORING_SETUP_NO_MMAP` | `1 << 14` | Don't mmap rings (use registered buffers). |
| `IORING_SETUP_REGISTERED_FD_ONLY` | `1 << 15` | Only allow registered file descriptors. |

## Configuration Examples

### High-Throughput Server

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = Environment.ProcessorCount,
    ReactorConfigs = Enumerable.Range(0, Environment.ProcessorCount)
        .Select(_ => new ReactorConfig(
            RingEntries: 16384,
            RecvBufferSize: 64 * 1024,
            BufferRingEntries: 32 * 1024,
            BatchCqes: 8192,
            MaxConnectionsPerReactor: 16384,
            CqTimeout: 500_000  // 0.5 ms
        )).ToArray()
});
```

### Low-Latency Server with SQPOLL

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 4,
    ReactorConfigs = Enumerable.Range(0, 4)
        .Select(i => new ReactorConfig(
            RingFlags: ABI.IORING_SETUP_SQPOLL
                     | ABI.IORING_SETUP_SQ_AFF
                     | ABI.IORING_SETUP_SINGLE_ISSUER,
            SqCpuThread: i + 4,      // pin SQPOLL threads to CPUs 4-7
            SqThreadIdleMs: 200,
            CqTimeout: 100_000        // 0.1 ms
        )).ToArray()
});
```

### Minimal Memory Footprint

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 1,
    ReactorConfigs = [new ReactorConfig(
        RecvBufferSize: 4096,
        BufferRingEntries: 1024,
        MaxConnectionsPerReactor: 256,
        RingEntries: 512
    )]
});
```
