[![NuGet](https://img.shields.io/nuget/v/zerg.svg)](https://www.nuget.org/packages/zerg/)

# zerg

**zerg** is an experimental, low-level TCP server framework built in C# on top of Linux `io_uring`. It intentionally avoids "magic" abstraction layers and gives the developer direct control over sockets, buffers, queues, and scheduling.

- **Author:** Diogo Martins
- **License:** MIT
- **Repository:** https://github.com/MDA2AV/uRocket
- **NuGet:** https://www.nuget.org/packages/zerg/
- **Target Frameworks:** .NET 9.0, .NET 10.0

---

## Table of Contents

1. [Requirements](#requirements)
2. [Installation](#installation)
3. [Architecture Overview](#architecture-overview)
4. [Quick Start](#quick-start)
5. [Configuration](#configuration)
6. [Connection API](#connection-api)
7. [Reading Data](#reading-data)
8. [Writing Data](#writing-data)
9. [Examples](#examples)
10. [io_uring Primer](#io_uring-primer)
11. [Performance Tuning](#performance-tuning)
12. [Project Structure](#project-structure)

---

## Requirements

- **Linux** (kernel 5.10+ recommended for stable `io_uring` support)
- **.NET 9.0** or **.NET 10.0** SDK
- **liburing** (the native shim `liburingshim.so` is bundled in the NuGet package for `linux-x64` and `linux-musl-x64`)

---

## Installation

### Via NuGet

```bash
dotnet add package zerg
```

### From Source

```bash
git clone https://github.com/MDA2AV/uRocket.git
cd uRocket
dotnet build
```

### Publishing with AOT

```bash
dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed
```

---

## Architecture Overview

zerg follows a **split architecture** with two thread pools:

```
                    ┌────────────────┐
    Clients ──────► │   Acceptor     │  (1 thread, 1 io_uring)
                    │  multishot     │
                    │  accept loop   │
                    └───┬───┬───┬────┘
                        │   │   │      round-robin distribution
              ┌─────────┘   │   └─────────┐
              ▼             ▼             ▼
        ┌──────────┐  ┌──────────┐  ┌──────────┐
        │ Reactor 0│  │ Reactor 1│  │ Reactor N│  (N threads, N io_urings)
        │ io_uring │  │ io_uring │  │ io_uring │
        │ buf_ring │  │ buf_ring │  │ buf_ring │
        │ conn map │  │ conn map │  │ conn map │
        └──────────┘  └──────────┘  └──────────┘
```

### Acceptor Thread
- Listens on a TCP socket and accepts new connections via `io_uring` multishot accept
- Distributes accepted connections to reactor threads in round-robin order

### Reactor Threads
Each reactor owns:
- Its own `io_uring` instance for recv/send operations
- A pre-allocated **buffer ring** for zero-copy receives
- A dictionary of active connections (fd -> Connection)
- Lock-free MPSC queues for cross-thread coordination

### Key Design Principles
- **No thread contention:** Each connection belongs to exactly one reactor
- **Explicit buffer lifetimes:** Consumers must return buffers to the kernel after processing
- **Allocation-free hot paths:** Uses unmanaged memory, `ValueTask`, and object pooling
- **Multishot operations:** Single submission produces multiple completions

---

## Quick Start

```csharp
using zerg.Engine;
using zerg.Engine.Configs;

var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 1
});
engine.Listen();

var cts = new CancellationTokenSource();

// Graceful shutdown on Enter key
_ = Task.Run(() => {
    Console.ReadLine();
    engine.Stop();
    cts.Cancel();
});

try
{
    while (engine.ServerRunning)
    {
        var connection = await engine.AcceptAsync(cts.Token);
        if (connection is null) continue;

        // Fire-and-forget connection handler
        _ = HandleConnectionAsync(connection);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}
```

### Minimal Connection Handler

```csharp
static async Task HandleConnectionAsync(Connection connection)
{
    while (true)
    {
        var result = await connection.ReadAsync();
        if (result.IsClosed) break;

        // Get received buffers
        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

        // Process data...

        // Return buffers to the kernel
        rings.ReturnRingBuffers(connection.Reactor);

        // Write a response
        connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8);
        await connection.FlushAsync();
        connection.ResetRead();
    }
}
```

---

## Configuration

### EngineOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `ReactorCount` | `int` | `1` | Number of reactor threads to spawn |
| `Ip` | `string` | `"0.0.0.0"` | IP address to bind to |
| `Port` | `ushort` | `8080` | TCP port to listen on |
| `Backlog` | `int` | `65535` | Listen backlog for pending connections |
| `AcceptorConfig` | `AcceptorConfig` | `new()` | Acceptor thread configuration |
| `ReactorConfigs` | `ReactorConfig[]` | `null` | Per-reactor configurations (auto-filled if null) |

### ReactorConfig

| Property | Type | Default | Description |
|---|---|---|---|
| `RingFlags` | `uint` | `SINGLE_ISSUER \| DEFER_TASKRUN` | `io_uring` setup flags |
| `SqCpuThread` | `int` | `-1` | CPU affinity for SQPOLL thread (-1 = kernel decides) |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout before sleeping |
| `RingEntries` | `uint` | `8192` | SQ/CQ size (max in-flight operations) |
| `RecvBufferSize` | `int` | `32768` | Size of each receive buffer in bytes |
| `BufferRingEntries` | `int` | `16384` | Number of pre-allocated recv buffers (must be power of 2) |
| `BatchCqes` | `int` | `4096` | Max CQEs processed per loop iteration |
| `MaxConnectionsPerReactor` | `int` | `8192` | Max concurrent connections per reactor |
| `CqTimeout` | `long` | `1000000` | Wait timeout in nanoseconds (1ms) |

### AcceptorConfig

| Property | Type | Default | Description |
|---|---|---|---|
| `RingFlags` | `uint` | `0` | `io_uring` setup flags |
| `SqCpuThread` | `int` | `-1` | CPU affinity for SQPOLL thread |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout |
| `RingEntries` | `uint` | `8192` | SQ/CQ size |
| `BatchSqes` | `uint` | `4096` | Max accepts processed per loop iteration |
| `CqTimeout` | `long` | `100000000` | Wait timeout in nanoseconds (100ms) |
| `IPVersion` | `IPVersion` | `IPv6DualStack` | IPv4, IPv6, or IPv6DualStack |

### Multi-Reactor Configuration Example

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 12,
    ReactorConfigs = Enumerable.Range(0, 12).Select(_ => new ReactorConfig(
        RecvBufferSize: 64 * 1024,
        BufferRingEntries: 32 * 1024,
        CqTimeout: 500_000
    )).ToArray()
});
```

---

## Connection API

### Engine Lifecycle

```csharp
// Create and start
var engine = new Engine(options);
engine.Listen();

// Accept connections
Connection? conn = await engine.AcceptAsync(cancellationToken);

// Shutdown
engine.Stop();
```

### Connection Properties

| Property | Type | Description |
|---|---|---|
| `ClientFd` | `int` | The OS file descriptor for this connection |
| `Reactor` | `Engine.Reactor` | The reactor that owns this connection |

---

## Reading Data

zerg provides both high-level and low-level read APIs. The core contract is:

1. **Only one `ReadAsync()` can be outstanding per connection at a time**
2. After processing data, **return buffers** to the kernel via `ReturnRing()`
3. Call `ResetRead()` to signal readiness for the next read

### High-Level API

```csharp
// Wait for data
ReadResult result = await connection.ReadAsync();
if (result.IsClosed) return; // Connection was closed

// Get all received buffers as UnmanagedMemoryManager[]
var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

// Create a ReadOnlySequence for easy slicing/parsing
ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();

// Return all buffers when done
rings.ReturnRingBuffers(connection.Reactor);

// Reset for next read
connection.ResetRead();
```

### Low-Level API

For fine-grained control, consume buffers one at a time:

```csharp
ReadResult result = await connection.ReadAsync();
if (result.IsClosed) return;

// Iterate through individual ring buffers
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ReadOnlySpan<byte> data = ring.AsSpan();
    // Process data...
    connection.ReturnRing(ring.BufferId);
}

connection.ResetRead();
```

### ReadResult

| Property | Type | Description |
|---|---|---|
| `TailSnapshot` | `long` | Snapshot of the receive ring tail at read time |
| `IsClosed` | `bool` | Whether the connection was closed |

### RingItem

| Property | Type | Description |
|---|---|---|
| `Ptr` | `byte*` | Pointer to the receive buffer |
| `Length` | `int` | Number of bytes received |
| `BufferId` | `ushort` | Kernel buffer ID (used with `ReturnRing()`) |

---

## Writing Data

### Simple Write (copies data to internal buffer)

```csharp
connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8);
await connection.FlushAsync();
```

### IBufferWriter Interface

```csharp
Span<byte> span = connection.GetSpan(256);
// Write directly into the span...
int bytesWritten = FormatResponse(span);
connection.Advance(bytesWritten);
await connection.FlushAsync();
```

### Write/Flush Lifecycle

1. **Write:** Data is staged in the connection's write buffer
2. **FlushAsync:** Signals the reactor to issue a `send` SQE to the kernel
3. The reactor handles partial sends automatically (resubmits remaining data)
4. The write buffer is reset after the full send completes

---

## Examples

The repository includes two example connection handlers:

### `Rings_as_ReadOnlySpan`

Simplest approach. Gets all snapshot rings and processes them as spans. Good starting point for understanding the API.

```
Examples/ZeroAlloc/Basic/Rings_as_ReadOnlySpan.cs
```

### `Rings_as_ReadOnlySequence`

Same as above but creates a `ReadOnlySequence<byte>` from the rings, which is useful for `SequenceReader<byte>` based parsing.

```
Examples/ZeroAlloc/Basic/Rings_as_ReadOnlySequence.cs
```

---

## io_uring Primer

`io_uring` is a Linux kernel interface for asynchronous I/O based on shared-memory ring buffers:

- **Submission Queue (SQ):** Application writes I/O request descriptors here
- **Completion Queue (CQ):** Kernel writes completion results here
- **Shared Memory:** Both queues live in kernel/user shared memory - most operations require **no syscalls**
- **Batching:** Submit many requests, get many completions with one syscall

### Features Used by zerg

| Feature | Description |
|---|---|
| **Multishot Accept** | Single submission produces a CQE for every new connection |
| **Multishot Recv** | Single submission per connection; kernel fills a buffer from the buffer ring for each packet |
| **Buffer Selection** | Pre-registered buffer pool; kernel picks a buffer and returns its ID in the CQE |
| **SQPOLL** (optional) | Kernel thread polls the SQ, eliminating the submit syscall at the cost of a dedicated CPU core |
| **DEFER_TASKRUN** | Defers kernel task execution for better async/await integration |
| **SINGLE_ISSUER** | Optimizes for single-thread submission (matches reactor model) |

---

## Performance Tuning

### Recv Buffer Configuration

| Tunable | Increase for... | Decrease for... |
|---|---|---|
| `RecvBufferSize` | Large payloads (fewer syscalls) | Low memory usage, small messages |
| `BufferRingEntries` | Many concurrent connections | Lower memory footprint |

### CQE Batching

| Tunable | Higher value | Lower value |
|---|---|---|
| `BatchCqes` | Better throughput under load | Lower per-loop latency |

### Timeout

| Tunable | Lower value (e.g. 1ms) | Higher value (e.g. 100ms) |
|---|---|---|
| `CqTimeout` | Lower tail latency, higher CPU | Lower CPU usage, higher tail latency |

### Ring Flags

| Flag | Effect |
|---|---|
| `IORING_SETUP_SQPOLL` | Kernel thread polls SQ; saves syscalls but dedicates a CPU core |
| `IORING_SETUP_DEFER_TASKRUN` | Better for async/await integration (default) |
| `IORING_SETUP_SQ_AFF` | Pin SQPOLL kernel thread to a specific CPU core |
| `IORING_SETUP_SINGLE_ISSUER` | Optimize for single-thread submission (default) |

---

## Project Structure

```
zerg/
├── zerg/                             # Core library (NuGet package)
│   ├── ABI/                          # Linux system ABI bindings
│   │   ├── CPU.cs                    # CPU affinity
│   │   ├── Kernel.cs                 # Kernel-level utilities
│   │   ├── LinuxSocket.cs            # Socket syscall wrappers
│   │   └── URing.cs                  # io_uring P/Invoke bindings
│   ├── Connection/                   # Per-connection state and APIs
│   │   ├── Connection.Read.cs        # Read state, IValueTaskSource, async signaling
│   │   ├── Connection.Read.HighLevelApi.cs  # Batch read APIs
│   │   ├── Connection.Read.LowLevelApi.cs   # Low-level streaming APIs
│   │   ├── Connection.Write.cs       # Write buffer, IBufferWriter, Flush
│   │   └── ConnectionStream.cs       # BCL Stream adapter
│   ├── Engine/                       # Reactor pattern implementation
│   │   ├── Engine.cs                 # Main coordinator
│   │   ├── Engine.Config.cs          # Configuration and thread setup
│   │   ├── Engine.Acceptor.cs        # Accept event loop
│   │   ├── Engine.Reactor.cs         # Reactor event loop
│   │   └── Configs/                  # EngineOptions, ReactorConfig, AcceptorConfig
│   ├── Utils/                        # Data structures and helpers
│   │   ├── RingItem.cs               # Received buffer metadata
│   │   ├── ReadResult.cs             # Read snapshot result
│   │   ├── UnmanagedMemoryManager/   # Wraps unmanaged memory as MemoryManager<byte>
│   │   ├── SingleProducerSingleConsumer/  # SPSC ring buffer
│   │   └── MultiProducerSingleConsumer/   # Lock-free MPSC queues
│   └── native/                       # Bundled native libraries
│       ├── linux-x64/liburingshim.so
│       └── linux-musl-x64/liburingshim.so
│
├── Examples/                         # Example applications
│   ├── Program.cs                    # Entry point with engine setup
│   └── ZeroAlloc/Basic/              # Simple read/write patterns
│
├── Playground/                       # Development sandbox
├── TechEmpower/BenchmarkApp/         # HTTP benchmark server
└── Docs/                             # Hextra documentation site
```

### Dependencies

| Dependency | Version | Purpose |
|---|---|---|
| `Microsoft.Extensions.ObjectPool` | 10.0.2 | Connection object pooling |
| `liburingshim.so` | bundled | C shim bridging P/Invoke to liburing |

---

## Threading Model

```
┌─────────────┐
│  Acceptor   │  Thread 1: Accepts connections via io_uring
│  Thread     │  Distributes FDs round-robin to reactors
└──────┬──────┘
       │ ConcurrentQueue<int> per reactor
       ▼
┌─────────────┐  ┌─────────────┐  ┌─────────────┐
│  Reactor 0  │  │  Reactor 1  │  │  Reactor N  │  N threads: recv/send via io_uring
└──────┬──────┘  └──────┬──────┘  └──────┬──────┘
       │                │                │
       ▼                ▼                ▼
┌─────────────┐  ┌─────────────┐  ┌─────────────┐
│  Handler    │  │  Handler    │  │  Handler    │  User async Tasks
│  Tasks      │  │  Tasks      │  │  Tasks      │  (ReadAsync/Write/FlushAsync)
└─────────────┘  └─────────────┘  └─────────────┘
```

**Thread safety guarantees:**
- Each connection belongs to exactly one reactor (no cross-thread contention)
- MPSC queues handle all cross-thread communication (lock-free)
- `Volatile.Read`/`Volatile.Write` and `Interlocked` operations enforce correct memory ordering
- Connection pooling uses generation counters to prevent stale access after reuse

---

## License

MIT License - Copyright (c) 2026 Diogo Martins (MDA2AV)
