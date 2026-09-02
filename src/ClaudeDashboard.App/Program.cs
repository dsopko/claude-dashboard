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
    public static int Main(string[] args)
    {
        IHost? host = null;

        // The /show action is handed this rather than the container: a post arrives on a Kestrel
        // thread, and resolving MainWindow there would *construct* the window — and its view
        // model, and its bound collection — off the UI thread. It also latches a request that
        // lands before the window exists, which is the gap that would otherwise answer 200 and
        // raise nothing (see WindowSurfacer).
        WindowSurfacer? surfacer = null;

        // Declared out here so the finally can withdraw the announcement (issue #29). THAT IS THE
        // FOURTH DELETION SITE AND IT COVERS A HOLE THE OLD LIFECYCLE HAD: both catch blocks below
        // return without reaching the ordinary quit, so a throw after the bind and before the
        // window ran — a view model that would not build, a settings file that would not load —
        // left the dashboard announced on an exit that was otherwise perfectly orderly.
        IngressAnnouncement? announcement = null;

        DashboardPaths paths;
        SingleInstanceGate gate;

        try
        {
            paths = new DashboardPaths();

            // The one-shot hook switches, and they run BEFORE the gate deliberately (issue #29).
            // An operator whose dashboard is running must still be able to repair their hooks, and
            // a switch that stood down because the application was open would be useless exactly
            // when it was wanted. Neither switch starts the UI or the host.
            if (HookSwitches.Requested(args) is { } requested)
            {
                return RunHookSwitch(requested, paths);
            }

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
                // running"; the port only corroborates, because after a hard kill anything at all
                // may be holding it. See StartupDecision.
                var settings = new SettingsStore(paths).Load().Settings;

                // WHERE THE FIRST INSTANCE ACTUALLY IS, WHICH IS NO LONGER A CONSTANT. While the
                // port was fixed, both launches read it from the settings file. With a per-user
                // port (§3.1) the running instance may be anywhere in its range, and port.txt is
                // the only thing that says where.
                //
                // AND WITH NO port.txt THERE IS NOTHING TO CORROBORATE, so nothing is probed. The
                // first version of this fell back to the base port, which was the old behaviour
                // and is now actively wrong: the base port belonging to somebody else is the
                // ORDINARY case once ports are per user. Measured — a staged instance on a fresh
                // data folder found the operator's dashboard on 52789, read it as "another
                // instance of ours", and started deaf while the port it had correctly derived for
                // itself sat free. The gate is the authority on "another copy of us is running"
                // (§5.3); the port only ever corroborated, and a port this user has never bound
                // corroborates nothing about this user.
                var recorded = PortFile.Read(paths);
                var probe = recorded is { } lastPort
                    ? HealthProbe.Probe(lastPort, gate.Name)
                    : new HealthProbeResult(PortOccupant.Free);
                var action = StartupDecision.For(gate.IsFirstInstance, probe.Occupant);

                if (action is StartupAction.SignalAndExit or StartupAction.ReportAndExit)
                {
                    return StandDown(paths, settings, action, probe, recorded ?? settings.Port);
                }

                // §3.1's three attempts. Binding is the only question asked of any of them.
                var choice = ChoosePort(settings, recorded, gate.Name, out var isSid);

                host = AppHost.Build(
                    paths,
                    onShow: () => surfacer!.Request(),
                    ingress: IngressFor(action, choice));

                // Between Build and Start, and that ordering is load-bearing rather than
                // stylistic: Build composes and Start binds the socket, so no /show can arrive
                // until after this line. The null-forgiving operator is deliberate — if that
                // ordering were ever broken, a throw here is caught by the ingress handler and
                // logged at Error, where `?.` would drop the request in silence, which is the
                // failure this whole type exists to remove.
                // The port was chosen before the logger existed, so it is reported now that one does.
                ReportPortChoice(
                    host.Services.GetRequiredService<Serilog.ILogger>(), choice, isSid);

                surfacer = new WindowSurfacer(host.Services.GetRequiredService<Serilog.ILogger>());

                host.Start();

                if (gate.TookOverFromACrash)
                {
                    Log.Warning(
                        "The previous dashboard did not exit cleanly — it left its single-instance gate " +
                        "abandoned. This one has taken it over.");
                }

                // Issue #29 made the hook an install step rather than a lifecycle: it names a
                // script, so one entry is right whether a dashboard is running or not, and nothing
                // is written on the way out. Issue #39 added the one thing a start still does write
                // — it puts the handler back when it has gone missing, because until T1.32 nothing
                // called the install step at all and a new user received no events for ever.
                //
                // AFTER Start, never before — between announcing and binding there would be a
                // window in which the script posts to a port nothing answers.
                announcement = host.Services.GetRequiredService<IngressAnnouncement>();
                announcement.Announce();

                // Rewritten at every start when it differs, so a fix in the build reaches an
                // install that already exists. See HookScript.
                HookScript.EnsureWritten(paths, host.Services.GetRequiredService<Serilog.ILogger>());

                // Reads the settings, and repairs the handler when it is missing and the operator
                // has not opted out (issue #39). Read-only in every other case, and it never
                // writes a file it could not read. Without the check a hook removed by anything at
                // all is undetectable: the dashboard receives nothing, which looks exactly like a
                // quiet day.
                StartupHookInstall.Run(
                    host.Services.GetRequiredService<HookInstaller>(),
                    settings.InstallHooksAtStart,
                    host.Services.GetRequiredService<Serilog.ILogger>());

                var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();

                // Best effort on the way down. A terminating fault runs managed code once more,
                // and withdrawing the announcement then is the difference between the next hook
                // event doing nothing and it posting the operator's prompt to whatever has taken
                // the port.
                using var handlers = AppHost.WireProcessExceptionHandlers(policy, () => announcement.Withdraw());

                var app = new App(policy);

                // Logoff and shutdown. WPF raises this before it tears the application down, and
                // nothing handled it before — so until now, every logoff left the dashboard
                // announced with nothing listening until the next logon.
                app.SessionEnding += (_, _) => announcement.Withdraw();

                // This is the UI thread, and the only place the window and its view model may be
                // built. Attaching the tick here rather than inside the container is what keeps the
                // consumer thread from ever constructing UI-thread state (see UiTick).
                var window = host.Services.GetRequiredService<MainWindow>();
                var tick = host.Services.GetRequiredService<UiTick>();
                tick.Attach(window.ViewModel);

                // Where it opens, whether it floats, and the pin to every virtual desktop
                // (Impl §5.4). Before the surfacer because placement should be in force the first
                // time the window is drawn — but this order is NOT the guard, and nothing here
                // depends on it. An early version only subscribed to SourceInitialized, and then
                // Attach showing a latched /show meant the event had already fired and the window
                // was never pinned. That is fixed inside WindowPresence.Apply, which pins at once
                // when a handle exists and subscribes only when it does not. Both orders work; the
                // reason is written where the check is.
                var presence = host.Services.GetRequiredService<WindowPresence>();
                var settingsStore = host.Services.GetRequiredService<SettingsStore>();
                presence.Apply(window, host.Services.GetRequiredService<DashboardSettings>().Window);

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

                // Where the operator left the window, so the next launch opens there (Impl §5.4).
                // Best effort: a dashboard that cannot write its own settings file should still
                // exit cleanly, and losing a remembered position is a smaller failure than a
                // crash on the way out.
                try
                {
                    var current = settingsStore.Load().Settings;
                    settingsStore.Save(current with { Window = WindowPresence.Capture(window, window.Topmost) });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Could not save the window position.");
                }

                // The ordinary quit — the tray's Quit item, and a logoff that already ran the
                // handler above. Withdrawing twice is a no-op, so both paths can call it.
                announcement.Withdraw();

                host.StopAsync().GetAwaiter().GetResult();
                Log.Information("Claude Dashboard exited with code {ExitCode}.", exitCode);
                return exitCode;
            }
            catch (IOException ex) when (ex.InnerException is AddressInUseException)
            {
                // A loopback bind is machine-wide while everything else here is per user, so
                // "something else already has it" stays a likely startup failure even with the port
                // derived per user (Impl §3.1). The operator has no console and no window, so the
                // log line has to be the whole diagnosis — a stack trace alone would say what
                // happened but not what to do.
                //
                // AND SINCE ISSUE #29 THE ADVICE IS SHORTER, BECAUSE THE HOOK CARRIES NO PORT. It
                // names post-status.cmd, which reads listening.txt at the moment it runs. This line
                // used to end "remember that the hook URL registered with Claude Code must match
                // it" — which now sends an operator who is already stuck into a file this build
                // promises never to write, hunting a URL that is not there.
                Log.Fatal(
                    ex,
                    "Claude Dashboard could not start: another process is already using the ingress port. " +
                    "Stop that process, or set a different \"port\" in the settings file, then restart. " +
                    "Claude Code's hook settings need no change: the hook finds the new port by itself.");
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
                // The fourth withdrawal, and the one that catches an orderly failure: every path
                // out of the block above runs this, including both catch arms. It is null when the
                // bind never happened, and withdrawing twice is a no-op — so this is safe to run
                // after the ordinary quit has already done it.
                announcement?.Withdraw();

                host?.Dispose();
                Log.CloseAndFlush();
            }
        }
    }
    /// <summary>
    /// Runs <c>--install-hooks</c> or <c>--remove-hooks</c> and exits, without starting anything
    /// (issue #29).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No host, no gate, no window.</strong> This is a one-shot that edits Claude Code's
    /// settings and leaves. It builds only what it needs: the data folder, a logger, and the
    /// installer.
    /// </para>
    /// <para>
    /// <strong>Every line goes to the log as well as to the console.</strong> The console is
    /// borrowed and may not be there at all — from Explorer, from a scheduled task, from a T10.2
    /// that redirects its own streams there is no parent to attach to. The log is the channel that
    /// is always there, and a record of what changed in the operator's settings file is worth
    /// keeping in any case.
    /// </para>
    /// </remarks>
    private static int RunHookSwitch(string requested, DashboardPaths paths)
    {
        // Before anything touches Console: .NET caches the standard streams on first use, and a
        // single stray write would fix the discarding sink in place for the life of the process.
        var attached = ConsoleReport.TryAttach();

        var foldersReady = paths.TryEnsureCreated(out _);
        var store = new SettingsStore(paths);
        var settings = store.Load().Settings;
        using var logger = AppHost.CreateLogger(paths, settings.Logging, foldersReady);

        var installer = new HookInstaller(new ClaudeCodePaths(), paths, new Adapters.SystemClock(), logger);

        var code = HookSwitches.Run(requested, installer, line =>
        {
            logger.Information("{Switch}: {Line}", requested, line);

            if (attached)
            {
                Console.Out.WriteLine(line);
            }
        });

        // The switch's decision outlives the switch (issue #39). Without this, --remove-hooks would
        // be undone by the next start and would therefore mean nothing. Re-read inside rather than
        // reusing `settings` above: HookSwitches.Run has been and gone, and the file may have moved
        // under us. It never throws and never writes a file it could not read.
        StartupHookInstall.RecordSwitch(requested, code, store, logger);

        if (!attached)
        {
            logger.Information(
                "{Switch} finished with exit code {Code}. There was no console to report to, so this " +
                "log is the report.",
                requested,
                code);
        }

        return code;
    }

    /// <summary>
    /// Works through §3.1's three attempts and says, once, how the port was arrived at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe is <see cref="HealthProbe.Probe"/>, which answers "may I have this port" by
    /// trying to bind it and classifies the occupant when the answer is no. That is the whole
    /// mechanism: <strong>there is no registry of who owns which port and none is to be built</strong>.
    /// </para>
    /// <para>
    /// <strong>Logged whichever way it goes, and the trail is the interesting part.</strong> A
    /// dashboard on an unexpected port with nothing in the log saying why is the shape this
    /// project has spent a fortnight removing — and the trail distinguishes "another user's
    /// dashboard was there" from "a stranger was there", which are different diagnoses with
    /// different fixes.
    /// </para>
    /// </remarks>
    private static PortChoice ChoosePort(DashboardSettings settings, int? recorded, string gateName, out bool isSid)
    {
        var identity = UserIdentity.Resolve(out isSid);

        return PortSelection.Choose(
            DashboardSettings.DefaultPort,
            identity,
            recorded,
            port => HealthProbe.Probe(port, gateName).Occupant,
            pinned: settings.Port);
    }

    /// <summary>Turns the port choice into what the tray will say about it.</summary>
    /// <remarks>
    /// Three outcomes, three lines. A refused pin is not the same event as a port taken out from
    /// under the dashboard: the operator chose that port, and the tooltip says "pinned" so they
    /// know which setting is the thing to change.
    /// </remarks>
    private static IngressStatus IngressFor(StartupAction action, PortChoice choice)
    {
        if (action == StartupAction.StartNormally && choice.Found)
        {
            return IngressStatus.Healthy(choice.Port);
        }

        return choice.PinRefused
            ? IngressStatus.PinnedPortTaken(choice.Port)
            : IngressStatus.Unavailable(choice.Port);
    }

    /// <summary>Says how the port was arrived at, once the logger that can record it exists.</summary>
    /// <remarks>
    /// <strong>Separate from the choice because of when each can happen.</strong> The port is
    /// chosen before <see cref="AppHost.Build"/>, and Build is what creates the file sink — so the
    /// first version logged the choice through the static logger and <em>every line went nowhere</em>.
    /// A dashboard on an unexpected port with nothing in the log saying why is the exact shape this
    /// project keeps removing, and it was reintroduced by logging in the wrong half of start-up.
    /// Found by reading a live run rather than by a test: nothing asserts on a line that is never
    /// written.
    /// </remarks>
    private static void ReportPortChoice(Serilog.ILogger logger, PortChoice choice, bool isSid)
    {
        if (!isSid)
        {
            logger.Warning(
                "Could not read this account's SID, so the ingress port is derived from the account " +
                "name instead. The port stays stable for this user on this machine, which is what the " +
                "derivation needs; it is only less stable across a rename.");
        }

        if (choice.Found)
        {
            logger.Information(
                "Ingress port {Port}, chosen from {Source} (base {Base}). Candidates: {Trail}",
                choice.Port,
                choice.Source,
                DashboardSettings.DefaultPort,
                choice.Trail);
        }
        else if (choice.PinRefused)
        {
            // ITS OWN SENTENCE, BECAUSE THE WALK MESSAGE IS WRONG THREE WAYS HERE. Free ports
            // exist and this code declined them; the base port is one the operator never chose;
            // and the pin — the one thing they did, and the only thing they can undo — went
            // unmentioned. This is a deliberate refusal, so it reads as one.
            logger.Error(
                "Port {Port} is pinned in settings.json and something else is holding it, so the " +
                "dashboard is starting deaf on purpose rather than moving to another port. A pinned " +
                "port is usually a contract with something outside the dashboard — a firewall rule, " +
                "a proxy entry, a script that posts to it — and quietly answering somewhere else " +
                "would leave all of those pointing at a port nothing serves. Free port {Port}, or " +
                "change or remove the \"port\" setting and restart. What is there now: {Trail}",
                choice.Port,
                choice.Port,
                choice.Trail);
        }
        else
        {
            logger.Error(
                "No free loopback port after {Attempts} attempts from base {Base}. The dashboard will " +
                "start and will not hear anything. Candidates: {Trail}",
                choice.Attempts.Count,
                DashboardSettings.DefaultPort,
                choice.Trail);
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
        HealthProbeResult probe,
        int? port)
    {
        var foldersReady = paths.TryEnsureCreated(out _);
        using var logger = AppHost.CreateLogger(paths, settings.Logging, foldersReady);

        if (action == StartupAction.ReportAndExit)
        {
            logger.Error(
                "Claude Dashboard will not start: {Reason}",
                StartupDecision.ExplainReportAndExit(probe.Occupant, port));

            return 1;
        }

        // Unreachable by construction: SignalAndExit is only chosen when the probe found OUR
        // instance, and nothing is probed unless a port was recorded. Guarded rather than
        // asserted with "!" because the alternative to being right here is signalling a port this
        // user never bound — which is the defect class this task has now produced four times.
        if (port is not { } target)
        {
            logger.Error(
                "Claude Dashboard is already running, but this process has no recorded port to signal. " +
                "No window will appear. Open the running dashboard from its tray icon.");

            return 1;
        }

        var result = ShowSignal.Send(
            target,
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
                    port,
                    (int)result.StatusCode!.Value,
                    IngressToken.EnvironmentVariable);
                return 1;

            case ShowSignalOutcome.Failed:
                logger.Error(
                    "Claude Dashboard is already running on port {Port}, but its /show answered {Status}. " +
                    "No window will appear.",
                    port,
                    (int)result.StatusCode!.Value);
                return 1;

            default:
                logger.Error(
                    "Claude Dashboard appears to be running, but nothing answered /show on port {Port}: " +
                    "{Problem}. No window will appear.",
                    port,
                    result.Problem);
                return 1;
        }
    }
}
