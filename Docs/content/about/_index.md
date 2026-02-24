---
title: About
---

## zerg

**zerg** (uR(ing)(S)ocket) is an experimental, high-performance TCP server framework for C# built directly on Linux `io_uring`.

It provides low-level control over sockets, buffers, queues, and scheduling with a focus on zero-allocation hot paths and maximum throughput.

## Author

Diogo Martins

## License

MIT License

## Links

- [GitHub Repository](https://github.com/MDA2AV/uRocket)
- [NuGet Package](https://www.nuget.org/packages/zerg)

## Version

Current release: **v0.3.12**

Target frameworks: .NET 9.0, .NET 10.0

## Acknowledgements

zerg builds on the following:

- [liburing](https://github.com/axboe/liburing) -- userspace library for `io_uring`
- [io_uring](https://kernel.dk/io_uring.pdf) -- Linux kernel async I/O interface by Jens Axboe
- Dmitry Vyukov's [bounded MPMC queue](https://www.1024cores.net/home/lock-free-algorithms/queues/bounded-mpmc-queue) -- basis for the MPSC queue implementations
