---
title: Installation
weight: 1
---

## NuGet Package

The recommended way to install zerg is via NuGet:

```bash
dotnet add package zerg
```

Or add the package reference directly to your `.csproj`:

```xml
<PackageReference Include="zerg" Version="*" />
```

zerg targets **.NET 8.0**, **.NET 9.0**, and **.NET 10.0**.

## Native Dependencies

zerg ships with bundled native libraries for the `io_uring` shim:

| Runtime | Library |
|---------|---------|
| `linux-x64` (glibc) | `liburingshim.so` |
| `linux-musl-x64` (Alpine) | `liburingshim.so` |

These are included in the NuGet package and copied to your output directory automatically. No manual installation of `liburing` is required.

### Kernel Requirements

zerg requires a Linux kernel with `io_uring` support:

- **Minimum:** Linux 6.1+ (multishot accept, multishot recv, buffer rings, `IORING_SETUP_DEFER_TASKRUN`, `IORING_SETUP_SINGLE_ISSUER`)

You can check your kernel version with:

```bash
uname -r
```

## Build from Source

Clone the repository and build with the .NET SDK:

```bash
git clone https://github.com/MDA2AV/zerg.git
cd zerg
dotnet build
```

The solution file `zerg.sln` includes the core library, examples, and playground projects.

## AOT Compilation

zerg is compatible with Native AOT compilation. The native `liburingshim.so` P/Invoke bindings use static linking-friendly signatures. To publish with AOT:

```bash
dotnet publish -c Release -r linux-x64 /p:PublishAot=true
```

## Managed Dependency

zerg has a single managed dependency:

- `Microsoft.Extensions.ObjectPool` (v10.0.2) -- used for connection pooling

## Project Structure

When you reference zerg, the following projects are available in the solution:

| Project | Description |
|---------|-------------|
| `zerg` | Core library (the NuGet package) |
| `Examples` | Basic usage examples (ReadOnlySpan, ReadOnlySequence) |
| `Playground` | Development sandbox |
