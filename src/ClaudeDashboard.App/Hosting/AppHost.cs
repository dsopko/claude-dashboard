using System.Globalization;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
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
    public static WebApplication Build(DashboardPaths? paths = null, Action? onShow = null)
    {
        var resolved = paths ?? new DashboardPaths();
        var foldersReady = resolved.TryEnsureCreated(out var folderFailure);

        var settingsStore = new SettingsStore(resolved);
        var loaded = settingsStore.Load();

        var logger = CreateLogger(resolved, loaded.Settings.Logging, foldersReady);

        ReportStartup(logger, resolved, loaded, foldersReady, folderFailure);

        var builder = WebApplication.CreateSlimBuilder();

        // Route the framework's own diagnostics into the rolling file. Without this bridge,
        // Kestrel's bind failure on the fixed port — the likeliest startup failure this app has
        // — would reach no sink at all, and the operator would see a dashboard that starts,
        // says so, and then never receives a hook, with nothing anywhere explaining why.
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: false);

        // Impl §3.1: loopback only, fixed default port, configurable. Loopback is the whole of
        // the network boundary — nothing off-machine may post events (TS §II.5).
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenLocalhost(loaded.Settings.Port);
            kestrel.AddServerHeader = false;
        });

        builder.Services.AddSingleton(resolved);
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
        builder.Services.AddSingleton<MotionPolicy>();
        builder.Services.AddSingleton<UiTick>();
        builder.Services.AddSingleton<IUiTick>(sp => sp.GetRequiredService<UiTick>());
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<ISoundModeReader>(sp => sp.GetRequiredService<SoundPolicyEngine>());
        builder.Services.AddSingleton<TrayViewModel>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventConsumer>());
        builder.Services.AddSingleton<EventConsumer>();

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
        registry.SessionChanged += (_, e) => sound.OnSessionChanged(e.Session);
        _ = app.Services.GetRequiredService<SessionProjection>();
        app.MapIngress(onShow);

        logger.Information(
            "Ingress will listen on http://127.0.0.1:{Port} (loopback only). Token check {TokenState}.",
            loaded.Settings.Port,
            app.Services.GetRequiredService<IngressToken>().IsConfigured ? "enabled" : "disabled");

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
    public static IDisposable WireProcessExceptionHandlers(UnhandledExceptionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
        {
            policy.HandleDomainException(e.ExceptionObject as Exception, e.IsTerminating);

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
    private static Serilog.Core.Logger CreateLogger(DashboardPaths paths, LoggingSettings logging, bool foldersReady)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()

            // The framework logs four lines per request at Information — request starting,
            // endpoint executing, status code, request finished. Across fifteen busy sessions
            // that buries the dashboard's own diagnostics in its own traffic, in a file kept for
            // a fortnight. Framework warnings and errors still reach the file, which is what the
            // bridge is for: a Kestrel bind failure on the fixed port is the likeliest startup
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
        logger.Information(
            "Claude Dashboard starting. Data folder {Root}; logging to {LogFolder}.",
            paths.Root,
            paths.LogFolder);

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
