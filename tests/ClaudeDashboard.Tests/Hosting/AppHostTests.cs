using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

    public void Dispose()
    {
        Log.CloseAndFlush();

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

    private string ReadAllLogs()
    {
        Log.CloseAndFlush();

        return Directory.Exists(_paths.LogFolder)
            ? string.Concat(Directory.EnumerateFiles(_paths.LogFolder, "*.log").Select(File.ReadAllText))
            : string.Empty;
    }

    // ---- Starting headless -------------------------------------------------------------------

    [Fact]
    public async Task The_host_builds_starts_and_stops()
    {
        using var host = AppHost.Build(_paths);

        host.Start();
        await host.StopAsync();
    }

    /// <summary>
    /// The real observation: a log file exists on disk afterwards, with the startup line in it.
    /// </summary>
    [Fact]
    public async Task Starting_writes_a_real_log_file_to_disk()
    {
        using (var host = AppHost.Build(_paths))
        {
            host.Start();
            await host.StopAsync();
        }

        var logs = ReadAllLogs();

        Assert.True(Directory.Exists(_paths.LogFolder), "The log folder must exist on disk.");
        Assert.NotEmpty(Directory.EnumerateFiles(_paths.LogFolder, "*.log"));
        Assert.Contains("Claude Dashboard starting", logs, StringComparison.Ordinal);
        Assert.Contains(_root, logs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_creates_the_data_and_log_folders()
    {
        // A root nothing has touched — this fixture's constructor writes settings into _root.
        var fresh = new DashboardPaths(Path.Combine(_root, "fresh"));
        Assert.False(Directory.Exists(fresh.Root));

        using var host = AppHost.Build(fresh);

        Assert.True(Directory.Exists(fresh.Root));
        Assert.True(Directory.Exists(fresh.LogFolder));
    }

    [Fact]
    public void The_host_publishes_what_later_tasks_resolve()
    {
        using var host = AppHost.Build(_paths);

        Assert.NotNull(host.Services.GetRequiredService<DashboardPaths>());
        Assert.NotNull(host.Services.GetRequiredService<SettingsStore>());
        Assert.NotNull(host.Services.GetRequiredService<DashboardSettings>());
        Assert.NotNull(host.Services.GetRequiredService<ILogger>());
        Assert.NotNull(host.Services.GetRequiredService<UnhandledExceptionPolicy>());
    }

    /// <summary>
    /// T1.9 must run the event consumer and the nudge tick as one serialized loop. No hosted
    /// service of ours exists yet, so it inherits a clean slate rather than an existing service
    /// to sit beside — which is how two racing loops come about.
    /// </summary>
    /// <remarks>
    /// Filtered to this solution's own assemblies. Since T1.8 the host is a
    /// <c>WebApplication</c>, so the framework registers a hosted service of its own to run
    /// Kestrel; that one is expected. A second of <em>ours</em> is what would race.
    /// </remarks>
    [Fact]
    public void No_hosted_service_of_our_own_is_registered_yet()
    {
        using var host = AppHost.Build(_paths);

        var ours = host.Services.GetServices<IHostedService>()
            .Where(service => service.GetType().Assembly.GetName().Name?
                .StartsWith("ClaudeDashboard", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Empty(ours);
    }

    // ---- Settings reach the host ----------------------------------------------------------------

    [Fact]
    public void Settings_from_the_file_reach_the_container()
    {
        new SettingsStore(_paths).Save(new DashboardSettings { Port = 51234 });

        using var host = AppHost.Build(_paths);

        Assert.Equal(51234, host.Services.GetRequiredService<DashboardSettings>().Port);
    }

    [Fact]
    public void A_missing_settings_file_is_logged_and_defaults_are_used()
    {
        // This fixture writes a settings file so its host binds a free port; remove it, and use
        // a fresh root so the default port is never actually bound.
        var fresh = new DashboardPaths(Path.Combine(_root, "no-settings"));

        using (AppHost.Build(fresh))
        {
        }

        Log.CloseAndFlush();
        var logs = string.Concat(
            Directory.EnumerateFiles(fresh.LogFolder, "*.log").Select(File.ReadAllText));

        Assert.Contains("using defaults", logs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of the malformed-file decision, observed end to end: the host still
    /// builds, the defaults are in the container, and the reason is on disk where the operator
    /// can find it — the only diagnostic channel a windowless app has.
    /// </summary>
    [Fact]
    public async Task A_malformed_settings_file_still_starts_and_logs_the_reason()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsFile, "{ \"port\": 51000,, }");

        using (var host = AppHost.Build(_paths))
        {
            host.Start();
            Assert.Equal(
                DashboardSettings.DefaultPort,
                host.Services.GetRequiredService<DashboardSettings>().Port);
            await host.StopAsync();
        }

        var logs = ReadAllLogs();
        Assert.Contains("could not be read", logs, StringComparison.Ordinal);
        Assert.Contains("Using defaults", logs, StringComparison.Ordinal);
    }

    // ---- The exception policy, against the real logger --------------------------------------------

    /// <summary>
    /// A deliberately thrown exception is caught and logged, not fatal — asserted on the real
    /// log file, and on the test process still being alive to make the assertion.
    /// </summary>
    [Fact]
    public void A_dispatcher_exception_is_logged_and_the_process_survives()
    {
        using (var host = AppHost.Build(_paths))
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
        using (var host = AppHost.Build(_paths))
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
        using (var host = AppHost.Build(_paths))
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
        using (var host = AppHost.Build(_paths))
        {
            host.Services.GetRequiredService<UnhandledExceptionPolicy>()
                .HandleDomainException(new InvalidOperationException("deliberate survivable fault"), false);
        }

        Assert.Contains("reached the AppDomain", ReadAllLogs(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_handled_fault_is_counted()
    {
        using var host = AppHost.Build(_paths);
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
        using var host = AppHost.Build(_paths);
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
        using var host = AppHost.Build(_paths);
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
}
