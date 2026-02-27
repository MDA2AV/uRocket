using zerg;

namespace Examples.Stream;

/// <summary>
/// HTTP handler using <see cref="ConnectionStream"/>.
///
/// This adapter copies received bytes into a managed buffer on every ReadAsync.
/// Useful for bridging APIs that only accept <see cref="System.IO.Stream"/>,
/// but carries a per-read copy cost compared to the PipeReader and raw
/// Connection paths.
/// </summary>
internal sealed class StreamExample
{
    internal static async Task HandleConnectionAsync(Connection connection)
    {
        var stream = new ConnectionStream(connection);
        var buf = new byte[4096];

        while (true)
        {
            // ReadAsync copies received data from io_uring kernel buffers
            // into buf[], then immediately returns those buffers to the
            // reactor pool. One copy per read.
            var n = await stream.ReadAsync(buf);
            if (n == 0)
                break;

            // Write the response.
            await stream.WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8.ToArray());
            await stream.FlushAsync();
        }
    }
}
