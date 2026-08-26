using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// A listener that gives one canned HTTP reply, or none at all.
/// </summary>
/// <remarks>
/// <para>
/// A raw <see cref="TcpListener"/> rather than a second Kestrel, because two of these cases
/// are things a well-behaved HTTP server will not do: answer a health probe with a plain
/// string, and accept a connection and never write a byte.
/// </para>
/// <para>
/// <strong>Its preconditions are observable, and that is the point (T1.15b).</strong> This used to
/// start its listener and hand the accept loop to a fire-and-forget <c>Task.Run</c>, so a test
/// could probe before the loop was running and had no way to tell afterwards. When
/// <c>A_socket_that_never_answers_is_silent</c> failed about once in ten Release runs, the
/// evidence said only "expected Silent, got Unrecognised" — nothing about whether this server had
/// accepted anything, faulted, or never started. The constructor now waits until the loop is
/// actually running, and <see cref="Accepted"/> and <see cref="Fault"/> let a test assert what it
/// was depending on instead of hoping for it.
/// </para>
/// <para>
/// The cause of that failure was never identified: it did not reproduce in 400 isolated probes or
/// 12 full Release suites. So this is not a fix aimed at a known mechanism — it removes one class
/// of race outright and makes the remaining possibilities <em>reportable</em>. If it recurs, the
/// failure now says which precondition broke.
/// </para>
/// </remarks>
internal sealed class CannedServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _accepting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loop;

    private int _accepted;

    private CannedServer(string? response)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _loop = Task.Run(() => ServeAsync(response, _stopping.Token));

        // Do not return until the accept loop is genuinely running. Scheduling it was never the
        // same as running it, and every caller of this type was written as though it were.
        if (!_accepting.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException(
                "The canned server's accept loop did not start within ten seconds. Nothing that " +
                "follows would be a test of the thing it claims to test.");
        }
    }

    /// <summary>The ephemeral port this server holds.</summary>
    public int Port { get; }

    /// <summary>How many connections the accept loop has taken.</summary>
    /// <remarks>
    /// A test that depends on this server having accepted its connection should say so. "The probe
    /// got the wrong answer" and "the server never accepted" are different findings, and without
    /// this they look identical.
    /// </remarks>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>What ended the accept loop, if anything did. Null while it is healthy.</summary>
    /// <remarks>
    /// The loop swallows the exceptions that mean "shutting down" so that a disposal is not a
    /// failure. Swallowing them silently is what made the original flake unreadable, so the last
    /// one is kept here — a loop that ended early closes its accepted connection, and a closed
    /// connection is exactly what a probe would report as a stranger rather than as silence.
    /// </remarks>
    public Exception? Fault { get; private set; }

    public static CannedServer Answering(string status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);

        return new CannedServer(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n{body}");
    }

    /// <summary>Accepts, and writes nothing, for as long as the client will wait.</summary>
    public static CannedServer Silent() => new(response: null);

    /// <summary>
    /// Everything a failing test needs to say what this server was doing, in one line.
    /// </summary>
    public string State =>
        $"port={Port} accepted={Accepted} loopStatus={_loop.Status} fault={Fault?.GetType().Name ?? "none"}" +
        (Fault is null ? string.Empty : $": {Fault.Message}");

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();

        // Let the loop finish before the token source goes; otherwise disposal races the very
        // thing it is trying to shut down, and the resulting ObjectDisposedException lands in
        // Fault and looks like a defect.
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Already recorded in Fault.
        }

        _stopping.Dispose();
    }

    private async Task ServeAsync(string? response, CancellationToken stopping)
    {
        try
        {
            // Signalled before the first accept, which is the earliest moment at which this loop
            // is genuinely waiting for a connection rather than merely scheduled to.
            _accepting.TrySetResult();

            while (!stopping.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);

                Interlocked.Increment(ref _accepted);

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
            // Shutting down, or the client went away. Either is the end of this server's job —
            // but recorded rather than discarded, because "the loop ended" and "the loop is
            // waiting" produce very different behaviour at the client and used to be
            // indistinguishable from outside.
            Fault = ex;
        }
        finally
        {
            // If the loop never reached its first accept, nothing must be left waiting on the
            // constructor's gate.
            _accepting.TrySetResult();
        }
    }
}
