using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
/// Who holds the ingress port, against real sockets (Impl §3.2, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Real listeners, not a stubbed transport. Every distinction this makes is a property of a
/// socket — refused against accepted, answered against silent, our body against somebody else's
/// — and a fake would assert none of them.
/// </para>
/// <para>
/// <strong>The four unhappy cases get four tests, deliberately not one.</strong> They all end at
/// the same startup action, so a build that lumped every non-<c>ok</c> answer together would
/// behave correctly today and be wrong the moment the actions diverge. Separate code paths,
/// separate tests.
/// </para>
/// </remarks>
public sealed class HealthProbeTests
{
    private const string OurGate = @"Local\ClaudeDashboard.SingleInstance.aaaaaaaaaaaaaaaa";
    private const string TheirGate = @"Local\ClaudeDashboard.SingleInstance.bbbbbbbbbbbbbbbb";

    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(750);

    /// <summary>Nothing is there — and that has to be decided quickly, not eventually.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The timing assertion is the substance of this test, not decoration.</strong> A
    /// probe that asks "is anyone there" by connecting has to make one timeout do two opposing
    /// jobs: bound a stranger that accepts and never replies, and outlast a refusal. Where
    /// refusal is the slower of the two there is no value that satisfies both, and the losing
    /// case is a free port read as a stranger — the ordinary first start, coming up deaf on a
    /// port nobody had taken. A bind attempt has no such number.
    /// </para>
    /// <para>
    /// This machine is one where refusal is the slower: about 2045 ms to refuse a loopback
    /// connect on either address family, against well under a millisecond to connect to an open
    /// port or to attempt the bind. That is why the timeout passed here is deliberately shorter
    /// than a refusal takes. Do not read the figure as a property of Windows — measure elsewhere
    /// and it may be a fraction of a millisecond, and the design still holds, because the
    /// argument above needs no measurement.
    /// </para>
    /// <para>
    /// A build that goes back to connecting fails this on the outcome; one that connects with a
    /// generous timeout instead passes the outcome and fails the duration.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unused_port_is_free_and_is_known_to_be_without_waiting()
    {
        var started = DateTimeOffset.UtcNow;
        var result = HealthProbe.Probe(UnusedPort(), OurGate, Brief);
        var took = DateTimeOffset.UtcNow - started;

        Assert.Equal(PortOccupant.Free, result.Occupant);
        Assert.True(
            took < TimeSpan.FromMilliseconds(500),
            $"Deciding a free port took {took}. It must not depend on how long a refused connect takes.");
    }

    /// <summary>
    /// 4a. A dashboard on our data folder — the real endpoint, not a canned reply.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="IngressEndpoints.MapIngress"/> over a real
    /// <see cref="DashboardPaths"/>, so this exercises the same code that answers a live probe.
    /// The gate name is derived from the root on both sides, which is what makes "ours" mean the
    /// same thing to the endpoint and to the prober.
    /// </remarks>
    [Fact]
    public async Task A_dashboard_on_our_data_folder_is_our_instance()
    {
        var root = UniqueRoot();
        var port = UnusedPort();
        await using var app = Ingress(root, port);
        await app.StartAsync();

        var result = HealthProbe.Probe(port, SingleInstanceGate.NameFor(root), Brief);

        Assert.Equal(PortOccupant.OurInstance, result.Occupant);
        Assert.Equal(SingleInstanceGate.NameFor(root), result.Instance);

        await app.StopAsync();
    }

    /// <summary>
    /// 4b. A real dashboard, but another one — another logon session, or another data folder.
    /// </summary>
    /// <remarks>
    /// The same real endpoint as the test above; only the root differs. That is the whole of the
    /// fast-user-switching case: an answer that is healthy, well-formed and correct, and still
    /// not ours to signal.
    /// </remarks>
    [Fact]
    public async Task A_dashboard_on_another_data_folder_is_another_instance()
    {
        var theirRoot = UniqueRoot();
        var port = UnusedPort();
        await using var app = Ingress(theirRoot, port);
        await app.StartAsync();

        var result = HealthProbe.Probe(port, SingleInstanceGate.NameFor(UniqueRoot()), Brief);

        Assert.Equal(PortOccupant.OtherInstance, result.Occupant);
        Assert.Equal(SingleInstanceGate.NameFor(theirRoot), result.Instance);

        await app.StopAsync();
    }

