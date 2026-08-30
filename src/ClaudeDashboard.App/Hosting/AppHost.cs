using System.Globalization;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ILogger = Serilog.ILogger;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// Composes the Generic Host that owns the process (Impl §3.1, §10.1).
/// </summary>
/// <remarks>
/// <para>
/// Wiring only. Nothing here decides anything about sessions, states, ordering, grouping or
/// sound — that all lives in Core and reaches this layer as registrations.
/// </para>
/// <para>
/// <strong>Exactly one hosted service of ours, and that is a correctness requirement.</strong>
/// <see cref="EventConsumer"/> owns both the channel read and the nudge tick on one loop,
/// because the Registry and the sound engine are lock-free on the assumption that a single
/// thread touches them (Impl §2.2, §4). A second <c>BackgroundService</c> — or a separate
/// <c>PeriodicTimer</c> loop, which Impl §4's wording invites — would race, and the T1.5 review
/// demonstrated that race concretely. A test pins the count at one; do not add another without
/// reading <see cref="SingleWriterGuard"/> first.
/// </para>
/// </remarks>
public static class AppHost
{
    /// <summary>Builds the host: settings, logging, the exception policy, and ingress.</summary>
    /// <param name="paths">Where the data folder is; defaults to <c>%LOCALAPPDATA%\ClaudeDashboard\</c>.</param>
    /// <param name="onShow">What a <c>/show</c> post should do; T1.15 supplies it.</param>
    /// <param name="ingressAvailable">
    /// Whether the configured port may be bound (T1.15). False means something else holds it and
    /// this process starts half-deaf; see <see cref="IngressStatus"/> for why it starts at all.
    /// Defaults to true, which is what every caller but <see cref="Program"/> wants.
    /// </param>
    /// <param name="ingress">
    /// The port actually chosen, and whether it was secured (T1.21). <see cref="Program"/> supplies
    /// this because §3.1 chooses the port before the host is built — the choice needs to probe, and
    /// probing needs the single-instance gate name, neither of which exists in here.
    /// <strong>Null keeps the pre-T1.21 behaviour</strong>: bind the base port from settings. Every
    /// test builds a host that way, having already put a free port in its settings file, and making
    /// them derive instead would change what they are testing without saying so.
    /// </param>
    public static WebApplication Build(
        DashboardPaths? paths = null,
        Action? onShow = null,
        bool ingressAvailable = true,
        IngressStatus? ingress = null)
    {
        var resolved = paths ?? new DashboardPaths();
        var foldersReady = resolved.TryEnsureCreated(out var folderFailure);

        var settingsStore = new SettingsStore(resolved);
        var loaded = settingsStore.Load();

        var logger = CreateLogger(resolved, loaded.Settings.Logging, foldersReady);

        ReportStartup(logger, resolved, loaded, foldersReady, folderFailure);

        // Rosters, normalised on the way out of the file: a hand edit can hold a name in two
        // rosters or a roster with no members, and RosterBook can represent neither. Each
        // correction is logged BY ROSTER NAME ONLY — a member name is a session title, and a title
        // can be a model-written summary of the operator's prompt (T1.24, issue #18).
        var (book, corrections) = new RosterSettings { Rosters = loaded.Settings.Rosters }.ToBook();

        foreach (var correction in corrections)
        {
            logger.Warning("The rosters in {SettingsFile} needed correcting. {Correction}", resolved.SettingsFile, correction);
        }

        // NO SILENT FALL-BACK TO THE BASE PORT. A caller that supplies no port gets the same
        // §3.1 choice Program makes — pin, then port.txt, then derive, then walk — because the
        // alternative is a working dashboard bound to the machine-wide port, announcing itself in
        // listening.txt so the hook agrees with it, and wrong for this user. The parameter stays
        // optional so that a test which has already pinned a free port in its settings file keeps
        // getting that port: a pin is attempt 0, so pinning still wins.
        if (ingress is null)
        {
            var chosen = PortSelection.ForDataFolder(resolved, loaded.Settings);

            ingress = ingressAvailable && chosen.Found
                ? IngressStatus.Healthy(chosen.Port)
                : IngressStatus.Unavailable(chosen.Port);
        }

        var builder = WebApplication.CreateSlimBuilder();

        // Route the framework's own diagnostics into the rolling file. Without this bridge,
        // Kestrel's bind failure on the ingress port — the likeliest startup failure this app has
        // — would reach no sink at all, and the operator would see a dashboard that starts,
        // says so, and then never receives a hook, with nothing anywhere explaining why.
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: false);

