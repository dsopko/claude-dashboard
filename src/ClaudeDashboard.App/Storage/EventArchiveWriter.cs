using ClaudeDashboard.Core.Events;
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
/// </remarks>
public sealed class EventArchiveWriter : BackgroundService
{
    private readonly EventArchive _archive;
    private readonly IEventStore _store;
    private readonly ILogger _logger;

    /// <summary>Creates the writer.</summary>
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
        await base.StartAsync(cancellationToken).ConfigureAwait(false);

        _logger.Information("Event archive writer started.");
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var inboundEvent in _archive.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                Write(inboundEvent);
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

        while (_archive.Reader.TryRead(out var inboundEvent))
        {
            Write(inboundEvent);
        }

        _archive.ReportDrops();

        _logger.Information(
            "Event archive writer stopped after {Written} written and {Refused} refused.",
            WrittenCount,
            RefusedCount);
    }

    private void Write(InboundEvent inboundEvent)
    {
        if (_store.Append(inboundEvent))
        {
            WrittenCount++;

            return;
        }

        // Counted, not logged. The store has already said once why it cannot write, and a line
        // per lost row would bury that one line under thousands.
        RefusedCount++;
    }
}
