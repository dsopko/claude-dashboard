using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// The only thing that touches <c>dashboard.db</c> (Impl Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// One reader, one connection, one thread. It drains <see cref="EventArchive"/> and writes each
/// event, so every disk wait happens here and nowhere near the event consumer.
/// </para>
/// <para>
/// <strong>Write-only. There is no read path, and Phase 1 must not grow one.</strong> Nothing
/// consumes this table until Phase 5, so a query surface added now would have no caller and no
/// test that could keep it honest — a shape this project has learned to recognise.
/// </para>
/// <para>
/// <strong>It logs the success as well as the failure.</strong> A recording feature that is silent
/// when it works and silent when it fails leaves the operator with a file they cannot reason
/// about, and leaves whoever debugs it unable to tell "never ran" from "ran and wrote nothing" —
/// the same absence that cost this project a diagnosis three times.
/// </para>
/// <para>
/// <strong>It writes this process's row in <c>runs</c> (T1.60, issue #78).</strong> The time is
/// taken when the host has started, which is after ingress has bound or failed, and before
/// <c>listening.txt</c> names this run, so no hook of this run is older than its start. The row is
/// written by this writer's loop, never by the consumer or the UI thread. A run so short that the
/// loop never ran writes it at the stop, before the drain. The stop time is set after the drain and
/// before the store closes; a kill, a crash or a host disposed without a stop leaves it empty.
/// </para>
/// </remarks>
public sealed class EventArchiveWriter
 : BackgroundService
{
    private readonly EventArchive _archive;
    private readonly IEventStore _store;
    private readonly ILogger _logger;
    private readonly RunStart? _run;
    private readonly IClock? _clock;
    private readonly CancellationToken _hostStarted;
    private readonly int _retentionDays;
    private readonly TaskCompletionSource<DateTimeOffset> _startedAt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration _startedRegistration;
    private int _startTried;
    private long _runId;
    private DateTimeOffset? _nextPruneAt;

    /// <summary>
    /// How often the history is pruned while the dashboard runs: once a day, after the prune at
    /// start (the operator's ruling of 2026-10-04 on #81: it often runs for weeks).
    /// </summary>
    public static readonly TimeSpan PruneEvery = TimeSpan.FromHours(24);

    /// <summary>
    /// How often the loop wakes when no record arrives, to see whether a prune is due. A check of the
    /// clock, not a timer: nothing is written unless the day is up.
    /// </summary>
    private static readonly TimeSpan Wake = TimeSpan.FromMinutes(1);

    /// <summary>Creates the writer, with no row in <c>runs</c>: for tests of the archive alone.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public EventArchiveWriter(EventArchive archive, IEventStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _archive = archive;
        _store = store;
        _logger = logger;
    }

    /// <summary>Creates the writer that also records this run (T1.60).</summary>
    /// <param name="archive">The channel it drains.</param>
    /// <param name="store">The file.</param>
    /// <param name="logger">Where the start and the stop are logged.</param>
    /// <param name="run">What the run row holds beside its times.</param>
    /// <param name="clock">The clock that stamps the start and the stop.</param>
    /// <param name="hostStarted">Cancelled when the host has started: ingress has bound or failed.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public EventArchiveWriter(
        EventArchive archive,
        IEventStore store,
        ILogger logger,
        RunStart run,
        IClock clock,
        CancellationToken hostStarted)
        : this(archive, store, logger)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(clock);

        _run = run;
        _clock = clock;
        _hostStarted = hostStarted;
    }

    /// <summary>Creates the writer that records this run and prunes the history (T1.60, T1.64).</summary>
    /// <param name="archive">The channel it drains.</param>
    /// <param name="store">The file.</param>
    /// <param name="logger">Where the start, the retention and the stop are logged.</param>
    /// <param name="run">What the run row holds beside its times.</param>
    /// <param name="clock">The clock that stamps the start and the stop, and counts the days.</param>
    /// <param name="retentionDays">The days of history to keep; 0 keeps everything (<c>history.retentionDays</c>).</param>
    /// <param name="hostStarted">Cancelled when the host has started: ingress has bound or failed.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public EventArchiveWriter(
        EventArchive archive,
        IEventStore store,
        ILogger logger,
        RunStart run,
        IClock clock,
        int retentionDays,
        CancellationToken hostStarted)
        : this(archive, store, logger, run, clock, hostStarted)
    {
        _retentionDays = retentionDays;
    }

    /// <summary>How many events this writer handed to the store. Diagnostic only.</summary>
    public long WrittenCount { get; private set; }

    /// <summary>How many the store refused. Diagnostic only.</summary>
    public long RefusedCount { get; private set; }

    /// <summary>Starts the writer, and says so before returning.</summary>
    /// <remarks>
    /// <strong>The line is here rather than at the top of <see cref="ExecuteAsync"/>, and that is
    /// a fix.</strong> <c>BackgroundService</c> does not run <c>ExecuteAsync</c>'s first statement
    /// before <c>StartAsync</c> returns — measured, after a test that asserted the line was there
    /// failed about one run in three. Worse, on a short-lived run the line could arrive after the
    /// stopped line or not at all, so "started" was reporting when the loop happened to be
    /// scheduled rather than whether the writer was running. Logged here it means what it says.
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run is not null)
        {
            // The retention in effect, BEFORE the loop starts, so before any prune line (T1.64).
            if (_retentionDays > 0)
            {
                _logger.Information(
                    "The history keeps {RetentionDays} days: older records are deleted at each start and once a day.",
                    _retentionDays);
            }
            else
            {
                _logger.Information("The history keeps everything: history.retentionDays is 0.");
            }

            // Synchronous, on the thread that starts the host: the time is taken before Start returns,
            // so before the announcement, and no hook this run accepts can be older than it.
            _startedRegistration = _hostStarted.Register(() => _startedAt.TrySetResult(_clock!.Now));
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);

        _logger.Information("Event archive writer started.");
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_run is not null)
            {
                // Until the host has started, or is stopping. Records queued meanwhile wait in the
                // channel, so the run row is the first thing this run writes.
                await Task.WhenAny(_startedAt.Task, Task.Delay(Timeout.Infinite, stoppingToken)).ConfigureAwait(false);
                WriteRunStart();
            }

            // On this loop only, so the prune is on the writer's thread: never the consumer, the UI or
            // a request thread, and the start never waits for it (T1.64).
            Task<bool>? waiting = null;

            while (true)
            {
                PruneIfDue();

                while (_archive.Reader.TryRead(out var record))
                {
                    // Before each record too: a day can end inside a batch of records.
                    PruneIfDue();
                    Write(record);
                }

                waiting ??= _archive.Reader.WaitToReadAsync(stoppingToken).AsTask();

                if (await Task.WhenAny(waiting, Task.Delay(Wake, stoppingToken)).ConfigureAwait(false) == waiting)
                {
                    var more = await waiting.ConfigureAwait(false);
                    waiting = null;

                    if (!more)
                    {
                        break;
                    }
                }

                stoppingToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Whatever is still queued is drained by StopAsync, not here.
        }
    }

    /// <summary>
    /// Stops the loop, then writes whatever is still queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The drain is here rather than at the end of <see cref="ExecuteAsync"/>, and that is
    /// a fix rather than a preference.</strong> It was at the end of the loop, and the result was a
    /// test that failed about one run in three: if cancellation arrived before the loop had
    /// consumed anything, <c>ExecuteAsync</c> could return without the queued events ever being
    /// read, and nothing was written at all. Measured, not reasoned — the instrumented run
    /// reported zero written, zero refused and no file on disk.
    /// </para>
    /// <para>
    /// Draining after the base class has stopped the loop makes it deterministic: the loop is over,
    /// nothing else reads the channel, and whatever is in it is written before this returns. The
    /// end of a run is the part nearest whatever the operator was doing when they quit, and losing
    /// it would be invisible — the rows would simply not be there.
    /// </para>
    /// <para>
    /// A hard kill still loses the queue. That is the residual, and it is written down rather than
    /// claimed closed.
    /// </para>
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // A run so short that the loop never ran: its start row, before the records it queued.
        WriteRunStart();

        while (_archive.Reader.TryRead(out var record))
        {
            Write(record);
        }

        _archive.ReportDrops();

        // AFTER the drain, and before the container closes the store (T1.60): a stop time means every
        // record this run queued before it stopped was written first.
        if (Volatile.Read(ref _runId) is var runId and > 0)
        {
            _store.StopRun(runId, _clock!.Now);
        }

        _startedRegistration.Dispose();

        _logger.Information(
            "Event archive writer stopped after {Written} written and {Refused} refused.",
            WrittenCount,
            RefusedCount);
    }

    private void Write(ArchiveRecord record)
    {
        if (_store.Append(record))
        {
            WrittenCount++;

            return;
        }

        // Counted, not logged. The store has already said once why it cannot write, and a line
        // per lost row would bury that one line under thousands.
        RefusedCount++;
    }

    /// <summary>
    /// Writes the run row once, if the host has started. Lost like any other record when the disk
    /// refuses, and counted by the store; never retried, because a late row would say the wrong time.
    /// </summary>
    /// <summary>
    /// Prunes when a prune is due: at the loop's first pass, then once each <see cref="PruneEvery"/>.
    /// A prune the store could not do now (closed, or failing) is tried again a minute later, with
    /// T1.54's retry. This process's run is kept, however long ago it started.
    /// </summary>
    private void PruneIfDue()
    {
        if (_run is null || _retentionDays <= 0)
        {
            return;
        }

        var now = _clock!.Now;

        if (_nextPruneAt is { } next && now < next)
        {
            return;
        }

        var runId = Volatile.Read(ref _runId);
        var result = _store.Prune(_retentionDays, now, runId > 0 ? runId : null);

        _nextPruneAt = now + (result is null ? SqliteEventStore.RetryAfter : PruneEvery);
    }

    private void WriteRunStart()
    {
        if (_run is null || !_startedAt.Task.IsCompletedSuccessfully || Interlocked.Exchange(ref _startTried, 1) == 1)
        {
            return;
        }

        if (_store.StartRun(_run, _startedAt.Task.Result) is { } id)
        {
            Volatile.Write(ref _runId, id);
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _startedRegistration.Dispose();
        base.Dispose();
    }
}
