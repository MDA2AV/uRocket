---
title: Engine
weight: 1
---

The `Engine` class is the entry point for zerg. It creates the acceptor and reactor threads, manages the listening socket, and provides the `AcceptAsync` API for receiving new connections.

## Class Definition

```csharp
namespace zerg.Engine;

public partial class Engine
```

## Constructors

### `Engine()`

Creates an engine with default `EngineOptions`.

```csharp
var engine = new Engine();
// Defaults: Port=8080, ReactorCount=1, Ip="0.0.0.0"
```

### `Engine(EngineOptions options)`

Creates an engine with custom configuration.

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = Environment.ProcessorCount,
    Backlog = 65535
});
```

If `options.ReactorConfigs` is null, it is auto-initialized with default `ReactorConfig` for each reactor.

## Properties

### `ServerRunning`

```csharp
public bool ServerRunning { get; private set; }
```

Global flag checked by the acceptor and all reactor threads. When `Stop()` is called, this becomes `false` and all event loops exit gracefully.

### `SingleAcceptor`

```csharp
public Acceptor SingleAcceptor { get; set; }
```

The acceptor instance responsible for listening and distributing connections.

### `Reactors`

```csharp
public Reactor[] Reactors { get; set; }
```

Array of reactor instances. Each reactor owns its own `io_uring`, buffer ring, and connection map. Index corresponds to reactor ID.

### `Connections`

```csharp
public Dictionary<int, Connection>[] Connections { get; set; }
```

Per-reactor connection dictionaries mapping file descriptors to `Connection` objects.

### `Options`

```csharp
public EngineOptions Options { get; }
```

The engine configuration. Read-only after construction.

## Methods

### `Listen()`

```csharp
public void Listen()
```

Starts the engine:

1. Creates the acceptor with the configured `AcceptorConfig`
2. Creates N reactor instances (one per `ReactorCount`)
3. Initializes per-reactor `ConcurrentQueue<int>` for fd distribution
4. Initializes per-reactor connection dictionaries
5. Starts reactor threads (each enters its event loop)
6. Starts the acceptor thread (arms multishot accept)
7. Prints diagnostics to console

After `Listen()` returns, the server is accepting connections. Call `AcceptAsync()` to receive them.

### `AcceptAsync(CancellationToken)`

```csharp
public async ValueTask<Connection?> AcceptAsync(CancellationToken cancellationToken = default)
```

Waits for the next accepted connection and returns the fully registered `Connection` object. The connection is already assigned to a reactor and has multishot recv armed by the time this method returns.

Returns `null` if the server is shutting down or the cancellation token is triggered.

**Usage:**

```csharp
while (engine.ServerRunning)
{
    var connection = await engine.AcceptAsync(cts.Token);
    if (connection is null) continue;

    _ = HandleConnectionAsync(connection);
}
```

**Important:** `AcceptAsync` blocks until a connection is available. The returned connection is ready for immediate `ReadAsync()`.

### `Stop()`

```csharp
public void Stop()
```

Signals all event loops (acceptor + reactors) to exit. Sets `ServerRunning = false`. This is a non-blocking signal -- threads will stop once they observe the flag change on their next loop iteration.

After calling `Stop()`:
- The acceptor closes the listening socket and destroys its ring
- Each reactor closes all active connections, frees buffer rings, and destroys its ring
- Any pending `AcceptAsync()` returns null

## Nested Types

### `Acceptor`

The acceptor manages the listening socket and multishot accept. Created internally by `Listen()`.

### `Reactor`

Each reactor manages an `io_uring` instance, buffer ring, and a set of connections. See [Threading Model](../../architecture/threading-model/) for how reactors interact with handlers.

Key reactor methods (internal, called by Connection):

| Method | Description |
|--------|-------------|
| `EnqueueReturnQ(ushort bufferId)` | Queue a buffer ID for return to the kernel buffer ring |
| `EnqueueFlush(int clientFd)` | Queue a connection for send by the reactor |

## Static Fields

### `ReactorConnectionCounts`

```csharp
static long[] ReactorConnectionCounts
```

Per-reactor connection counters for metrics and diagnostics. Index corresponds to reactor ID.

## Example: Full Server Lifecycle

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 4
});

engine.Listen();
Console.WriteLine("Server started on :8080 with 4 reactors");

var cts = new CancellationTokenSource();

// Shutdown hook
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    engine.Stop();
    cts.Cancel();
};

try
{
    while (engine.ServerRunning)
    {
        var conn = await engine.AcceptAsync(cts.Token);
        if (conn is null) continue;
        _ = HandleConnectionAsync(conn);
    }
}
catch (OperationCanceledException) { }

Console.WriteLine("Server stopped.");
```
