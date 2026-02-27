using System.Buffers;
using zerg;

namespace Examples.PipeReader;

/// <summary>
/// HTTP handler using <see cref="ConnectionPipeReader"/>.
///
/// Compared to the ZeroAlloc examples that use the Connection API directly,
/// this handler:
///   - Receives data as a zero-copy <see cref="ReadOnlySequence{T}"/> backed
///     by the original io_uring kernel buffers — no copy into a caller buffer.
///   - Uses <see cref="ConnectionPipeReader.AdvanceTo"/> for explicit buffer lifecycle:
///     kernel buffers are returned to the reactor only when consumed.
///   - Supports partial consumption: if a recv boundary splits a message,
///     the unconsumed prefix stays buffered until the next ReadAsync.
/// </summary>
internal sealed class PipeReaderExample
{
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        var reader = new ConnectionPipeReader(connection);

        while (true)
        {
            // ReadAsync returns a ReadOnlySequence<byte> backed by the original
            // io_uring kernel buffers — zero copies. Kernel buffers stay held
            // until AdvanceTo releases them.
            var result = await reader.ReadAsync();
            if (result.IsCompleted)
                break;

            var buffer = result.Buffer;

            // Mark everything as consumed. This returns the kernel buffers
            // to the reactor pool.
            //
            // For protocol parsing, you can consume only a prefix:
            //   reader.AdvanceTo(consumed: partialPos, examined: buffer.End);
            // The unconsumed remainder stays buffered for the next ReadAsync.
            reader.AdvanceTo(buffer.End);

            // Write the response.
            var msg =
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8;
            connection.Write(msg);

            await connection.FlushAsync();
        }

        reader.Complete();
    }
}
