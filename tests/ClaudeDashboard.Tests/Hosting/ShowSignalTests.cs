using System.IO;
using System.Net;
using System.Net.Sockets;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// How a second instance hands over to the resident one (Impl §5.3).
/// </summary>
/// <remarks>
/// Against the real <c>/show</c> endpoint, because the whole point of this path is that it reuses
/// ingress rather than opening a channel of its own — a stub would be a second channel wearing
/// the first one's name.
/// </remarks>
public sealed class ShowSignalTests
{
    private const string Token = "show-signal-token";

    /// <summary>The ordinary duplicate launch: the resident instance raises its window.</summary>
    /// <remarks>
    /// The outcome is asserted together with the count of times the window was actually asked to
    /// surface. <see cref="ShowSignalOutcome.Shown"/> on its own is a claim about a status code,
    /// and a <c>/show</c> that answered 200 without doing anything — which is exactly what this
    /// endpoint did before T1.15 wired it — would satisfy it.
    /// </remarks>
    [Fact]
    public async Task An_authorised_signal_raises_the_resident_window()
    {
        var shown = 0;
        var port = UnusedPort();
        await using var app = Ingress(port, new IngressToken(Token), () => Interlocked.Increment(ref shown));
        await app.StartAsync();

        var result = ShowSignal.Send(port, Token, TimeSpan.FromSeconds(10));

        Assert.Equal(ShowSignalOutcome.Shown, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref shown));

        await app.StopAsync();
    }

    /// <summary>
    /// A signal with the wrong token is refused, and the window stays where it is.
    /// </summary>
    /// <remarks>
    /// Same gate name means the same data folder means the same token, so this should not happen
    /// — which is why it is worth an Error in the log rather than a shrug. The second instance
    /// still exits: two dashboards on one data folder is what the gate exists to prevent. What it
    /// must not do is exit silently, and <see cref="ShowSignalOutcome.Rejected"/> is what lets
    /// the caller tell that apart from success.
    /// </remarks>
    [Fact]
    public async Task A_signal_with_the_wrong_token_is_rejected_and_raises_nothing()
    {
        var shown = 0;
        var port = UnusedPort();
        await using var app = Ingress(port, new IngressToken(Token), () => Interlocked.Increment(ref shown));
        await app.StartAsync();

        var result = ShowSignal.Send(port, "not-the-token", TimeSpan.FromSeconds(10));

        Assert.Equal(ShowSignalOutcome.Rejected, result.Outcome);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, Volatile.Read(ref shown));

        await app.StopAsync();
    }

    /// <summary>A dashboard with no token configured accepts a signal that presents none.</summary>
    /// <remarks>
    /// The token is optional (Impl §3.4), so the handover has to work without one. Paired with
    /// the test above so that neither "always accepts" nor "always refuses" satisfies both.
    /// </remarks>
    [Fact]
    public async Task A_signal_needs_no_token_when_the_resident_instance_has_none()
    {
        var shown = 0;
        var port = UnusedPort();
        await using var app = Ingress(port, new IngressToken(expected: null), () => Interlocked.Increment(ref shown));
        await app.StartAsync();

        var result = ShowSignal.Send(port, token: null, TimeSpan.FromSeconds(10));

        Assert.Equal(ShowSignalOutcome.Shown, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref shown));

        await app.StopAsync();
    }

    /// <summary>Nothing is listening, so there is nothing to hand over to.</summary>
    [Fact]
    public void A_signal_to_a_port_nobody_holds_is_unreachable()
    {
        var result = ShowSignal.Send(UnusedPort(), Token, TimeSpan.FromMilliseconds(500));

        Assert.Equal(ShowSignalOutcome.Unreachable, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }

    /// <summary>Something answered, but not with anything that means the window came up.</summary>
    /// <remarks>
    /// Separate from <see cref="ShowSignalOutcome.Rejected"/> because the two have different
    /// diagnoses: a 401 says the token is wrong, and anything else says whatever is on the port
    /// is not the dashboard this process expected.
    /// </remarks>
    [Fact]
    public void A_signal_answered_with_something_else_has_failed()
    {
        using var server = CannedServer.Answering("500 Internal Server Error", "text/plain", "no");

        var result = ShowSignal.Send(server.Port, Token, TimeSpan.FromSeconds(10));

        Assert.Equal(ShowSignalOutcome.Failed, result.Outcome);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
    }

    /// <summary>A resident instance that accepts and never answers must not hang the launcher.</summary>
    /// <remarks>
    /// The second instance has no window and no console. If this blocked, the operator would have
    /// a process that appears to do nothing and has to be found and killed.
    /// </remarks>
    [Fact]
    public void A_signal_that_is_never_answered_gives_up()
    {
        using var server = CannedServer.Silent();

        var started = DateTimeOffset.UtcNow;
        var result = ShowSignal.Send(server.Port, Token, TimeSpan.FromMilliseconds(500));
        var took = DateTimeOffset.UtcNow - started;

        Assert.Equal(ShowSignalOutcome.Unreachable, result.Outcome);
        Assert.True(took < TimeSpan.FromSeconds(10), $"The signal took {took}, which is not a bounded wait.");
    }

    private static int UnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static WebApplication Ingress(int port, IngressToken token, Action onShow)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));

        builder.Services.AddSingleton<Serilog.ILogger>(Logger.None);
        builder.Services.AddSingleton(new DashboardPaths(
            Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"))));
        builder.Services.AddSingleton<IClock>(new FakeClock());
        builder.Services.AddSingleton(token);
        builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<IEventSink>(new RecordingEventSink());

        var app = builder.Build();
        app.MapIngress(onShow);
        return app;
    }
}
