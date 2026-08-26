using System.IO;
using System.Windows.Threading;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using Microsoft.AspNetCore.Connections;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ClaudeDashboard.App;

/// <summary>The process entry point (Impl §3.1, §10.1).</summary>
/// <remarks>
/// <para>
/// The Generic Host is the outer thing and the WPF application runs inside it, which is what
/// Impl §3.1 describes — "the .NET Generic Host that also owns the WPF app, logging, and
/// background services". So this is an explicit <c>Main</c> rather than an override of
/// <c>App.OnStartup</c>: with the host built first, everything the app resolves already exists
/// by the time WPF starts, and there is no window where a WPF callback could run against a
/// half-built container.
/// </para>
/// <para>
/// <strong>This is the only entry point, and that took arranging.</strong> As an
/// <c>ApplicationDefinition</c>, <c>App.xaml</c> makes WPF generate a <c>Main</c> of its own
/// that constructs <c>new App()</c> — which collides with this one (CS0017) and, once
/// <see cref="App"/> takes a constructor argument, does not compile at all. Naming this method
/// in <c>StartupObject</c> would not have been enough: it resolves which <c>Main</c> runs, not
/// whether the generated one compiles. So the .csproj compiles <c>App.xaml</c> as a
/// <c>Page</c> instead, which still generates <c>InitializeComponent</c> and the resource
/// loading T1.11 will use, but generates no entry point. (T1.0's placeholder
/// <c>Program.Main</c> is gone — this replaces it.)
/// </para>
/// <para>
/// <strong>Synchronous on purpose.</strong> An <c>async Task Main</c> under
/// <c>[STAThread]</c> is a trap here: a continuation after an <c>await</c> resumes on a thread
/// pool thread, and <see cref="System.Windows.Application.Run()"/> must be called on the STA
/// thread that owns the dispatcher. Blocking here costs nothing, because the dispatcher this
/// would otherwise stall does not exist until <c>Run</c> is called.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Decides whether this process is the resident dashboard, then builds and starts the host,
    /// runs the WPF app, and shuts the host down (Impl §5.3).
    /// </summary>
    /// <returns>
    /// 0 when this process ran the dashboard, or successfully handed over to the one that is.
    /// 1 when startup failed, or when a resident instance exists but could not be reached — both
    /// are conditions the operator has to fix, and a tray app's exit code is the only place a
    /// launch that produced no window can say so to anything but the log.
    /// </returns>
    [STAThread]
    public static int Main()
    {
        IHost? host = null;

        // The /show action is handed this rather than the container: a post arrives on a Kestrel
        // thread, and resolving MainWindow there would *construct* the window — and its view
        // model, and its bound collection — off the UI thread. It also latches a request that
        // lands before the window exists, which is the gap that would otherwise answer 200 and
        // raise nothing (see WindowSurfacer).
        WindowSurfacer? surfacer = null;

        DashboardPaths paths;
        SingleInstanceGate gate;

        try
        {
            paths = new DashboardPaths();
            gate = SingleInstanceGate.Acquire(paths.Root);
        }
        catch (Exception ex)
        {
            // Before the logger exists, so the static sink is all there is. Reaching here means
            // the data folder or the gate could not be resolved at all; without this the throw
            // would leave the process with no window, no tray and no diagnosis whatever.
            Log.Fatal(ex, "Claude Dashboard could not work out where its data folder is, or take the single-instance gate.");
            Log.CloseAndFlush();

            return 1;
        }

        // Released on every path out of this method, and that is not what frees the gate for the
        // next launch: Windows closes the handle at process exit either way. The evidence for
        // that is the manual kill-and-restart — a running dashboard killed outright, and the next
        // launch taking the gate as an ordinary first instance.
        //
        // Deleting this release also leaves the whole suite green, and that is a different and
        // much narrower claim: it says nothing about the operating system, only that no test
        // depends on the call — which an entirely uncovered branch would produce just as well.
        // It is here so the gate is given up at the same point everything else is.
        using (gate)
        {
            try
            {
                // Impl §5.3's two interlocks. The gate is the authority on "another copy of us is
                // running"; the port only corroborates, because the port is fixed and after a hard
                // kill anything at all may be holding it. See StartupDecision.
                var settings = new SettingsStore(paths).Load().Settings;
                var probe = HealthProbe.Probe(settings.Port, gate.Name);
                var action = StartupDecision.For(gate.IsFirstInstance, probe.Occupant);

                if (action is StartupAction.SignalAndExit or StartupAction.ReportAndExit)
                {
                    return StandDown(paths, settings, action, probe);
                }

                host = AppHost.Build(
                    paths,
                    onShow: () => surfacer!.Request(),
                    ingressAvailable: action == StartupAction.StartNormally);

                // Between Build and Start, and that ordering is load-bearing rather than
                // stylistic: Build composes and Start binds the socket, so no /show can arrive
                // until after this line. The null-forgiving operator is deliberate — if that
                // ordering were ever broken, a throw here is caught by the ingress handler and
                // logged at Error, where `?.` would drop the request in silence, which is the
                // failure this whole type exists to remove.
                surfacer = new WindowSurfacer(host.Services.GetRequiredService<Serilog.ILogger>());

                host.Start();

                if (gate.TookOverFromACrash)
                {
                    Log.Warning(
                        "The previous dashboard did not exit cleanly — it left its single-instance gate " +
                        "abandoned. This one has taken it over.");
                }

                // Impl §9.3: a hook exists only while something is answering the port it names.
                // After Start, never before — between registering and binding there would be a
                // window in which Claude Code posts to a port nothing answers, which is the state
                // the whole lifecycle exists to avoid.
                var hooks = host.Services.GetRequiredService<HookLifecycle>();
                hooks.Register();

                var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

                // Best effort on the way down. A terminating fault runs managed code once more,
                // and taking the handlers out then is the difference between the operator's next
                // Claude Code turn being normal and it carrying an error they cannot diagnose.
                using var handlers = AppHost.WireProcessExceptionHandlers(policy, () => hooks.Unregister());

                var app = new App(policy);

                // Logoff and shutdown. WPF raises this before it tears the application down, and
                // nothing handled it before — so until now, every logoff left the handlers
                // registered with nothing listening until the next logon.
                app.SessionEnding += (_, _) => hooks.Unregister();

                // This is the UI thread, and the only place the window and its view model may be
                // built. Attaching the tick here rather than inside the container is what keeps the
                // consumer thread from ever constructing UI-thread state (see UiTick).
                var window = host.Services.GetRequiredService<MainWindow>();
                var tick = host.Services.GetRequiredService<UiTick>();
                tick.Attach(window.ViewModel);

                // Hands the window over, and raises it if a /show already asked while the window
                // did not exist. On this thread, which owns it.
                surfacer.Attach(window);

                // The tray is a Win32 shell notification and a WPF ContextMenu, so it belongs on
                // this thread with the window. Constructing it also puts it on the clock — see
                // TrayIcon, which attaches itself so that being registered and being driven cannot
                // come apart the way T1.6's and T1.11's ticks did.
                using var tray = host.Services.GetRequiredService<TrayIcon>();
                tray.ViewModel.OpenRequested += (_, _) => window.ToggleDashboard();
                tray.ViewModel.QuitRequested += (_, _) => app.Shutdown();

                var exitCode = app.Run(window);

                // The ordinary quit — the tray's Quit item, and a logoff that already ran the
                // handler above. Removing twice is a no-op, so both paths can call it.
                hooks.Unregister();

                host.StopAsync().GetAwaiter().GetResult();
                Log.Information("Claude Dashboard exited with code {ExitCode}.", exitCode);
                return exitCode;
            }
            catch (IOException ex) when (ex.InnerException is AddressInUseException)
            {
                // Impl §3.1 fixes the port so the hook URL stays stable, which makes "something
                // else already has it" the likeliest startup failure this app will ever have. The
                // operator has no console and no window, so the log line has to be the whole
                // diagnosis — a stack trace alone would say what happened but not what to do.
                Log.Fatal(
                    ex,
                    "Claude Dashboard could not start: another process is already using the ingress port. " +
                    "Stop that process, or set a different \"port\" in the settings file, and remember that " +
                    "the hook URL registered with Claude Code must match it.");
                return 1;
            }
            catch (Exception ex)
            {
                // Startup failed before the handlers could take over. Nothing is running to report
                // this any other way, so the log file is the only place it can go.
                Log.Fatal(ex, "Claude Dashboard failed to start.");
                return 1;
            }
            finally
            {
                host?.Dispose();
                Log.CloseAndFlush();
            }
        }
    }

    /// <summary>
    /// Hands over to the dashboard that is already running, or explains why it could not
    /// (Impl §5.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This process has no window and no console, so the log is the only channel it has. It gets
    /// its own logger — the same rolling file, opened <c>shared</c> — because nothing else in
    /// this process has built one yet.
    /// </para>
    /// <para>
    /// <strong>A refused <c>/show</c> is an Error, not a shrug.</strong> The same gate name means
    /// the same data folder means the same token, so it should have been authorised. If it was
    /// not, something is wrong with the token or the settings, and the operator's symptom is a
    /// shortcut that does nothing at all. Exiting is still right — two dashboards on one data
    /// folder is what the gate exists to prevent — but exiting quietly is not.
    /// </para>
    /// </remarks>
    private static int StandDown(
        DashboardPaths paths,
        DashboardSettings settings,
        StartupAction action,
        HealthProbeResult probe)
    {
        var foldersReady = paths.TryEnsureCreated(out _);
        using var logger = AppHost.CreateLogger(paths, settings.Logging, foldersReady);

        if (action == StartupAction.ReportAndExit)
        {
            logger.Error(
                "Claude Dashboard will not start: {Reason}",
                StartupDecision.ExplainReportAndExit(probe.Occupant, settings.Port));

            return 1;
        }

        var result = ShowSignal.Send(
            settings.Port,
            Environment.GetEnvironmentVariable(IngressToken.EnvironmentVariable));

        switch (result.Outcome)
        {
            case ShowSignalOutcome.Shown:
                logger.Information(
                    "Claude Dashboard is already running; asked it to surface its window and exited.");
                return 0;

            case ShowSignalOutcome.Rejected:
                logger.Error(
                    "Claude Dashboard is already running on port {Port}, but it refused this process's " +
                    "/show with {Status}. The token in {Variable} does not match the one it started with. " +
                    "No window will appear. Correct the variable and restart the dashboard.",
                    settings.Port,
                    (int)result.StatusCode!.Value,
                    IngressToken.EnvironmentVariable);
                return 1;

            case ShowSignalOutcome.Failed:
                logger.Error(
                    "Claude Dashboard is already running on port {Port}, but its /show answered {Status}. " +
                    "No window will appear.",
                    settings.Port,
                    (int)result.StatusCode!.Value);
                return 1;

            default:
                logger.Error(
                    "Claude Dashboard appears to be running, but nothing answered /show on port {Port}: " +
                    "{Problem}. No window will appear.",
                    settings.Port,
                    result.Problem);
                return 1;
        }
    }
}
