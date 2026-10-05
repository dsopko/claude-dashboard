using System.IO;
using System.Net;
using System.Net.Sockets;
using ClaudeDashboard.App.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// How a test starts a real copy of the dashboard on a port that it keeps (T1.77, issue #120).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a choice alone is not enough.</strong> A port that a test asks Windows for and lets go is free at
/// that moment only. Tests run side by side, and another one can take it before this test's host listens on it.
/// The host then cannot hear on it (the pin is taken, T1.57), or its start throws, and the test's request is
/// refused. Seen once in the T1.75 review as "connection actively refused".
/// </para>
/// <para>
/// <strong>So a start chooses again when the port was taken.</strong> <see cref="StartAsync"/> pins a port that was
/// free a moment ago, builds the host over it, and starts it only if the host's own probe found the port free; a
/// host whose start then fails to bind is stopped. Either way it chooses a new port and builds again, up to
/// <see cref="Attempts"/> times. A host that is not started writes no run row and sends no hook, so a test sees
/// only the start that kept its port.
/// </para>
/// <para>
/// A host that is meant not to hear (ingress unavailable) listens on a port that Windows chooses at the moment it
/// listens (T1.8), so it has no race and needs none of this.
/// </para>
/// </remarks>
internal static class TestPorts
{
    /// <summary>How many times a start chooses a port before it gives up.</summary>
    internal const int Attempts = 5;

    /// <summary>
    /// A loopback port that nothing listened on a moment ago, already let go. <strong>Never to start a host on:</strong>
    /// only for a settings file whose host is built and not started, or for a host that does not listen on it.
    /// </summary>
    public static int Unbound()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Builds and starts a host on a pinned loopback port that it keeps: a port that is taken before the start is
    /// chosen again (see the remarks on this type).
    /// </summary>
    /// <param name="build">Builds the host over a pinned port: it saves the port in the settings, and builds.</param>
    /// <param name="takeFirst">
    /// For the test of this helper only: runs after a port is chosen and before the host is built, as another test
    /// that takes the port would.
    /// </param>
    /// <param name="takeBeforeStart">For the test of this helper only: the same, after the host is built and before it starts.</param>
    /// <returns>
    /// The started host, the port it listens on, and how many hosts were built to get it: each build writes its start-up
    /// lines to the data folder's log, so a test that counts one of them expects one for each build.
    /// </returns>
    public static async Task<(WebApplication Host, int Port, int Builds)> StartAsync(
        Func<int, WebApplication> build,
        Action<int>? takeFirst = null,
        Action<int>? takeBeforeStart = null)
    {
        ArgumentNullException.ThrowIfNull(build);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var port = Unbound();
            takeFirst?.Invoke(port);

            var host = build(port);

            if (CanHear(host, port))
            {
                takeBeforeStart?.Invoke(port);

                try
                {
                    await host.StartAsync();
                    return (host, port, attempt);
                }
                catch (Exception ex) when (TakenAtStart(ex))
                {
                    // Taken after the host's probe and before its bind: stop what started, and choose again.
                    await Stopped(host);
                }
            }

            Discarded(host);
            await host.DisposeAsync();
        }

        throw new InvalidOperationException($"No test host kept a port in {Attempts} attempts.");
    }

    /// <summary>The same as <see cref="StartAsync"/>, with a blocking start, for a host built on the UI thread.</summary>
    public static (WebApplication Host, int Port) Start(Func<int, WebApplication> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var port = Unbound();
            var host = build(port);

            if (CanHear(host, port))
            {
                try
                {
                    host.Start();
                    return (host, port);
                }
                catch (Exception ex) when (TakenAtStart(ex))
                {
                    Stopped(host).GetAwaiter().GetResult();
                }
            }

            Discarded(host);
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        throw new InvalidOperationException($"No test host kept a port in {Attempts} attempts.");
    }

    /// <summary>Whether the host's own probe found its pinned port free, so it will listen there.</summary>
    private static bool CanHear(WebApplication host, int port) =>
        host.Services.GetRequiredService<IngressStatus>() is { CanReceiveHooks: true } status && status.Port == port;

    /// <summary>Kestrel's bind failure: an address in use, alone or wrapped in an <see cref="IOException"/>.</summary>
    private static bool TakenAtStart(Exception ex) =>
        ex is AddressInUseException || (ex is IOException && ex.InnerException is AddressInUseException);

    /// <summary>
    /// Closes the logger of a host that did not keep its port (the T1.77 review). <c>AppHost.Build</c> makes a Serilog
    /// logger that the container does not dispose, and a host that never ran its stop holds the log file open: the
    /// test could not then delete its scratch folder.
    /// </summary>
    private static void Discarded(WebApplication host) =>
        (host.Services.GetService<Serilog.ILogger>() as IDisposable)?.Dispose();

    private static async Task Stopped(WebApplication host)
    {
        try
        {
            await host.StopAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // A host whose start failed may have nothing to stop.
        }
    }
}
