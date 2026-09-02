using System.Linq;
using System.IO;
using System.Text;
using System.Reflection;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The host as an artifact: it starts, it writes a real log file, and a fault does not take the
/// process with it (Impl §3.1, §10.1).
/// </summary>
/// <remarks>
/// These assert on the log file that appears on disk rather than on a logging double. A fake
/// logger receiving a line would prove the fake works; it would say nothing about whether
/// Serilog is configured, whether the sink path is right, or whether anything was flushed —
/// which are the three ways this wiring actually fails.
/// </remarks>
public sealed class AppHostTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public AppHostTests()
    {
        _paths = new DashboardPaths(_root);

        // Every host now binds Kestrel (T1.8). Tests take a free ephemeral port so they neither
        // collide with each other nor with a dashboard actually running on the fixed 52789.
        new SettingsStore(_paths).Save(new DashboardSettings { Port = FreePort() });
    }

    /// <summary>Asks the OS for a free loopback port and releases it.</summary>
    internal static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Removes this fixture's temporary root.</summary>
    /// <remarks>
    /// No <c>Log.CloseAndFlush</c>. It closes whichever logger is currently the process-wide one,
    /// which — with <see cref="AppHost.Build"/> assigning it and other classes building hosts in
    /// parallel — is quite often not this class's. Disposing the host is what releases the sink
    /// this class opened, and each test already does that.
    /// </remarks>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A sink may still hold the file on a slow machine; the temp folder is disposable.
            }
        }
    }

    private readonly List<IDisposable> _loggers = [];

    /// <summary>
    /// Builds a host and remembers the logger it created, so the log can be read afterwards.
    /// </summary>
    /// <remarks>
    /// Every host in this class goes through here. <see cref="AppHost.Build"/> creates a Serilog
    /// logger and assigns the process-wide <c>Log.Logger</c>; holding a reference to the one
    /// <em>this</em> host made is what lets <see cref="ReadAllLogs"/> close it deterministically
    /// without touching whichever logger the static happens to point at.
    /// </remarks>
    private WebApplication Build(DashboardPaths? paths = null, bool ingressAvailable = true)
    {
        var host = AppHost.Build(paths ?? _paths, ingressAvailable: ingressAvailable);

        if (host.Services.GetService<Serilog.ILogger>() is IDisposable disposable)
        {
            _loggers.Add(disposable);
        }

        return host;
    }

    /// <summary>Closes the loggers this class made, then reads what they wrote.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Closing is what flushes, and an earlier version of this file assumed otherwise.</strong>
    /// It dropped <c>Log.CloseAndFlush</c> — rightly, because the static logger often belongs to a
    /// different class — and replaced it with a remark claiming the shared file sink "flushes on
    /// write, so an open file is a readable file". That was a capability claim nobody had checked,
    /// and it was wrong often enough to matter: the sink is configured with a two-second flush
    /// interval, so a read straight after a write could find the line missing. It failed about once
    /// in sixteen full runs, on a content assertion rather than an obvious one.
    /// </para>
    /// <para>
    /// Disposing the specific loggers this class created flushes them and needs no waiting, no
    /// interval, and no claim about buffering — and it still never touches another class's sink.
    /// </para>
    /// </remarks>
    private string ReadAllLogs() => ReadLogsIn(_paths.LogFolder);

    private void CloseOurLoggers()
    {
        foreach (var logger in _loggers)
        {
            logger.Dispose();
        }

        _loggers.Clear();
    }

    /// <summary>
    /// Reads every log file in <paramref name="folder"/>, whether or not a sink still holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately not <see cref="File.ReadAllText(string)"/>, and deliberately without
    /// <c>Log.CloseAndFlush</c>.</strong> <c>Log.Logger</c> is process-wide and
    /// <see cref="AppHost.Build"/> assigns it, so with three test classes building hosts and
    /// xUnit running classes in parallel, the static logger at any moment may belong to a
    /// different class. Closing it shuts <em>that</em> class's live sink mid-test and leaves this
    /// one's file open, so the read fails with a sharing violation — intermittently, and more
    /// often the more hosts a class builds.
    /// </para>
    /// <para>
    /// Calling it anyway "just in case" is the same defect pointing the other way, and it was in
    /// the first version of this fix: whatever it helps with here, it can break somebody else's
    /// run. So it is gone rather than hedged.
    /// </para>
    /// <para>
    /// The file is instead opened the way Serilog opened it: <see cref="FileShare.ReadWrite"/>.
    /// The sink is configured <c>shared</c> precisely so a second reader is allowed, so a file
    /// another sink still holds can be read. That is about <em>access</em> only — whether the
    /// content is there yet is a separate question, and the answer is to close the writer, which
    /// <see cref="ReadAllLogs"/> does.
    /// </para>
    /// </remarks>
    private string ReadLogsIn(string folder)
    {
        CloseOurLoggers();

        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        var everything = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(folder, "*.log"))
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            everything.Append(reader.ReadToEnd());
        }

        return everything.ToString();
    }

    // ---- Starting headless -------------------------------------------------------------------

    [Fact]
    public async Task The_host_builds_starts_and_stops()
    {
        using var host = Build();

        host.Start();
        await host.StopAsync();
    }

    /// <summary>
    /// The real observation: a log file exists on disk afterwards, with the startup line in it.
    /// </summary>
    [Fact]
    public async Task Starting_writes_a_real_log_file_to_disk()
    {
        using (var host = Build())
        {
            host.Start();
            await host.StopAsync();
        }

        var logs = ReadAllLogs();

        Assert.True(Directory.Exists(_paths.LogFolder), "The log folder must exist on disk.");
        Assert.NotEmpty(Directory.EnumerateFiles(_paths.LogFolder, "*.log"));
        Assert.Contains("Claude Dashboard starting", logs, StringComparison.Ordinal);
        Assert.Contains(_root, logs, StringComparison.Ordinal);

        // The version line, and FIRST (PKG.2): it is what PKG.4's gate and every support
        // question reads, and first is the one position nobody has to search for. Asserted
        // through the real file sink rather than only against a recording logger, because the
        // wiring — ReportStartup calling it before its own first line — is what this test can
        // see and StartupVersionTests cannot.
        var version = logs.IndexOf($"Claude Dashboard {StartupVersion.Value}.", StringComparison.Ordinal);
        var starting = logs.IndexOf("Claude Dashboard starting", StringComparison.Ordinal);

        Assert.True(version >= 0, "The startup log carries no version line.");
        Assert.True(version < starting, "The version line must be the first thing the logger says.");
    }

    [Fact]
    public void The_host_creates_the_data_and_log_folders()
    {
        // A root nothing has touched — this fixture's constructor writes settings into _root.
        var fresh = new DashboardPaths(Path.Combine(_root, "fresh"));
        Assert.False(Directory.Exists(fresh.Root));

        using var host = Build(fresh);

        Assert.True(Directory.Exists(fresh.Root));
        Assert.True(Directory.Exists(fresh.LogFolder));
    }

    [Fact]
    public void The_host_publishes_what_later_tasks_resolve()
    {
        using var host = Build();

        Assert.NotNull(host.Services.GetRequiredService<DashboardPaths>());
        Assert.NotNull(host.Services.GetRequiredService<SettingsStore>());
        Assert.NotNull(host.Services.GetRequiredService<DashboardSettings>());
        Assert.NotNull(host.Services.GetRequiredService<ILogger>());
        Assert.NotNull(host.Services.GetRequiredService<UnhandledExceptionPolicy>());
    }

    /// <summary>
    /// The UI's clock is wired, and the consumer is the thing that drives it.
    /// </summary>
    /// <remarks>
    /// Without this registration the process would look identical in every other respect and
    /// every age on screen would stop advancing — a session blocked for nine minutes reading "9
    /// min" for the rest of the afternoon. The behaviour is covered in <c>UiTickTests</c>; this
    /// is the composition, which is the half that can be lost without any test noticing.
    /// </remarks>
    [Fact]
    public void The_ui_tick_is_registered_and_reaches_the_consumer()
    {
        using var host = Build();

        var tick = host.Services.GetRequiredService<UiTick>();

        Assert.Same(tick, host.Services.GetRequiredService<IUiTick>());
        Assert.Same(tick, host.Services.GetRequiredService<EventConsumer>().UiTick);
    }

    /// <summary>
    /// The window and its view model are registered, and the reduced-motion policy with them.
    /// </summary>
    /// <remarks>
    /// The window is asserted as a registration rather than resolved: constructing a
    /// <see cref="System.Windows.Window"/> requires an STA thread, and this suite runs on the
    /// pool. Program resolves it on the UI thread, which is the only place it may be built.
    /// </remarks>
    [Fact]
    public void The_window_and_its_view_model_are_registered()
    {
        using var host = Build();

        var isService = host.Services.GetRequiredService<IServiceProviderIsService>();

        Assert.True(isService.IsService(typeof(MainWindow)));
        Assert.True(isService.IsService(typeof(MainViewModel)));
        Assert.NotNull(host.Services.GetRequiredService<MotionPolicy>());
    }

    /// <summary>
    /// The manual acknowledgment is wired: the view model the container builds has a publisher,
    /// and that publisher's acks land in the channel the consumer reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the test whose absence made T1.12 look shipped and be dead.</strong>
    /// Deleting <c>AddSingleton&lt;IAckPublisher, AckPublisher&gt;()</c> left the whole suite
    /// green, because every ack test injects the publisher the container is supposed to supply.
    /// In the shipped app the row would still have drawn its Ack button — correctly, in the right
    /// place, permanently disabled, forever.
    /// </para>
    /// <para>
    /// The registration is load-bearing twice here. <see cref="MainViewModel"/> requires the
    /// publisher, so resolving it throws outright if the registration is gone; and the round trip
    /// through <see cref="EventPipeline.Reader"/> shows the publisher writing into the channel the
    /// consumer actually reads rather than into one of its own. Resolving alone would prove a
    /// service exists, not that the ack has anywhere to go.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_manual_acknowledgment_is_wired_end_to_end()
    {
        using var host = Build();

        // Throws if the publisher is unregistered: the parameter is required, not defaulted.
        Assert.NotNull(host.Services.GetRequiredService<MainViewModel>());

        var registry = host.Services.GetRequiredService<SessionRegistry>();
        var id = new SessionId("s-wired");

        registry.Apply(new UserPromptSubmit
        {
            SessionId = id,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
            Cwd = _root,
            PromptId = "p-1",
            Prompt = "run the tests",
        });

        Assert.True(host.Services.GetRequiredService<IAckPublisher>().Acknowledge(registry.Sessions[id]));

        Assert.True(host.Services.GetRequiredService<EventPipeline>().Reader.TryRead(out var queued));
        var ack = Assert.IsType<Ack>(queued);
        Assert.Equal(id, ack.SessionId);
        Assert.Equal(AckSource.Manual, ack.Source);
    }

    /// <summary>
    /// The hosted services of ours are exactly the ones listed here, so adding another is a
    /// deliberate act with a reason attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This used to assert "exactly one", and it fired when T1.17 added the second.</strong>
    /// It was right to fire and wrong to be a count. What it was really protecting is the next
    /// test's invariant — that one thread mutates the Registry and the sound engine — and a count
    /// is a proxy for that which is both too strict and too weak: too strict, because a service
    /// that touches neither cannot break it; too weak, because two services could touch the
    /// Registry while some third was deleted and the count stayed at two.
    /// </para>
    /// <para>
    /// So the count became an inventory, and the invariant became its own test. Both are needed:
    /// this one makes adding a service deliberate, that one makes it safe.
    /// </para>
    /// <para>
    /// Filtered to our own assemblies, since the framework registers its own service to run
    /// Kestrel.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_hosted_services_of_ours_are_the_ones_we_expect()
    {
        using var host = Build();

        var ours = OurHostedServices(host).Select(service => service.GetType()).ToList();

        Assert.Equal<Type[]>([typeof(EventConsumer), typeof(EventArchiveWriter)], [.. ours]);
    }

    /// <summary>
    /// <strong>Only the event consumer may reach the Registry or the sound engine.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are lock-free on the assumption that one thread mutates them (Impl §2.2, §4), and the
    /// consumer keeps that true by reading the channel and running the nudge tick on a single
    /// loop. A second <c>BackgroundService</c> that could touch either would give them a second
    /// driver, and the resulting race is intermittent and invisible in a green suite — the T1.5
    /// review demonstrated it throwing within a few hundred iterations.
    /// </para>
    /// <para>
    /// <strong>This walks what each service can actually reach, rather than trusting its
    /// name.</strong> T1.17's archive writer is safe because it holds a channel, a store and a
    /// logger and nothing else; if somebody hands it a Registry so it can "enrich" a row, this
    /// fails. That is the failure worth catching, and a count of services would sail past it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_the_event_consumer_can_reach_the_registry_or_the_sound_engine()
    {
        using var host = Build();

        foreach (var service in OurHostedServices(host))
        {
            var reachable = Reachable(service);

            var forbidden = reachable
                .Where(type => type == typeof(SessionRegistry) || type == typeof(SoundPolicyEngine))
                .ToList();

            if (service is EventConsumer)
            {
                // The positive half. Without it this test would also pass if the consumer stopped
                // holding the Registry at all, which would mean the dashboard had stopped working
                // and the guard had congratulated it.
                Assert.Contains(typeof(SessionRegistry), reachable);
                Assert.Contains(typeof(SoundPolicyEngine), reachable);

                continue;
            }

            Assert.True(
                forbidden.Count == 0,
                $"{service.GetType().Name} can reach {string.Join(", ", forbidden.Select(t => t.Name))}, " +
                "which only the event consumer may touch.");
        }
    }

    private static List<IHostedService> OurHostedServices(WebApplication host) =>
        [.. host.Services.GetServices<IHostedService>()
            .Where(service => service.GetType().Assembly.GetName().Name?
                .StartsWith("ClaudeDashboard", StringComparison.Ordinal) == true)];

    /// <summary>Every type an object holds, directly or through what it holds.</summary>
    /// <remarks>
    /// Depth-limited and cycle-safe. It walks instance fields, which is where a collaborator that
    /// was injected ends up; a type reached only through a local or a service-locator call is not
    /// visible here, and that is a real limit of this check rather than a claim it does not have.
    /// </remarks>
    private static HashSet<Type> Reachable(object root)
    {
        var seen = new HashSet<Type>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var field in current.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.GetValue(current) is not { } value || value.GetType().IsPrimitive)
                {
                    continue;
                }

                seen.Add(value.GetType());

                if (value.GetType().Assembly.GetName().Name?
                    .StartsWith("ClaudeDashboard", StringComparison.Ordinal) == true)
                {
                    queue.Enqueue(value);
                }
            }
        }

        return seen;
    }

    // ---- Settings reach the host ----------------------------------------------------------------

    [Fact]
    public void Settings_from_the_file_reach_the_container()
    {
        new SettingsStore(_paths).Save(new DashboardSettings { Port = 51234 });

        using var host = Build();

        Assert.Equal(51234, host.Services.GetRequiredService<DashboardSettings>().Port);
    }

    [Fact]
    public void A_missing_settings_file_is_logged_and_defaults_are_used()
    {
        // This fixture writes a settings file so its host binds a free port; remove it, and use
        // a fresh root so the default port is never actually bound.
        var fresh = new DashboardPaths(Path.Combine(_root, "no-settings"));

        using (Build(fresh))
        {
        }

        Assert.Contains("using defaults", ReadLogsIn(fresh.LogFolder), StringComparison.Ordinal);
    }

    /// <summary>
    /// A malformed settings file leaves the defaults in the container, logs the reason where the
    /// operator can find it, and <strong>still starts a host that serves</strong>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THE START IS BACK, AND THE REASON IT WAS EVER REMOVED WAS FALSE.</strong> This
    /// test was split in two, and the paragraph justifying the split said: "a malformed file
    /// always yields <c>DefaultPort</c> — that is the fallback this very test asserts — so the
    /// settings can never name a free port, and a host started from them always binds the fixed
    /// one." Three claims, all wrong.
    /// </para>
    /// <para>
    /// <strong>A malformed file yields an <em>unset</em> port, not <c>DefaultPort</c></strong> —
    /// <see cref="DashboardSettings.Port"/> is <c>int?</c> and its own remark says in bold that an
    /// out-of-range value becomes unset rather than defaulted. That nullability is T1.21's whole
    /// mechanism. The assertion below never said otherwise: it compares against a fresh
    /// <see cref="DashboardSettings"/>, whose port is null. <strong>The remark cited this test as
    /// its evidence and this test said something else.</strong>
    /// </para>
    /// <para>
    /// <strong>And an unset port is exactly the case the derivation exists for.</strong>
    /// <c>AppHost.Build</c> with no ingress runs <c>PortSelection.ForDataFolder</c> — pin, then
    /// <c>port.txt</c>, then a derivation from the SID, then a bounded walk — under a comment
    /// headed "NO SILENT FALL-BACK TO THE BASE PORT". The old remark asserted the fall-back that
    /// code exists to prevent. Far from binding an occupied port, a malformed file gets the same
    /// free port any ordinary start would.
    /// </para>
    /// <para>
    /// <strong>So the coverage came back rather than staying recovered by composition.</strong>
    /// Two tests composing into "a malformed file still starts" was a real second-best; one test
    /// that starts one is better, and there was never anything preventing it after T1.21. The
    /// port is read from <see cref="IngressStatus"/> rather than from the settings, because the
    /// point of the claim is that <em>nothing in the file chose it</em>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_malformed_settings_file_falls_back_to_defaults_and_still_serves()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsFile, "{ \"port\": 51000,, }");

        using var host = Build();

        // Equality against a fresh instance, not just the port: every value has to be ordinary,
        // and a fault living in one the port assertion never reads would otherwise go unseen.
        var settings = host.Services.GetRequiredService<DashboardSettings>();
        Assert.Equal(new DashboardSettings(), settings);

        // UNSET, and named separately from the equality above because this is the specific claim
        // the deleted remark got wrong.
        Assert.Null(settings.Port);

        host.Start();

        try
        {
            var status = host.Services.GetRequiredService<IngressStatus>();

            Assert.True(
                status.CanReceiveHooks,
                $"a malformed settings file left the host unable to hear on port {status.Port}");

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{status.Port}/health"));

            Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }

        var logs = ReadAllLogs();
        Assert.Contains("could not be read", logs, StringComparison.Ordinal);
        Assert.Contains("Using defaults", logs, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host started from ordinary settings binds the port those settings name, and serves on it.
    /// </summary>
    /// <remarks>
    /// Strictly stronger than the start it replaces. The old test bound the default port and
    /// asserted nothing whatever about the bind, so it never showed that the port in the settings
    /// file reaches Kestrel at all — a build that ignored the setting and bound a constant would
    /// have passed it. Answering on the chosen port could not have been produced any other way:
    /// the port was free immediately before, so nothing else could be answering there.
    /// </remarks>
    [Fact]
    public async Task A_started_host_binds_the_port_the_settings_name()
    {
        var port = FreePort();
        new SettingsStore(_paths).Save(new DashboardSettings { Port = port });

        using var host = Build();
        host.Start();

        try
        {
            using var client = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10),
            };

            var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/health"));

            Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// When something else holds the configured port, the dashboard starts anyway and says so
    /// (Impl §5.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three claims, and all three are needed. That it started at all — a host that threw would
    /// leave the operator with no dashboard. That the log names the port at Error — the log is
    /// the diagnosis. And that <see cref="IngressStatus"/> in the container carries a fault, which
    /// is what puts the reason in front of the operator's eyes rather than in a file.
    /// </para>
    /// <para>
    /// The stranger is a real listener on the real port, because "the port is taken" is a
    /// property of a socket and nothing else can stand in for it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_host_whose_port_is_taken_starts_and_says_it_cannot_hear()
    {
        var port = FreePort();
        new SettingsStore(_paths).Save(new DashboardSettings { Port = port });

        var stranger = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        stranger.Start();

        try
        {
            using var host = Build(ingressAvailable: false);
            host.Start();

            var status = host.Services.GetRequiredService<IngressStatus>();

            Assert.False(status.CanReceiveHooks);
            Assert.Equal(port, status.Port);
            Assert.Contains(port.ToString(System.Globalization.CultureInfo.CurrentCulture), status.Fault!, StringComparison.Ordinal);

            await host.StopAsync();
        }
        finally
        {
            stranger.Stop();
        }

        var logs = ReadAllLogs();
        Assert.Contains("cannot receive hooks", logs, StringComparison.Ordinal);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), logs, StringComparison.Ordinal);
    }

    /// <summary>The ordinary case still reports a healthy ingress, which is the control above.</summary>
    /// <remarks>
    /// Without this, a build that reported a fault unconditionally would satisfy the test above
    /// and put "not receiving hooks" in every operator's tray for ever.
    /// </remarks>
    [Fact]
    public void A_host_that_bound_its_port_reports_no_fault()
    {
        using var host = Build();

        var status = host.Services.GetRequiredService<IngressStatus>();

        Assert.True(status.CanReceiveHooks);
        Assert.Null(status.Fault);
    }

    // ---- The exception policy, against the real logger --------------------------------------------

    /// <summary>
    /// A deliberately thrown exception is caught and logged, not fatal — asserted on the real
    /// log file, and on the test process still being alive to make the assertion.
    /// </summary>
    [Fact]
    public void A_dispatcher_exception_is_logged_and_the_process_survives()
    {
        using (var host = Build())
        {
            var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

            var handled = policy.HandleDispatcherException(
                new InvalidOperationException("deliberate test exception on the UI thread"));

            Assert.True(handled, "Impl §10.1: a fault downgrades a feature, it does not kill the process.");
            Assert.Equal(1, policy.ObservedCount);
        }

        var logs = ReadAllLogs();
        Assert.Contains("deliberate test exception on the UI thread", logs, StringComparison.Ordinal);
        Assert.Contains("Unhandled exception on the UI thread", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unobserved_task_exception_is_logged_and_marked_observed()
    {
        using (var host = Build())
        {
            var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

            Assert.True(policy.HandleUnobservedTaskException(
                new InvalidOperationException("deliberate unobserved task fault")));
        }

        Assert.Contains("deliberate unobserved task fault", ReadAllLogs(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The terminating case cannot be prevented, so the only requirement is that the reason
    /// reaches disk before the process goes — which is what this asserts.
    /// </summary>
    [Fact]
    public void A_terminating_domain_exception_is_logged_as_fatal()
    {
        using (var host = Build())
        {
            host.Services.GetRequiredService<UnhandledExceptionPolicy>()
                .HandleDomainException(
                    new InvalidOperationException("deliberate terminating fault"),
                    isTerminating: true);
        }

        var logs = ReadAllLogs();
        Assert.Contains("deliberate terminating fault", logs, StringComparison.Ordinal);
        Assert.Contains("the process is terminating", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_terminating_domain_exception_is_logged_as_an_error()
    {
        using (var host = Build())
        {
            host.Services.GetRequiredService<UnhandledExceptionPolicy>()
                .HandleDomainException(new InvalidOperationException("deliberate survivable fault"), false);
        }

        Assert.Contains("reached the AppDomain", ReadAllLogs(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_handled_fault_is_counted()
    {
        using var host = Build();
        var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

        policy.HandleDispatcherException(new InvalidOperationException("one"));
        policy.HandleUnobservedTaskException(new InvalidOperationException("two"));
        policy.HandleDomainException(new InvalidOperationException("three"), false);

        Assert.Equal(3, policy.ObservedCount);
    }

    // ---- Wiring the process-wide handlers ------------------------------------------------------------

    /// <summary>
    /// The real <c>TaskScheduler.UnobservedTaskException</c> event, raised by the runtime rather
    /// than called directly — this is the one of the three process-wide handlers that can be
    /// exercised in-process without ending the test run.
    /// </summary>
    [Fact]
    public void A_genuinely_unobserved_task_reaches_the_wired_handler()
    {
        using var host = Build();
        var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

        using (AppHost.WireProcessExceptionHandlers(policy))
        {
            DropAFaultedTask();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // The runtime raises the event on the finalizer thread; the count is the observation.
        Assert.True(
            policy.ObservedCount >= 1,
            "A faulted task that nobody observed should have reached the wired handler.");
    }

    [Fact]
    public void Wiring_can_be_undone_so_handlers_do_not_outlive_a_host()
    {
        using var host = Build();
        var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

        var wiring = AppHost.WireProcessExceptionHandlers(policy);
        wiring.Dispose();
        wiring.Dispose();
    }

    [Fact]
    public void Wiring_needs_a_policy()
    {
        Assert.Throws<ArgumentNullException>(() => AppHost.WireProcessExceptionHandlers(null!));
    }

    /// <summary>Kept out of the test body so the faulted task is unreachable and can be collected.</summary>
    private static void DropAFaultedTask()
    {
        _ = Task.Run(static () => throw new InvalidOperationException("deliberate dropped fault"));
        Thread.Sleep(50);
    }

    /// <summary>
    /// The tick the container registered is the one the consumer holds, and it reaches every
    /// attached target rather than only the first.
    /// </summary>
    /// <remarks>
    /// The tray advances on this tick and on nothing else: a global mute lapses by predicate and
    /// raises no event, so without it the tooltip would be correct exactly once, at startup. That
    /// the tray actually responds is asserted in <c>TrayCompositionTests</c>, on a thread with a
    /// real dispatcher.
    /// </remarks>
    [Fact]
    public void The_trays_tick_is_the_one_the_consumer_drives()
    {
        using var host = Build();

        var tick = host.Services.GetRequiredService<UiTick>();

        Assert.Same(tick, host.Services.GetRequiredService<IUiTick>());
        Assert.Same(tick, host.Services.GetRequiredService<EventConsumer>().UiTick);

        // …and it reaches everything attached, not just the first thing.
        var tray = host.Services.GetRequiredService<TrayViewModel>();
        var window = host.Services.GetRequiredService<MainViewModel>();

        tick.Attach(window);
        tick.Attach(tray);

        var before = tick.DeliveredCount;
        tick.Tick(DateTimeOffset.UtcNow);

        Assert.Equal(before + 1, tick.DeliveredCount);
    }

    /// <summary>
    /// The tray's global sound modes come from the engine the consumer mutates — one engine, not
    /// a second one built for the display.
    /// </summary>
    /// <remarks>
    /// If <c>ISoundModeReader</c> resolved to its own instance, every mode would read false
    /// forever: the operator would mute, the sound would stop, and the tooltip would never say
    /// so. Resolving is not enough to catch that — the two would both exist — so this asserts
    /// they are the same object.
    /// </remarks>
    [Fact]
    public void The_tray_reads_the_engine_the_consumer_writes()
    {
        using var host = Build();

        var engine = host.Services.GetRequiredService<SoundPolicyEngine>();

        Assert.Same(engine, host.Services.GetRequiredService<ISoundModeReader>());
    }

    /// <summary>
    /// <strong>The resolved sound player IS the NAudio adapter.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted on <em>identity</em>, deliberately, and not on a sound being played. The
    /// placeholder this replaces — <c>SilentSoundPlayer</c> — failed as silence, and silence is
    /// indistinguishable from a quiet afternoon, from a working mute, and from a machine with no
    /// speakers. There is no observable behaviour that separates "the real adapter is wired" from
    /// "the placeholder came back"; only the type does.
    /// </para>
    /// <para>
    /// The file was deleted rather than registered over, for the reason T1.8 gave about its
    /// sibling: a superseded-but-present implementation is the kind that comes back. This is what
    /// notices if it does.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_sound_player_is_the_real_adapter()
    {
        using var host = Build();

        var player = host.Services.GetRequiredService<ISoundPlayer>();

        Assert.IsType<NAudioSoundPlayer>(player);

        // …and it is the one the engine will actually call, not a second instance.
        Assert.Same(player, host.Services.GetRequiredService<ISoundPlayer>());
    }

    /// <summary>
    /// <strong>Building a host builds the audio stack, and a beep cannot stop the dashboard starting.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adapter is not constructed lazily on the first notice. <see cref="AppHost.Build"/>
    /// resolves <see cref="SoundPolicyEngine"/> eagerly so the engine can subscribe on the
    /// consumer thread, and the engine takes an <see cref="ISoundPlayer"/> — so building a host
    /// opens the Windows audio stack, and anything the adapter's constructor throws comes out of
    /// start-up rather than out of a beep. That is the fault this test exists for: a COM
    /// activation failure once left this path unguarded, which turned "no sound on this machine"
    /// into "the dashboard does not start on this machine".
    /// </para>
    /// <para>
    /// <strong>What this test proves and what it cannot.</strong> It runs on a machine whose audio
    /// stack works, so it proves the construction is really on the start-up path and really
    /// completes. It cannot prove start-up survives a stack that will not build — that needs the
    /// operator's audio service stopped, and it is measured instead one layer down, in
    /// <c>NAudioSoundPlayerTests.An_audio_stack_that_will_not_build_degrades_to_silence</c>, where
    /// the failure can be injected. Neither test covers the path alone: this one shows start-up
    /// goes through the constructor, and that one shows the constructor never throws.
    /// </para>
    /// <para>
    /// The dependency is asserted rather than assumed, because it is the whole reason the two
    /// halves join up. If the engine ever took a factory or a lazy instead, construction would
    /// move off the start-up path, this reasoning would stop holding, and this line would say so.
    /// </para>
    /// <para>
    /// <strong>The reasoning rests on a second premise, and only the first is asserted here.</strong>
    /// It also needs <see cref="AppHost.Build"/> to resolve the engine <em>eagerly</em>, which it
    /// does at the <c>GetRequiredService&lt;SoundPolicyEngine&gt;</c> before its return — measured
    /// there, not here, so if that resolve ever moved this test would stay green while the claim
    /// in its name went false. It is left unasserted deliberately: dropping the eager resolve
    /// would take the audio stack <em>off</em> the start-up path and make start-up safer, so the
    /// gap cannot hide a hazard, only a stale name.
    /// </para>
    /// </remarks>
    [Fact]
    public void Host_startup_builds_the_audio_stack_rather_than_deferring_it()
    {
        var takesThePlayerDirectly = typeof(SoundPolicyEngine)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(ISoundPlayer));

        Assert.True(
            takesThePlayerDirectly,
            "SoundPolicyEngine no longer takes ISoundPlayer directly, so building a host may no "
            + "longer construct the audio adapter. The reasoning that a guarded constructor keeps "
            + "start-up safe depends on this.");

        // The assertion is that this returns at all. Before the guard, an audio stack that would
        // not build made this line throw.
        using var host = Build();

        var player = host.Services.GetRequiredService<ISoundPlayer>();

        // …and the thing start-up built is usable, not merely present. A constructor that
        // swallowed its failure and left a broken object would satisfy "did not throw".
        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);
    }

    /// <summary>
    /// The placeholder is gone from the assembly, not merely unregistered.
    /// </summary>
    /// <remarks>
    /// Registering over a placeholder leaves it one line away from returning, and the line that
    /// would bring it back looks like a fix. Asserted by name so the check survives the type
    /// being renamed into something equally silent.
    /// </remarks>
    [Fact]
    public void No_silent_sound_player_remains_in_the_assembly()
    {
        var silent = typeof(AppHost).Assembly
            .GetTypes()
            .Where(type => typeof(ISoundPlayer).IsAssignableFrom(type) && !type.IsInterface)
            .Select(type => type.Name)
            .ToList();

        Assert.Equal([nameof(NAudioSoundPlayer)], silent);
    }

    /// <summary>
    /// The engine's options come from the file, mapped one way onto Core's defaults.
    /// </summary>
    [Fact]
    public void The_sound_options_are_registered_from_the_settings()
    {
        using var host = Build();

        var options = host.Services.GetRequiredService<SoundPolicyOptions>();

        // No settings file was written, so this is Core's defaults arriving through the mapping
        // rather than the mapping being skipped.
        Assert.Equal(SoundPolicyOptions.DefaultMasterVolume, options.MasterVolume);
        Assert.Equal(SoundPolicyOptions.DefaultNoticeGain, options.NoticeGain);
    }
}