        // Impl §3.1: loopback only, with the port chosen per user since T1.21. Loopback is the whole of
        // the network boundary — nothing off-machine may post events (TS §II.5).
        //
        // When the configured port is held by something that is not us, Kestrel is pointed at an
        // ephemeral loopback port instead, so the host starts and the window and tray still run.
        // It listens somewhere no hook is addressed to, which is the honest expression of "this
        // dashboard cannot hear anything" — and IngressStatus is what says so out loud. There is
        // no way to make Kestrel bind nothing: clearing the URLs setting falls back to port 5000,
        // which would quietly take a port a development server commonly wants (measured). Note
        // also that ListenLocalhost rejects port 0 outright — dynamic binding needs an explicit
        // address, which is why this is Listen(IPAddress.Loopback, 0).
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (ingress.CanReceiveHooks)
            {
                kestrel.ListenLocalhost(ingress.Port);
            }
            else
            {
                kestrel.Listen(System.Net.IPAddress.Loopback, 0);
            }

            kestrel.AddServerHeader = false;
        });

        builder.Services.AddSingleton(ingress);
        builder.Services.AddSingleton(resolved);

        // Claude Code's own configuration directory, resolved the way Claude Code resolves it.
        // A separate registration from DashboardPaths, and deliberately so — see ClaudeCodePaths.
        builder.Services.AddSingleton<ClaudeCodePaths>();
        builder.Services.AddSingleton<IVirtualDesktopService, VirtualDesktopService>();
        builder.Services.AddSingleton<WindowPresence>();
        builder.Services.AddSingleton<HookInstaller>();
        builder.Services.AddSingleton<IngressAnnouncement>();
        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(loaded.Settings);
        builder.Services.AddSingleton<ILogger>(logger);
        builder.Services.AddSingleton<UnhandledExceptionPolicy>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IngressToken>();
        builder.Services.AddSingleton<HookEventMapper>();

        // The pipeline (Impl §4). Exactly one hosted service reads the channel and runs the
        // nudge tick, on one loop — see EventConsumer for why that is a correctness
        // requirement rather than a simplification.
        // One shared region: a thread inside the Registry cannot also be inside the sound engine.
        builder.Services.AddSingleton<SingleWriterGuard>();
        builder.Services.AddSingleton<EventPipeline>();
        builder.Services.AddSingleton<IEventSink>(sp => sp.GetRequiredService<EventPipeline>().Sink);
        // Built through the container rather than beside the settings, because RosterStore announces
        // every change on the pipeline and so needs the sink — which only exists once EventPipeline
        // is registered.
        //
        // THE LOADED BOOK GOES TO THE CONSTRUCTOR, SO LOADING THE FILE ANNOUNCES NOTHING. Replace is
        // the only mutator and can therefore announce unconditionally; reading a file at startup is
        // not a change to a running system, and an announcement here would put an event at the head
        // of the pipeline before the consumer has started.
        builder.Services.AddSingleton(sp => new RosterStore(sp.GetRequiredService<IEventSink>(), book));
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<SoundCatalog>();
        builder.Services.AddSingleton<ISoundPlayer, NAudioSoundPlayer>();

        // The engine's options are Core's defaults with the operator's file layered on, one way
        // only (Impl Part 7, Part 8). This is the first setting anything consumes, and the
        // direction is the whole point: Core owns the defaults and never learns a file exists.
        builder.Services.AddSingleton(loaded.Settings.Sound.Apply());
        builder.Services.AddSingleton<SoundPolicyEngine>();
        builder.Services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        builder.Services.AddSingleton<SessionProjection>();

        // The UI (T1.10, T1.11). The window and its view model own UI-thread state, so they must
        // be resolved on the UI thread and nowhere else — Program does, once, before Run.
        // UiTick is the wire from the consumer's tick to the age and staleness display; it is
        // handed the view model rather than resolving one, so that nothing can construct the UI
        // from the consumer thread.
        // The manual ack tier (Design Document §4). It takes the event sink, not the Registry:
        // TS §I.3 requires every ack source to travel one path, and the Registry is lock-free on
        // the assumption that the consumer is its only writer.
        builder.Services.AddSingleton<IAckPublisher, AckPublisher>();
        builder.Services.AddSingleton<IClipboard, WindowsClipboard>();
        builder.Services.AddSingleton<MotionPolicy>();
        builder.Services.AddSingleton<UiTick>();
        builder.Services.AddSingleton<IUiTick>(sp => sp.GetRequiredService<UiTick>());
        builder.Services.AddSingleton<IRosterPersistence, SettingsRosterPersistence>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<ISoundModeReader>(sp => sp.GetRequiredService<SoundPolicyEngine>());
        builder.Services.AddSingleton<TrayViewModel>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventConsumer>());
        builder.Services.AddSingleton<EventConsumer>();

        // The durable event log (T1.17). The archive is the channel the consumer hands events to
        // without ever waiting; the writer is the only thing that touches the file. They are
        // separate registrations because they are separate threads: if the store were reachable
        // from the consumer, a slow disk would stall the Registry's only writer.
        builder.Services.AddSingleton<EventArchive>();
        builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
        builder.Services.AddSingleton<EventArchiveWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventArchiveWriter>());

        // The seam the composition guard reads (T1.12b; ServiceCompositionTests). A built
        // WebApplication does not publish its own descriptors — measured on a clean host, not
        // assumed — and without them the guard cannot see any type registered behind an
        // interface, which is most of them. Deferred so the snapshot is taken after every
        // registration above, and typed read-only so nothing can reach through it to mutate the
        // container.
        builder.Services.AddSingleton<IReadOnlyList<ServiceDescriptor>>(_ => [.. builder.Services]);

        var app = builder.Build();

        // Resolving the projection subscribes it to the Registry, and the sound engine has to
        // hear about changes on the consumer thread that raised them.
        var registry = app.Services.GetRequiredService<SessionRegistry>();
        var sound = app.Services.GetRequiredService<SoundPolicyEngine>();
        var rosters = app.Services.GetRequiredService<RosterStore>();
        registry.SessionChanged += (_, e) =>
            sound.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, rosters.Book));
        _ = app.Services.GetRequiredService<SessionProjection>();
        app.MapIngress(onShow);

        if (ingress.CanReceiveHooks)
        {
            logger.Information(
                "Ingress will listen on http://127.0.0.1:{Port} (loopback only). Token check {TokenState}.",
                ingress.Port,
                app.Services.GetRequiredService<IngressToken>().IsConfigured ? "enabled" : "disabled");
        }
        else
        {
            // Error, not Warning. The dashboard will look exactly like a quiet afternoon, and
            // this line is the only place the difference is written down.
            //
            // The advice is shorter since issue #29: the hook names a script, and the script reads
            // listening.txt for the port when it runs, so changing the port changes nothing in
            // Claude Code's settings. Telling a stuck operator to go and edit a URL there would
            // send them looking for something this build never writes.
            logger.Error(
                "Port {Port} is held by another process, so the dashboard cannot receive hooks and " +
                "every session will be missing. It is starting anyway, with the reason in the tray " +
                "tooltip. Free that port, or set a different \"port\" in {SettingsFile}, then restart " +
                "the dashboard. Claude Code's hook settings need no change: the hook finds the new " +
                "port by itself.",
                ingress.Port,
                resolved.SettingsFile);
        }

        return app;
    }

    /// <summary>
    /// Subscribes the two process-wide exception handlers (Impl §10.1). The dispatcher handler
    /// is wired by <see cref="App"/>, which owns the <c>Application</c> that raises it.
    /// </summary>
    /// <remarks>
    /// Returns the subscriptions' removal so a test — or a second host in one process — does not
    /// leave handlers behind on these process-wide events.
    /// </remarks>
    /// <param name="policy">What an unhandled fault does.</param>
    /// <param name="onTerminating">
    /// Run once when a fault is taking the process down, before the log is flushed — T1.18 uses it
    /// to take the hook handlers out of Claude Code's settings. Best effort by nature: this is the
    /// last managed code that runs, and a fault here must not replace one crash with two.
    /// </param>
    public static IDisposable WireProcessExceptionHandlers(
        UnhandledExceptionPolicy policy,
        Action? onTerminating = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
        {
            policy.HandleDomainException(e.ExceptionObject as Exception, e.IsTerminating);

            if (e.IsTerminating && onTerminating is not null)
            {
                try
                {
                    onTerminating();
                }
                catch (Exception cleanupFailure)
                {
                    Log.Error(cleanupFailure, "Could not tidy up while the process was terminating.");
                }
            }

            // The process may be seconds from gone; get it on disk now.
            Log.CloseAndFlush();
        }

        void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (policy.HandleUnobservedTaskException(e.Exception))
            {
                e.SetObserved();
            }
        }

        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        return new Unsubscriber(() =>
        {
            AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;

            // Unwiring is the shutdown path, and it is the last chance to write out counts the
            // storm guard is still holding. Nothing runs a timer to expire a window, so without
            // this a storm that stopped before the process did would take its tail with it —
            // and the tail is where "it stopped when the session ended" is visible.
            policy.Flush();
        });
    }

    /// <summary>Builds the rolling-file logger described by Impl Part 8.</summary>
    /// <remarks>
    /// Internal rather than private because a second instance needs one too, and it must be the
    /// same one: it writes into the same rolling file as the resident instance (the sink is
    /// opened <c>shared</c> for exactly this), so the reason a launch produced no window sits in
    /// the file the operator is already reading rather than somewhere of its own.
    /// </remarks>
    internal static Serilog.Core.Logger CreateLogger(DashboardPaths paths, LoggingSettings logging, bool foldersReady)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()

            // The framework logs four lines per request at Information — request starting,
            // endpoint executing, status code, request finished. Across fifteen busy sessions
            // that buries the dashboard's own diagnostics in its own traffic, in a file kept for
            // a fortnight. Framework warnings and errors still reach the file, which is what the
            // bridge is for: a Kestrel bind failure on the ingress port is the likeliest startup
            // failure this app has, and it must not be silent.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)

            // …except the lifetime messages, which say what was bound and that startup finished.
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext();

        if (foldersReady)
        {
            configuration = configuration.WriteTo.File(
                paths.LogFile,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: logging.RetainedFileCount,
                fileSizeLimitBytes: logging.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: LogEventLevel.Information,
                shared: true,

                // This process can be killed rather than asked to stop — at logoff, or by the
                // scheduled task's restart-on-failure (Impl §10.1) — and neither runs the clean
                // shutdown that would flush. Unflushed diagnostics would be lost in exactly the
                // cases they exist to explain.
                flushToDiskInterval: TimeSpan.FromSeconds(2));
        }

        var logger = configuration.CreateLogger();

        // The static logger is what the AppDomain handler flushes on the way down, when there is
        // no time to resolve anything from the container.
        Log.Logger = logger;
        return logger;
    }

    private static void ReportStartup(
        ILogger logger,
        DashboardPaths paths,
        SettingsLoadResult loaded,
        bool foldersReady,
        string? folderFailure)
    {
        // The effective root, always, at Information. When the override goes wrong the operator's
        // symptom is "my settings are being ignored", and the first question anybody asks is
        // which folder was actually read. Answer it before it is asked.
        logger.Information(
            "Claude Dashboard starting. Data folder {Root} ({RootSource}); logging to {LogFolder}.",
            paths.Root,
            paths.RootSource,
            paths.LogFolder);

        if (paths.RootProblem is { } rootProblem)
        {
            logger.Warning(
                "{Variable} was set but could not be used: {Problem}. Falling back to {Root}.",
                DashboardPaths.HomeVariable,
                rootProblem,
                paths.Root);
        }

        if (!foldersReady)
        {
            logger.Warning(
                "Could not create the data folder {Root}: {Failure}. Running without file logging.",
                paths.Root,
                folderFailure);
        }

        switch (loaded.Outcome)
        {
            case SettingsLoadOutcome.Loaded:
                logger.Information("Settings loaded from {File}.", paths.SettingsFile);
                break;

            case SettingsLoadOutcome.Missing:
                logger.Information(
                    "No settings file at {File}; using defaults.",
                    paths.SettingsFile);
                break;

            case SettingsLoadOutcome.Unreadable:
                logger.Error(
                    "Settings file {File} could not be read: {Problem}. Using defaults; the file is left as it is.",
                    paths.SettingsFile,
                    loaded.Problem);
                break;

            default:
                break;
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
