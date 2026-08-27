using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// Two data roots with different derived candidates both bind, against real sockets (T1.21).
/// </summary>
/// <remarks>
/// <para>
/// <c>PortSelectionTests</c> decides the arithmetic against a table of answers. This asks the
/// operating system instead: two hosts, two derived ports, both actually bound and both answering
/// at the same time. <strong>That is the whole of issue #5</strong> — a second signed-in user
/// getting a dashboard that can hear — and a table cannot evidence it, because a table cannot
/// refuse a bind.
/// </para>
/// <para>
/// Real users are not simulated; two identities and two data roots are. What is genuinely
/// exercised is that two derived ports differ, that both bind at once, and that each host reports
/// the port it was given rather than a default.
/// </para>
/// </remarks>
public sealed class TwoUsersBindTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplication> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            try
            {
                host.StopAsync().GetAwaiter().GetResult();
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // Already down.
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }
    }

    private DashboardPaths RootFor(string user)
    {
        var paths = new DashboardPaths(Path.Combine(_root, user));
        Directory.CreateDirectory(paths.Root);

        return paths;
    }

    /// <summary>
    /// Two identities derive two ports, and both are bound at the same moment.
    /// </summary>
    [Fact]
    public async Task Two_users_derive_different_ports_and_both_bind()
    {
        const string FirstSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        const string SecondSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";

        // A base well clear of anything else this suite uses, so a neighbour's port cannot decide
        // the outcome. The derivation spreads across the range from here.
        const int Base = 49200;
        const int Range = 200;

        var firstPort = PortSelection.Derive(FirstSid, Base, Range);
        var secondPort = PortSelection.Derive(SecondSid, Base, Range);

        Assert.NotEqual(firstPort, secondPort);

        var first = Start(RootFor("first"), firstPort);
        var second = Start(RootFor("second"), secondPort);

        // Both are up at once, which a single fixed port makes impossible — the point of the task.
        Assert.Equal(firstPort, first.Services.GetRequiredService<IngressStatus>().Port);
        Assert.Equal(secondPort, second.Services.GetRequiredService<IngressStatus>().Port);

        using var client = new System.Net.Http.HttpClient();

        foreach (var port in new[] { firstPort, secondPort })
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// A user whose derived port is already held walks past it and still binds.
    /// </summary>
    /// <remarks>
    /// The stranger case, with a real occupant rather than a table entry: the port is genuinely
    /// held by another listener, so the walk is answered by the operating system.
    /// </remarks>
    [Fact]
    public void A_taken_derived_port_is_walked_past_against_a_real_listener()
    {
        const string Sid = "S-1-5-21-4444444444-5555555555-6666666666-1001";
        const int Base = 49500;
        const int Range = 200;

        var derived = PortSelection.Derive(Sid, Base, Range);

        // A real stranger on the derived port. CannedServer answers nothing, which is exactly what
        // a stranger that is not a dashboard looks like.
        using var stranger = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, derived);
        stranger.Start();

        try
        {
            var choice = PortSelection.Choose(
                Base,
                Sid,
                recorded: null,
                port => HealthProbe.Probe(port, "Local\\ClaudeDashboard-two-users", TimeSpan.FromMilliseconds(150)).Occupant,
                range: Range);

            Assert.True(choice.Found);
            Assert.Equal(PortSource.Walked, choice.Source);
            Assert.NotEqual(derived, choice.Port);

            // The occupant was classified rather than merely counted, which is what keeps
            // "a stranger" apart from "another user's dashboard" in the log.
            Assert.Contains(choice.Attempts, a => a.Port == derived && a.Occupant != PortOccupant.Free);
        }
        finally
        {
            stranger.Stop();
        }
    }

    private WebApplication Start(DashboardPaths paths, int port)
    {
        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        var host = AppHost.Build(paths, ingress: IngressStatus.Healthy(port));
        _hosts.Add(host);

        host.StartAsync().GetAwaiter().GetResult();

        return host;
    }
}
