using System.Threading.Channels;
using ClaudeDashboard.Core.Events;
using Serilog;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// The bounded channel between the event consumer and the disk (Impl §4, Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists so the consumer never waits on a disk.</strong> The consumer is the single
/// writer of the Registry and of the sound engine; if it blocked on a write, the dashboard would
/// stop seeing events for as long as the disk was slow, and a stalled disk would look exactly like
/// a dead dashboard. It therefore hands each event over with a <c>TryWrite</c> that cannot block
/// and returns. A separate service owns the connection and is the only thing that touches the
/// file, so disk trouble shows up here as a full channel and never as a stalled consumer.
/// </para>
/// <para>
/// <strong>Drop-oldest, matching the event pipeline, and the reason is not only consistency.</strong>
/// Either policy leaves a gap when the queue fills. Drop-oldest leaves a contiguous <em>recent</em>
/// window; drop-newest leaves a contiguous <em>ancient</em> one. For a dashboard's history, recent
/// is what anyone would search, so the gap goes at the far end.
/// </para>
/// <para>
/// <strong>The gap is never silent, which is the whole point of counting.</strong> A history file
/// with holes that says nothing about them is worse than no history: it invites conclusions from
/// an absence that was never a fact about the sessions. Every drop is counted, and the count is
/// reported at shutdown if it is not zero.
/// </para>
/// </remarks>
public sealed class EventArchive
{
    /// <summary>
    /// How many events may wait for the disk before the oldest is dropped.
    /// </summary>
    /// <remarks>
    /// The same capacity as the event pipeline, for the same reason: roughly a minute of the
    /// busiest traffic this tool is designed for. Filling it means the disk has genuinely stopped,
    /// not that the operator was briefly busy.
    /// </remarks>
    public const int DefaultCapacity = 1024;

    private readonly Channel<ArchiveRecord> _channel;
    private readonly ILogger _logger;

    /// <summary>Creates the archive channel.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public EventArchive(ILogger logger, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _logger = logger;

        _channel = Channel.CreateBounded<ArchiveRecord>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            OnDropped);
    }

    /// <summary>The read side. Only <see cref="EventArchiveWriter"/> may read it.</summary>
    public ChannelReader<ArchiveRecord> Reader => _channel.Reader;

    /// <summary>
    /// Told when the channel drops a record (T1.37). Set once at composition; invoked on the
    /// consumer thread, whose TryWrite is what overflows this channel.
    /// </summary>
    public Action<ArchiveRecord>? Dropped { get; set; }

    /// <summary>How many events were dropped because the disk could not keep up.</summary>
    /// <remarks>
    /// Public because a future reader needs to find it. This is the number that says whether the
    /// history has holes in it.
    /// </remarks>
    public long DroppedCount { get; private set; }

    /// <summary>
    /// The archive backlog (T1.66): told the channel's count at each hand-off, on the consumer thread.
    /// Set once at composition.
    /// </summary>
    public Pipeline.Timing? Backlog { get; set; }

    /// <summary>How many events were handed over for writing. Diagnostic only.</summary>
    public long OfferedCount { get; private set; }

    /// <summary>
    /// Hands a record — an event with its decisions, or a tick's decisions alone — to the
    /// writer. Never blocks, never throws, and never waits on the disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The record travels whole (T1.37).</strong> The event's row id does not exist
    /// until the archive thread inserts it, so the decisions ride WITH the event and take the id
    /// inside the writer's one transaction. One non-blocking hand-off per event, or two when
    /// decisions born on other threads are pending and leave as their own record first.
    /// </para>
    /// <para>
    /// The old rule "an empty payload is not archived" is gone with the old shape: the Ack event
    /// carries no payload and IS archived now, with its decision rows — its absence was exactly
    /// the hole issue #48's investigation fell into. What still does not land is a record with
    /// nothing in it at all.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> if the record was taken for writing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public bool TryArchive(ArchiveRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.IsEmpty)
        {
            return false;
        }

        OfferedCount++;

        var written = _channel.Writer.TryWrite(record);

        // A slow disk shows here first: records drop at the capacity (1,024), and this warns at 512.
        Backlog?.Record(_channel.Reader.Count);

        return written;
    }

    /// <summary>Closes the channel so the writer drains and stops.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Says once, at shutdown, whether the history has holes in it.</summary>
    /// <remarks>
    /// Silent when nothing was dropped, because the ordinary case does not need a line. Loud when
    /// something was, because that is a fact about the file the operator would otherwise never
    /// learn.
    /// </remarks>
    public void ReportDrops()
    {
        if (DroppedCount == 0)
        {
            return;
        }

        _logger.Warning(
            "{DroppedCount} of {OfferedCount} events were never written to the event log because " +
            "the disk could not keep up. The recorded history has gaps, and they are the oldest " +
            "events of each burst rather than the most recent.",
            DroppedCount,
            OfferedCount);
    }

    private void OnDropped(ArchiveRecord record)
    {
        DroppedCount++;
        Dropped?.Invoke(record);

        if (record.Event is not { } dropped)
        {
            _logger.Debug(
                "The event archive is full; discarded {DecisionCount} tick decision(s) unwritten.",
                record.Decisions.Count);

            return;
        }

        // The hook's name and the session are all this line needs.
        _logger.Debug(
            "The event archive is full; discarded {HookEventName} for session {SessionId} unwritten.",
            dropped.HookEventName,
            dropped.SessionId.Value);
    }
}
