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
    /// <param name="claude">
    /// Claude Code's configuration folder. Null resolves it the way Claude Code does. Tests pass a
    /// scratch folder, so a host never reads the operator's real Claude Code settings.
    /// </param>
    /// <param name="settingsAtStart">
    /// What <see cref="Program"/> did with the settings file at this start (T1.56): its first load,
    /// and the backup or the refusal. When given, the host runs on that first load and does not read
    /// the file again, so this start is the start the first load describes. Null reads the file here,
    /// as every test that is not about the settings does.
    /// </param>
    public static WebApplication Build(
        DashboardPaths? paths = null,
        Action? onShow = null,
        bool ingressAvailable = true,
        IngressStatus? ingress = null,
        ClaudeCodePaths? claude = null,
        SettingsAtStart? settingsAtStart = null)
    {
        var resolved = paths ?? new DashboardPaths();
        var foldersReady = resolved.TryEnsureCreated(out var folderFailure);

        var loaded = settingsAtStart?.Original ?? new SettingsStore(resolved).Load();

        var logger = CreateLogger(resolved, loaded.Settings.Logging, foldersReady);

        // With the logger, so that a save the run refuses says so (T1.56).
        var settingsStore = new SettingsStore(resolved, logger);

        ReportStartup(logger, resolved, loaded, foldersReady, folderFailure, settingsAtStart);

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

            ingress = !chosen.Found
                ? IngressStatus.NotBound(chosen, resolved.SettingsFile)
                : ingressAvailable
                    ? IngressStatus.Healthy(chosen.Port)
                    : IngressStatus.Unavailable(chosen.Port, settingsFile: resolved.SettingsFile);
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
        builder.Services.AddSingleton(claude ?? new ClaudeCodePaths());
        builder.Services.AddSingleton<IVirtualDesktopService, VirtualDesktopService>();
        builder.Services.AddSingleton<WindowPresence>();
        builder.Services.AddSingleton<HookCheck>();

        // The plugin route (issue #30): the claude program registers the hook, and the dashboard
        // never writes Claude Code's settings. Nothing is started by resolving these.
        builder.Services.AddSingleton<IClaudeCli, ClaudeCli>();
        builder.Services.AddSingleton<PluginInstaller>();

        // What a start found about the hook route that the operator must see (the ruling of 2026-10-01).
        // Set by StartupHookInstall, read by the tray and, through it, the window.
        builder.Services.AddSingleton<HookNotice>();

        // The path from Claude Code (T1.61, issue #74): when a message last arrived, the refusals, and the
        // self-test that runs the script as Claude Code does. Written on request threads and the
        // self-test's thread; read on the tray's tick and by /state.
        builder.Services.AddSingleton<HookHealth>();
        builder.Services.AddSingleton<HookSelfTest>();
        builder.Services.AddSingleton<SelfTestNotice>();
        builder.Services.AddSingleton<RefusedNotice>();

        // Start with Windows and the Settings window (issue #36). The registry seam is the real HKCU
        // here; the exe comes from Velopack, and is null for a copy that is not installed, which then
        // never reads or writes the registry at all.
        builder.Services.AddSingleton<IStartupRegistry, WindowsStartupRegistry>();
        builder.Services.AddSingleton(sp => new StartWithWindows(
            sp.GetRequiredService<IStartupRegistry>(),
            InstalledCopy.CurrentExe(sp.GetRequiredService<ILogger>()),
            sp.GetRequiredService<ILogger>()));
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<SettingsWindowHost>();
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
        // By factory, with the clock that stamps the last shed for the notice (T1.58).
        builder.Services.AddSingleton(sp => new EventPipeline(
            sp.GetRequiredService<ILogger>(),
            clock: sp.GetRequiredService<Core.Ports.IClock>()));
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

        // What the sound device notice reads (T1.55): the player's own output state, through an App
        // interface, so the notice never names the NAudio type. The same instance as the player.
        builder.Services.AddSingleton<ISoundOutput>(sp => (ISoundOutput)sp.GetRequiredService<ISoundPlayer>());

        // The engine's options are Core's defaults with the operator's file layered on, one way
        // only (Impl Part 7, Part 8). This is the first setting anything consumes, and the
        // direction is the whole point: Core owns the defaults and never learns a file exists.
        builder.Services.AddSingleton(loaded.Settings.Sound.Apply());

        // The decisions recorder (T1.37, issue #48): the scribe that puts every judgement beside
        // the event that caused it. The engine is built by factory so the recorder reaches it as
        // its IDecisionSink — the sink parameter stays optional for standalone construction, and
        // an optional parameter of a registered service type is what the composition guard
        // forbids, so the factory is the shape that satisfies both.
        builder.Services.AddSingleton<DecisionRecorder>();
        builder.Services.AddSingleton(sp => new SoundPolicyEngine(
            sp.GetRequiredService<ISoundPlayer>(),
            sp.GetRequiredService<Core.Ports.IClock>(),
            sp.GetRequiredService<SingleWriterGuard>(),
            sp.GetRequiredService<SoundPolicyOptions>(),
            sp.GetRequiredService<DecisionRecorder>()));
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

        // By factory for the recorder's sake, like the engine: the tray's light changing is a
        // decision, and the log parameter is optional-and-unregistered so tests build trays
        // without one while the product cannot lose the wiring silently — the composition test
        // asserts it arrived.
        builder.Services.AddSingleton(sp => new TrayViewModel(
            sp.GetRequiredService<SessionProjection>(),
            sp.GetRequiredService<ISoundModeReader>(),
            sp.GetRequiredService<IEventSink>(),
            sp.GetRequiredService<Core.Ports.IClock>(),
            sp.GetRequiredService<IngressStatus>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetRequiredService<DecisionRecorder>(),
            sp.GetRequiredService<NoticeBoard>(),
            sp.GetRequiredService<HookHealth>()));
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddSingleton<StateBoard>();
        // The durable event log (T1.17). The archive is the channel the consumer hands records
        // to without ever waiting; the writer is the only thing that touches the file. They are
        // separate registrations because they are separate threads: if the store were reachable
        // from the consumer, a slow disk would stall the Registry's only writer.
        //
        // The writer's hosted service is registered BEFORE the consumer's, and the order is a
        // correctness constraint, not taste: hosted services stop in reverse registration order,
        // and since T1.37 the archive record is born on the consumer thread. The consumer's
        // stop-drain applies whatever ingress queued and hands the last records to the archive;
        // only a writer that stops after it can still write them. Registered the other way
        // round, the writer drained an empty channel, the consumer archived into a stopped
        // writer, and the final events of a run vanished — caught by HookToDatabaseTests under
        // full-suite load, where the consumer loses the race with shutdown.
        builder.Services.AddSingleton<EventArchive>();
        builder.Services.AddSingleton(sp => new SqliteEventStore(
            sp.GetRequiredService<DashboardPaths>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetRequiredService<Core.Ports.IClock>()));
        builder.Services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<SqliteEventStore>());
        // By factory (T1.60, issue #78): the writer also records this run in the runs table, with the
        // version, the port ingress bound (none for a start that could not bind) and the data folder,
        // stamped when the host has started.
        builder.Services.AddSingleton(sp => new EventArchiveWriter(
            sp.GetRequiredService<EventArchive>(),
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<ILogger>(),
            new RunStart(StartupVersion.Value, ingress.CanReceiveHooks ? ingress.Port : null, resolved.Root),
            sp.GetRequiredService<Core.Ports.IClock>(),
            // How long the history is kept (T1.64, issue #81): this start's first load, like every setting.
            loaded.Settings.History.RetentionDays,
            sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventArchiveWriter>());

        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventConsumer>());
        // By factory (T1.65, issue #76): the consumer also keeps the health board, which counts since the
        // start and by the hour, writes the hourly summary inside the tick, and publishes the snapshot
        // /state reads. Each source is read on the consumer thread, from a value its owner publishes.
        builder.Services.AddSingleton(sp => new HealthBoard(
            HealthSourcesFor(
                ingress,
                sp.GetRequiredService<EventPipeline>(),
                sp.GetRequiredService<EventArchive>(),
                sp.GetRequiredService<HookHealth>(),
                sp.GetRequiredService<EventArchiveWriter>(),
                sp.GetRequiredService<SqliteEventStore>(),
                sp.GetRequiredService<ISoundOutput>(),
                sp.GetRequiredService<ISoundModeReader>()),
            sp.GetRequiredService<Core.Ports.IClock>(),
            sp.GetRequiredService<ILogger>()));
        builder.Services.AddSingleton(sp => new EventConsumer(
            sp.GetRequiredService<EventPipeline>(),
            sp.GetRequiredService<SessionRegistry>(),
            sp.GetRequiredService<SoundPolicyEngine>(),
            sp.GetRequiredService<Core.Ports.IClock>(),
            sp.GetRequiredService<SingleWriterGuard>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetRequiredService<IUiTick>(),
            sp.GetRequiredService<EventArchive>(),
            sp.GetRequiredService<RosterStore>(),
            sp.GetRequiredService<DecisionRecorder>(),
            health: sp.GetRequiredService<HealthBoard>()));

        // The notice row and the tooltip's faults (T1.54, issue #71): the port first (T1.57, issue #14),
        // then the hook route, then the self-test and the refused messages (T1.61, issue #74), then the
        // history, then the sound device (T1.55, issue #72), then the
        // settings file (T1.56, issue #73). The port is the one path for the ingress fault: the tray adds
        // no term of its own. The history and sound notices read a published state on the tray's tick;
        // neither touches the file or the device, so the UI thread never waits on a disk or a driver.
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SqliteEventStore>();
            return new HistoryNotice(() => store.Available == false);
        });
        builder.Services.AddSingleton(sp => new SoundDeviceNotice(sp.GetRequiredService<ISoundOutput>()));
        // The settings notice last (T1.56, issue #73). It is fixed by what this start did with its
        // settings file, and stays until the next start.
        builder.Services.AddSingleton(new SettingsNotice(settingsAtStart, resolved));
        // The queue last (T1.58, issue #3): fell behind, then events lost. Both read the pipeline on
        // the tray's tick, through values it publishes with Interlocked.
        builder.Services.AddSingleton(sp =>
        {
            var pipeline = sp.GetRequiredService<EventPipeline>();
            return new FellBehindNotice(() => pipeline.LastShedAt);
        });
        builder.Services.AddSingleton(sp =>
        {
            var pipeline = sp.GetRequiredService<EventPipeline>();
            return new EventsLostNotice(() => pipeline.DroppedCount);
        });
        builder.Services.AddSingleton(sp => new NoticeBoard(
            sp.GetRequiredService<IngressStatus>(),
            sp.GetRequiredService<HookNotice>(),
            sp.GetRequiredService<SelfTestNotice>(),
            sp.GetRequiredService<RefusedNotice>(),
            sp.GetRequiredService<HistoryNotice>(),
            sp.GetRequiredService<SoundDeviceNotice>(),
            sp.GetRequiredService<SettingsNotice>(),
            sp.GetRequiredService<FellBehindNotice>(),
            sp.GetRequiredService<EventsLostNotice>()));

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
        var projection = app.Services.GetRequiredService<SessionProjection>();

        // A session that changes is a hook event that arrived, and an arriving event is proof
        // that Claude Code reaches this dashboard. So it clears a notice that said nothing was
        // reporting (the ruling of 2026-10-01): a notice that went on saying so above rows that
        // are updating would be contradicted by the screen it is on. The projection's collection
        // is touched on the UI thread only, which is the thread the notice is bound on. A start
        // has no sessions until a hook brings one: nothing is restored from the archive.
        //
        // Except a plugin that is turned off or removed (PR #66 review, M1): a session opened before
        // that keeps reporting until it restarts, so for those two the notice reads Claude Code's
        // settings again, quietly, and clears only if the plugin is enabled there. See HookNotice.
        var hookNotice = app.Services.GetRequiredService<HookNotice>();
        var hookCheck = app.Services.GetRequiredService<HookCheck>();
        hookNotice.ConfirmPluginWith(() => hookCheck.Read().PluginEnabled, app.Services.GetRequiredService<IClock>());
        projection.Sessions.CollectionChanged += (_, _) => hookNotice.EventArrived();

        // AFTER the sound engine's subscription, and the order is a correctness constraint: events
        // run their handlers in subscription order, and the board reads the nudge time the engine
        // has just set for this change. Resolved the other way round, /state would report the
        // schedule as it stood before the change (T1.46; StateBoard's remarks).
        _ = app.Services.GetRequiredService<StateBoard>();

        // The decisions recorder hears about drops and about /show (T1.37). Post-build wiring,
        // like the sound engine's subscription above: these callbacks fire on the writing thread
        // — ingress for the pipeline, the consumer for the archive, Kestrel for /show — and ride
        // the recorder's cross-thread queue.
        var decisions = app.Services.GetRequiredService<DecisionRecorder>();
        var wallClock = app.Services.GetRequiredService<Core.Ports.IClock>();

        // A shed event is noise refused at the door (reason "noise", its kind in the detail); a lost
        // one was the oldest, dropped at the hard limit (reason "pipeline", as before T1.58).
        // Identifiers only: the hook name, and a notification's type, which is wire vocabulary.
        app.Services.GetRequiredService<EventPipeline>().Dropped = (dropped, why) =>
            decisions.External(new Storage.Decision(
                wallClock.Now,
                dropped.SessionId.Value,
                Storage.DecisionKind.EventDropped,
                Reason: why == PipelineDrop.Shed ? "noise" : "pipeline",
                Detail: why == PipelineDrop.Shed ? EventPipeline.KindOf(dropped) : null));

        // Refused posts are rows with no event and no session (T1.61, the operator's comment on #74): at most
        // one a second, with the count (the ruling of 2026-10-04). Nothing from the post: it is not trusted.
        app.Services.GetRequiredService<HookHealth>().RefusedPost = (at, count) =>
            decisions.External(new Storage.Decision(
                at,
                null,
                Storage.DecisionKind.HookRefused,
                Detail: string.Create(CultureInfo.InvariantCulture, $"refused={count}")));

        app.Services.GetRequiredService<EventArchive>().Dropped = record =>
            decisions.External(new Storage.Decision(
                wallClock.Now,
                record.Event?.SessionId.Value,
                Storage.DecisionKind.EventDropped,
                Reason: "archive"));

        app.MapIngress(() =>
        {
            decisions.External(new Storage.Decision(
                wallClock.Now, null, Storage.DecisionKind.WindowSurfaced));
            onShow?.Invoke();
        });

        // The retired variable (T1.48). Said once, at Information, because an operator who set it on
        // T1.46's instructions deserves to know it now does nothing. The NAME is logged and never
        // the value: the value is a secret they chose, whatever it once protected.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(IngressToken.RetiredEnvironmentVariable)))
        {
            logger.Information(
                "{Variable} is set and is ignored. The dashboard makes a new token at every start and " +
                "hands it to the hook in {ListeningFile}; the variable can be removed.",
                IngressToken.RetiredEnvironmentVariable,
                resolved.ListeningFile);
        }

        if (ingress.CanReceiveHooks)
        {
            logger.Information(
                "Ingress will listen on http://127.0.0.1:{Port} (loopback only), with a new token for " +
                "this run in {ListeningFile}.",
                ingress.Port,
                resolved.ListeningFile);
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
            //
            // The line is the notice's own text (T1.57 review). It used to say "Port N is held by
            // another process", which turned false when a start stayed deaf on a port its own choice
            // had found free. The notice is made from the choice, so it says what is true: every port
            // tried was in use, or the pin is held, and what to do about it.
            logger.Error(
                "The dashboard did not bind a port, so it cannot receive hooks and every session will be " +
                "missing. It is starting anyway, with the same words in the window and the tray: {Notice}",
                ingress.Text);
        }

        return app;
    }

    /// <summary>
    /// Where the health board reads each count it does not keep itself (T1.65). One method, so a test
    /// holds the wiring: <c>notWritten</c> must be the writer's count since the start, not the store's
    /// <c>LostCount</c>, which starts again at each recovery.
    /// </summary>
    internal static HealthSources HealthSourcesFor(
        IngressStatus ingress,
        EventPipeline pipeline,
        EventArchive archive,
        HookHealth hookHealth,
        EventArchiveWriter writer,
        SqliteEventStore store,
        ISoundOutput output,
        ISoundModeReader modes) => new()
        {
            Version = StartupVersion.Value,
            Port = ingress.CanReceiveHooks ? ingress.Port : null,
            CanReceive = ingress.CanReceiveHooks,
            Shed = () => pipeline.ShedCount,
            Lost = () => pipeline.DroppedCount,
            ArchiveDropped = () => archive.DroppedCount,
            Refused = () => hookHealth.RefusedCount,
            NotWritten = () => writer.RefusedCount,
            DatabaseAvailable = () => store.Available,
            SoundOutput = () => output.HasOutput,
            Paused = () => modes.IsMonitoringPaused,
            MutedUntil = () => modes.AllMutedUntil,
        };

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

            // The operator's own floor (T1.37): logging.minimumLevel in settings.json, default
            // Information. Debug is what turns the decisions record on in the text log — the
            // database is the queryable record; the log is what someone tails — and a settings
            // key is what makes that possible without a rebuild. The file sink below takes the
            // same floor (T1.52, issue #68).
            .MinimumLevel.Is(logging.EffectiveMinimumLevel)

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
                // The file follows logging.minimumLevel, not a floor of its own (T1.52, issue #68).
                // It had a fixed Information here, so Debug in the settings changed nothing in the
                // file: a line must pass both floors, and the comments that promised the decisions
                // record at Debug were wrong. The default is still Information, so a file whose
                // operator never set the key carries what it always did.
                restrictedToMinimumLevel: logging.EffectiveMinimumLevel,
                shared: true,

                // This process can be killed rather than asked to stop — at logoff, or from Task
                // Manager — and neither runs the clean shutdown that would flush. Unflushed
                // diagnostics would be lost in exactly the cases they exist to explain.
                flushToDiskInterval: TimeSpan.FromSeconds(2));
        }

        var logger = configuration.CreateLogger();

        // The static logger is what the AppDomain handler flushes on the way down, when there is
        // no time to resolve anything from the container.
        Log.Logger = logger;
        return logger;
    }

    /// <summary>A message to put inside a sentence: trimmed, and without its own final full stop.</summary>
    private static string? Clause(string? message) => message?.Trim().TrimEnd('.');

    private static void ReportStartup(
        ILogger logger,
        DashboardPaths paths,
        SettingsLoadResult loaded,
        bool foldersReady,
        string? folderFailure,
        SettingsAtStart? settingsAtStart)
    {
        // The version first, before anything else says anything (PKG.2): it is what PKG.4's gate
        // and every later support question reads, and first is the one position nobody has to
        // search for.
        StartupVersion.Log(logger);

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
            // A value the load repaired is one Warning (T1.64): a port that is not a port, a negative
            // history.retentionDays. Until T1.64 the port's sentence was made and never logged.
            case SettingsLoadOutcome.Loaded when loaded.Problem is { } repaired:
                logger.Warning(
                    "Settings loaded from {File}, with a value repaired: {Repaired}",
                    paths.SettingsFile,
                    repaired);
                break;

            case SettingsLoadOutcome.Loaded:
                logger.Information("Settings loaded from {File}.", paths.SettingsFile);
                break;

            case SettingsLoadOutcome.Missing:
                logger.Information(
                    "No settings file at {File}; using defaults.",
                    paths.SettingsFile);
                break;

            // ONE Error line for the start (T1.56): what was wrong, and what this start did about it.
            // The problem is the parser's or Windows' message, which names a position or a file and
            // never a setting value. It ends in its own full stop, which is trimmed so the line does not
            // read ".." (T1.56's review).
            case SettingsLoadOutcome.Unreadable when settingsAtStart is { KeptAside: true, BackupFile: { } backup }:
                logger.Error(
                    "Settings file {File} could not be read: {Problem}. It was renamed to {Backup}, and a new " +
                    "settings file with the defaults was written. This start registers no plugin and leaves " +
                    "start with Windows as it found it.",
                    paths.SettingsFile,
                    Clause(loaded.Problem),
                    backup);
                break;

            case SettingsLoadOutcome.Unreadable when settingsAtStart is { SavesRefused: true }:
                logger.Error(
                    "Settings file {File} could not be read: {Problem}. It was left as it is: {KeepAside}. " +
                    "Using defaults, and no settings are saved until the dashboard restarts. This start registers " +
                    "no plugin and leaves start with Windows as it found it.",
                    paths.SettingsFile,
                    Clause(loaded.Problem),
                    Clause(settingsAtStart.KeepAsideProblem) ?? "it could not be opened");
                break;

            case SettingsLoadOutcome.Unreadable:
                logger.Error(
                    "Settings file {File} could not be read: {Problem}. Using defaults; the file is left as it is.",
                    paths.SettingsFile,
                    Clause(loaded.Problem));
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
