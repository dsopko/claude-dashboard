using System.IO;
using System.Net;
using System.Net.Sockets;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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
/// <para>
/// <strong>Another program can hold a port that a test derives</strong> (issue #155). Windows gives
/// ports from 49152 up to the outgoing connections of every program, so a browser, Claude Code or
/// another test can hold one at any moment. Once, in T1.86, a test here failed because 49240 was
/// taken. So each test keeps the derivation and moves its base when a port it derived cannot be
/// bound: down by <see cref="Shift"/>, up to <see cref="TestPorts.Attempts"/> times, and a test
/// that runs out of attempts names every port it tried. The first test also gives why each one
/// could not be bound, in Kestrel's words where Kestrel refused it, so a fault of the dashboard is
/// not read as a busy PC. A move changes only where the range starts; what each test proves stays
/// the same. The two bases are 300 apart at every attempt and each range is 200 wide, so the tests
/// never move into each other's ports.
/// </para>
/// </remarks>
public sealed class TwoUsersBindTests : IDisposable
{
    /// <summary>How far down the base moves when a derived port cannot be bound.</summary>
    private const int Shift = 1000;

    /// <summary>How many ports each derivation may land on, counted up from its base.</summary>
    private const int Range = 200;

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

        // The first base is clear of every other port this suite fixes. Each move goes down by Shift,
        // so this base stays 300 below the other test's at every attempt.
        const int FirstBase = 49200;

        var tried = new List<string>();

        // Why each port could not be bound, in the words of the check or of Kestrel. Kestrel's words
        // name the address it tried, which is not always the derived port: a dashboard that binds
        // one shared port for every user (issue #5) fails here naming that port.
        var causes = new List<string>();

        for (var attempt = 0; attempt < TestPorts.Attempts; attempt++)
        {
            var basePort = FirstBase - (attempt * Shift);
            var firstPort = PortSelection.Derive(FirstSid, basePort, Range);
            var secondPort = PortSelection.Derive(SecondSid, basePort, Range);

            Assert.NotEqual(firstPort, secondPort);
            tried.Add($"{firstPort} and {secondPort}");

            if (await TryStartAsync(RootFor($"first-{attempt}"), firstPort, causes) is not { } first)
            {
                continue;
            }

            if (await TryStartAsync(RootFor($"second-{attempt}"), secondPort, causes) is not { } second)
            {
                await DiscardAsync(first);
                continue;
            }

            // Both are up at once, which a single fixed port makes impossible — the point of the task.
            Assert.Equal(firstPort, first.Services.GetRequiredService<IngressStatus>().Port);
            Assert.Equal(secondPort, second.Services.GetRequiredService<IngressStatus>().Port);

            using var client = new System.Net.Http.HttpClient();

            foreach (var port in new[] { firstPort, secondPort })
            {
                using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            return;
        }

        Assert.Fail(
            $"One port of each derived pair could not be bound in {TestPorts.Attempts} attempts. "
            + $"Pairs tried: {string.Join("; ", tried)}. Causes: {string.Join(" | ", causes)}");
    }

    /// <summary>
    /// A user whose derived port is already held walks past it and still binds.
    /// </summary>
    /// <remarks>
    /// The stranger case, with a real occupant rather than a table entry: the port is genuinely
    /// held by another listener, so the walk is answered by the operating system. The test holds
    /// the derived port itself; when another program holds it first, or the bind fails for another
    /// reason, the test's own listener cannot start there, and the base moves.
    /// </remarks>
    [Fact]
    public void A_taken_derived_port_is_walked_past_against_a_real_listener()
    {
        const string Sid = "S-1-5-21-4444444444-5555555555-6666666666-1001";

        // 300 above the other test's base, and it moves down by the same Shift.
        const int FirstBase = 49500;

        var tried = new List<int>();

        for (var attempt = 0; attempt < TestPorts.Attempts; attempt++)
        {
            var basePort = FirstBase - (attempt * Shift);
            var derived = PortSelection.Derive(Sid, basePort, Range);

            tried.Add(derived);

            // A real stranger on the derived port. A listener that never accepts answers nothing,
            // which is exactly what a stranger that is not a dashboard looks like.
            using var stranger = new TcpListener(IPAddress.Loopback, derived);

            try
            {
                stranger.Start();
            }
            catch (SocketException)
            {
                // Another program holds it, or the bind failed for another reason: move the base.
                continue;
            }

            try
            {
                var choice = PortSelection.Choose(
                    basePort,
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

            return;
        }

        Assert.Fail(
            $"The test's own listener could not hold the derived port in {TestPorts.Attempts} attempts. "
            + $"Ports tried: {string.Join(", ", tried)}.");
    }

    /// <summary>
    /// Starts a host on <paramref name="port"/>, or returns null when the port cannot be bound: before
    /// the start (the IPv4 check) or at the start (Kestrel's bind). Each time, it adds the cause to
    /// <paramref name="causes"/>.
    /// </summary>
    /// <remarks>
    /// The check moves past a port that a listener cannot hold for any socket error, before a host is
    /// built on it; <see cref="TestPorts.TakenAtStart"/> knows only "address in use". A port taken
    /// after the check fails Kestrel's start with "address in use", and the host is discarded. The
    /// cause is Kestrel's own message, which names the address it could not bind (the PR #156 review).
    /// </remarks>
    private async Task<WebApplication?> TryStartAsync(DashboardPaths paths, int port, List<string> causes)
    {
        if (BindRefusal(port) is { } refused)
        {
            causes.Add($"a listener on 127.0.0.1:{port} failed with {refused}");

            return null;
        }

        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        var host = AppHost.Build(paths, ingress: IngressStatus.Healthy(port), claude: new ClaudeCodePaths(Path.Combine(paths.Root, "claude-config")));

        try
        {
            await host.StartAsync();
        }
        catch (Exception ex) when (TestPorts.TakenAtStart(ex))
        {
            causes.Add(ex.Message);

            await TestPorts.Stopped(host);
            TestPorts.Discarded(host);
            await host.DisposeAsync();

            return null;
        }

        _hosts.Add(host);

        return host;
    }

    /// <summary>Stops a started host whose partner could not start, and closes its log file.</summary>
    private async Task DiscardAsync(WebApplication host)
    {
        _hosts.Remove(host);

        await TestPorts.Stopped(host);
        TestPorts.Discarded(host);
        await host.DisposeAsync();
    }

    /// <summary>
    /// Null when a listener can hold <paramref name="port"/> on the IPv4 loopback now; otherwise the socket
    /// error that refused it.
    /// </summary>
    private static SocketError? BindRefusal(int port)
    {
        using var check = new TcpListener(IPAddress.Loopback, port);

        try
        {
            check.Start();
            check.Stop();

            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }
}
