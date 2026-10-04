using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// Runs the dashboard's own hook script, as Claude Code runs it, and checks that the test message
/// arrives (T1.61, issue #74).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it proves, in one step:</strong> the script is on disk and runs, <c>curl.exe</c> is
/// there, the token in <c>listening.txt</c> is accepted, and the port answers. <strong>What it does
/// not prove:</strong> that Claude Code fires the hook. Only a real message proves that, which is
/// why the tooltip says when the last one arrived.
/// </para>
/// <para>
/// <strong>The script is not edited.</strong> The test is a JSON body of its own on standard input:
/// a test event name and a one-time value. Ingress recognises it before the mapper, notes the
/// arrival and answers <c>200</c> empty, as for any post. It makes no session, no row and no sound.
/// </para>
/// <para>
/// <strong>Never on the start's path.</strong> <see cref="RunInBackground"/> is called after
/// <c>listening.txt</c> is written and returns at once; the test runs on a pool thread. A second
/// request while one runs joins it, so the Settings button cannot start two.
/// </para>
/// </remarks>
public sealed class HookSelfTest
{
    /// <summary>How long the test message has to arrive.</summary>
    public static readonly TimeSpan ArrivalLimit = TimeSpan.FromSeconds(3);

    private readonly DashboardPaths _paths;
    private readonly HookHealth _health;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string _curl;
    private readonly TimeSpan _limit;
    private readonly Lock _gate = new();

    private Task<SelfTestResult>? _running;

    /// <summary>Creates the self-test over the dashboard's data folder.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public HookSelfTest(DashboardPaths paths, HookHealth health, IClock clock, ILogger logger)
        : this(paths, health, clock, logger, DefaultCurl, ArrivalLimit)
    {
    }

    /// <summary>Creates the self-test with a <c>curl.exe</c> path and a time limit of the test's choosing.</summary>
    internal HookSelfTest(DashboardPaths paths, HookHealth health, IClock clock, ILogger logger, string curl, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(curl);

        _paths = paths;
        _health = health;
        _clock = clock;
        _logger = logger;
        _curl = curl;
        _limit = limit;
    }

    /// <summary>The <c>curl.exe</c> the script runs, by the same absolute path.</summary>
    public static string DefaultCurl => Path.Combine(Environment.SystemDirectory, "curl.exe");

    /// <summary>Starts a test on a pool thread and returns at once. Never throws.</summary>
    public void RunInBackground() => _ = RunAsync();

    /// <summary>Runs a test, or joins the one already running. Never faults.</summary>
    public Task<SelfTestResult> RunAsync()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } running)
            {
                return running;
            }

            _running = Task.Run(RunOnce);

            return _running;
        }
    }

    private async Task<SelfTestResult> RunOnce()
    {
        try
        {
            if (!File.Exists(_paths.HookScriptFile))
            {
                return Finish(SelfTestCause.ScriptMissing, roundTripMs: null);
            }

            if (!File.Exists(_curl))
            {
                return Finish(SelfTestCause.CurlMissing, roundTripMs: null);
            }

            var value = Guid.NewGuid().ToString("N");
            var arrival = _health.Expect(value);
            var watch = Stopwatch.StartNew();

            using var process = Start(Body(value));

            if (process is null)
            {
                return Finish(SelfTestCause.CouldNotRun, roundTripMs: null);
            }

            var arrived = await Task.WhenAny(arrival, Task.Delay(_limit)).ConfigureAwait(false) == arrival;
            var elapsed = watch.ElapsedMilliseconds;

            // The script ends by itself (curl gives up after two seconds). It is waited for briefly,
            // so a test leaves no process behind, and never longer than that.
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                TryKill(process);
            }

            return arrived
                ? Finish(SelfTestCause.None, elapsed)
                : Finish(SelfTestCause.NothingArrived, roundTripMs: null);
        }
        catch (Exception ex)
        {
            // Degrade, never crash: a test that could not run is a failed test, said once.
            _logger.Warning(ex, "The self-test of the hook script could not run.");
            return Finish(SelfTestCause.CouldNotRun, roundTripMs: null);
        }
    }

    /// <summary>The test message: its own event name and a one-time value. No session, no text.</summary>
    internal static string Body(string value) =>
        $$"""{"hook_event_name":"{{HookHealth.SelfTestEventName}}","{{HookHealth.SelfTestValueField}}":"{{value}}"}""";

    /// <summary>Starts the script as Claude Code does, with the body on standard input.</summary>
    private Process? Start(string body)
    {
        var start = new ProcessStartInfo(HookHandlers.Interpreter)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _paths.Root,
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(_paths.HookScriptFile);

        try
        {
            var process = Process.Start(start);

            if (process is null)
            {
                return null;
            }

            process.StandardInput.Write(body);
            process.StandardInput.Close();

            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _logger.Warning("The self-test could not start the hook script: {ErrorType}.", ex.GetType().Name);
            return null;
        }
    }

    private SelfTestResult Finish(SelfTestCause cause, long? roundTripMs)
    {
        var result = new SelfTestResult(cause == SelfTestCause.None, roundTripMs, _clock.Now, cause);

        _health.Finished(result);

        if (result.Passed)
        {
            // The real cost of one message on this machine, measured at each start.
            _logger.Information("The test message from the hook script arrived in {RoundTripMs} ms.", roundTripMs);
        }
        else
        {
            _logger.Warning(
                "The test message from the hook script did not arrive ({Cause}), so messages from Claude Code " +
                "may not reach the dashboard. The window and the tray say so.",
                cause);
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Already gone, or not ours to end.
        }
    }
}
