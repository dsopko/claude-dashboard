using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// Where events are durably recorded (Impl Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a layer. <see cref="SqliteEventStore"/> is the only implementation that
/// ships; this exists so the archive's behaviour when the disk refuses can be tested at all. On a
/// machine where writing works, the degrade path never runs — the same problem the virtual-desktop
/// adapter has, and the same answer.
/// </para>
/// <para>
/// <strong>Write-only in Phase 1, deliberately.</strong> Nothing reads the database back until
/// Phase 5, so there is no query method here and adding one "while we are in the file" would be
/// building a surface with no caller and no test that could hold it honest.
/// </para>
/// <para>
/// This interface lives in App rather than Core because nothing in Core calls it. Core carries
/// <see cref="PayloadJson"/> only because the event does.
/// </para>
/// </remarks>
public interface IEventStore
{
    /// <summary>Appends one event. Never throws.</summary>
    /// <returns>
    /// <see langword="true"/> if the row was written. <see langword="false"/> if it was not — a
    /// dead disk is not a dead dashboard (TS §IV.7), so a failure here is a lost row and nothing
    /// more.
    /// </returns>
    bool Append(InboundEvent inboundEvent);

    /// <summary>
    /// Appends one record — an event, or none for a tick, plus its decisions — atomically
    /// (T1.37). Never throws.
    /// </summary>
    /// <remarks>
    /// The event row and its decision rows share one transaction, so a failure between them
    /// leaves neither: the record can never say an event happened while losing why, or the
    /// reverse.
    /// </remarks>
    /// <returns><see langword="true"/> if the record was written.</returns>
    bool Append(ArchiveRecord record);

    /// <summary>
    /// Writes this process's row in <c>runs</c>, with no stop time (T1.60). Never throws.
    /// </summary>
    /// <remarks>
    /// Lost like any other record when the disk refuses, and counted, but not retried: a start that
    /// is written a minute late would say the wrong time.
    /// </remarks>
    /// <returns>The row's id, for <see cref="StopRun"/>; null if it was not written.</returns>
    long? StartRun(RunStart run, DateTimeOffset startedAt);

    /// <summary>Sets the stop time on a row <see cref="StartRun"/> wrote (T1.60). Never throws.</summary>
    /// <returns><see langword="true"/> if the time was written.</returns>
    bool StopRun(long runId, DateTimeOffset stoppedAt);

    /// <summary>
    /// Deletes what is older than <paramref name="retentionDays"/> days before
    /// <paramref name="now"/>, in one transaction (T1.64). 0 keeps everything. Never throws.
    /// </summary>
    /// <param name="retentionDays">The days to keep; 0 keeps everything.</param>
    /// <param name="now">The instant the window is counted back from.</param>
    /// <param name="keepRunId">This process's run, which is kept however long ago it started; or null.</param>
    /// <returns>What was deleted; null if the store could not prune now (closed, or failing).</returns>
    PruneCounts? Prune(int retentionDays, DateTimeOffset now, long? keepRunId);
}

/// <summary>What one prune deleted, by table (T1.64).</summary>
/// <param name="Events">Rows deleted from <c>events</c>.</param>
/// <param name="Decisions">Rows deleted from <c>decisions</c>: those of the deleted events, and those with no event.</param>
/// <param name="Runs">Rows deleted from <c>runs</c>.</param>
public sealed record PruneCounts(long Events, long Decisions, long Runs)
{
    /// <summary>Nothing deleted.</summary>
    public static PruneCounts None { get; } = new(0, 0, 0);

    /// <summary>All the rows deleted.</summary>
    public long Total => Events + Decisions + Runs;
}