    /// <summary>
    /// 4c. An old build answering the bare text <c>ok</c> — the exact reply the previous
    /// <c>/health</c> gave.
    /// </summary>
    /// <remarks>
    /// It is healthy and it is almost certainly a dashboard, and it still must not be treated as
    /// ours: it cannot say whose it is, and under fast user switching "a dashboard" and "my
    /// dashboard" are different things. This is the case that would pass if the probe looked for
    /// the word <c>ok</c>.
    /// </remarks>
    [Fact]
    public void A_build_that_answers_bare_ok_is_unrecognised()
    {
        using var server = CannedServer.Answering("200 OK", "text/plain", "ok");

        var result = HealthProbe.Probe(server.Port, OurGate, Brief);

        Assert.Equal(PortOccupant.Unrecognised, result.Occupant);
    }

    /// <summary>4c. Well-formed JSON, healthy status, and no identity at all.</summary>
    [Fact]
    public void A_health_answer_with_no_instance_name_is_unrecognised()
    {
        using var server = CannedServer.Answering("200 OK", "application/json", """{"status":"ok"}""");

        var result = HealthProbe.Probe(server.Port, OurGate, Brief);

        Assert.Equal(PortOccupant.Unrecognised, result.Occupant);
    }

    /// <summary>4c. An identity, but the status says it is not well.</summary>
    [Fact]
    public void A_health_answer_that_is_not_ok_is_unrecognised()
    {
        using var server = CannedServer.Answering(
            "200 OK",
            "application/json",
            $$"""{"status":"draining","instance":{{"\"" + OurGate.Replace(@"\", @"\\", StringComparison.Ordinal) + "\""}}}""");

        var result = HealthProbe.Probe(server.Port, OurGate, Brief);

        Assert.Equal(PortOccupant.Unrecognised, result.Occupant);
    }

    /// <summary>4c. Something on the port that is not answering health at all.</summary>
    [Fact]
    public void A_stranger_that_answers_an_error_is_unrecognised()
    {
        using var server = CannedServer.Answering("404 Not Found", "text/plain", "no");

        var result = HealthProbe.Probe(server.Port, OurGate, Brief);

        Assert.Equal(PortOccupant.Unrecognised, result.Occupant);
    }

    /// <summary>
    /// 4d. A socket that accepts and never writes. The timeout is the only thing that ends this.
    /// </summary>
    /// <remarks>
    /// The harder stranger, and the one that proves the timeout works: without a bounded wait,
    /// this hangs startup for as long as the stranger cares to hold the connection open.
    /// </remarks>
    [Fact]
    public void A_socket_that_never_answers_is_silent()
    {
        using var server = CannedServer.Silent();

        var started = DateTimeOffset.UtcNow;
        var result = HealthProbe.Probe(server.Port, OurGate, Brief);
        var took = DateTimeOffset.UtcNow - started;

        Assert.Equal(PortOccupant.Silent, result.Occupant);

        // The outcome alone would also be produced by a probe that waited a minute and gave up.
        // The point of this case is that startup is not held, so the duration is asserted too.
        Assert.True(took < TimeSpan.FromSeconds(10), $"The probe took {took}, which is not a bounded wait.");
    }

    /// <summary>The body a live dashboard answers with is the body the probe reads.</summary>
    /// <remarks>
    /// Pins the two halves of the contract against each other. Either alone can drift: a writer
    /// that changed the field names would still satisfy a reader tested on canned input, and a
    /// reader that stopped checking the status would still satisfy a writer tested on its own.
    /// </remarks>
    [Fact]
    public void The_health_body_this_build_writes_is_one_this_build_reads()
    {
        using var server = CannedServer.Answering(
            "200 OK",
            "application/json",
            HealthProbe.BodyFor(OurGate));

        Assert.Equal(PortOccupant.OurInstance, HealthProbe.Probe(server.Port, OurGate, Brief).Occupant);
        Assert.Equal(PortOccupant.OtherInstance, HealthProbe.Probe(server.Port, TheirGate, Brief).Occupant);
    }

    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private static int UnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The real ingress surface over <paramref name="root"/>, on <paramref name="port"/>.</summary>
    private static WebApplication Ingress(string root, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));

        builder.Services.AddSingleton<Serilog.ILogger>(Logger.None);
        builder.Services.AddSingleton(new DashboardPaths(root));
        builder.Services.AddSingleton<IClock>(new FakeClock());
        builder.Services.AddSingleton(new IngressToken(expected: null));
        builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<IEventSink>(new RecordingEventSink());

        var app = builder.Build();
        app.MapIngress();
        return app;
    }
}
