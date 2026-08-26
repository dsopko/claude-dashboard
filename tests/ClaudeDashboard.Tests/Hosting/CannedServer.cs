using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClaudeDashboard.Tests.Hosting;


/// <summary>
/// A listener that gives one canned HTTP reply, or none at all.
/// </summary>
/// <remarks>
/// A raw <see cref="TcpListener"/> rather than a second Kestrel, because two of these cases
/// are things a well-behaved HTTP server will not do: answer a health probe with a plain
/// string, and accept a connection and never write a byte.
/// </remarks>
internal sealed class CannedServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();

    private CannedServer(string? response)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(() => ServeAsync(response, _stopping.Token));
    }

    public int Port { get; }

    public static CannedServer Answering(string status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);

        return new CannedServer(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n{body}");
    }

    /// <summary>Accepts, and writes nothing, for as long as the client will wait.</summary>
    public static CannedServer Silent() => new(response: null);

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
    }

    private async Task ServeAsync(string? response, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
                using var stream = client.GetStream();

                if (response is null)
                {
                    // Hold the connection open and say nothing until the probe gives up.
                    await Task.Delay(Timeout.InfiniteTimeSpan, stopping).ConfigureAwait(false);
                    continue;
                }

                var buffer = new byte[4096];
                await stream.ReadAsync(buffer, stopping).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), stopping).ConfigureAwait(false);
                await stream.FlushAsync(stopping).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            // Shutting down, or the client went away. Either is the end of this server's job.
        }
    }
}
