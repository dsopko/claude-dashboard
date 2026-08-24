using System.Globalization;
using ClaudeDashboard.App.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// <strong>For T1.9.</strong> No hosted service is registered yet, deliberately. The event
/// consumer and the nudge tick must run as <em>one</em> serialized loop, because the Registry
/// and the sound engine are lock-free on the assumption that a single thread touches them
/// (Impl §2.2, §4) — two independent <c>BackgroundService</c>s would race, and the reviewer
/// demonstrated that race against T1.5's engine. Impl §4's wording invites a separate
/// <c>PeriodicTimer</c> loop; it should not be a separate service. Register one hosted service
/// that owns both the channel read and the tick.
/// </para>
/// </remarks>
public static class AppHost
{
    /// <summary>Builds the host: settings, logging, and the exception policy.</summary>
    /// <param name="paths">Where the data folder is; defaults to <c>%LOCALAPPDATA%\ClaudeDashboard\</c>.</param>
    public static IHost Build(DashboardPaths? paths = null)
    {
        var resolved = paths ?? new DashboardPaths();
        var foldersReady = resolved.TryEnsureCreated(out var folderFailure);

        var settingsStore = new SettingsStore(resolved);
        var loaded = settingsStore.Load();

        var logger = CreateLogger(resolved, loaded.Settings.Logging, foldersReady);

        ReportStartup(logger, resolved, loaded, foldersReady, folderFailure);

        var builder = Host.CreateApplicationBuilder();

        // The host's own diagnostics would need Serilog.Extensions.Hosting to reach the rolling
        // file, and that package is not in Impl Appendix A. Clearing the default providers keeps
        // the console provider from writing into a process that has no console. See the status
        // report: this is a genuine gap in Appendix A, not a preference.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(resolved);
        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(loaded.Settings);
        builder.Services.AddSingleton<ILogger>(logger);
        builder.Services.AddSingleton<UnhandledExceptionPolicy>();

        return builder.Build();
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
        });
    }

    /// <summary>Builds the rolling-file logger described by Impl Part 8.</summary>
    private static Serilog.Core.Logger CreateLogger(DashboardPaths paths, LoggingSettings logging, bool foldersReady)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
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
