using System.Threading.Channels;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.App.Adapters;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>Why the pipeline did not deliver an event (T1.58).</summary>
public enum PipelineDrop
{
    /// <summary>Shed at the door: noise, refused because the queue was at its capacity.</summary>
    Shed = 1,

    /// <summary>Lost: the oldest event, dropped at the hard limit to keep memory bounded.</summary>
    Lost = 2,
}

/// <summary>
/// The bounded channel every event crosses on its way from ingress to the Registry
/// (Impl §4), and the <see cref="IEventSink"/> that writes to it.
/// </summary>
/// <remarks>
/// <para>
/// Many producers, one consumer. Kestrel request threads write here (T1.8), and so will
/// Phase 3's focus inference, because Impl §4 and TS §I.3 require every acknowledgment source
/// to travel this one path — a second route would reintroduce the multiple-writer problem the
/// whole design exists to avoid.
/// </para>
/// <para>
/// <strong>A full queue sheds only noise</strong> (T1.58, the operator's ruling of 2026-10-03 on
/// issue #3). Until T1.58 the channel dropped its oldest entry when full, and the oldest entry
/// could be the permission prompt the operator most needed to see. Now, below
/// <see cref="Capacity"/>, every event is written. At or above it, an event that cannot change what
/// the board shows (<see cref="PipelineNoise"/>) is refused at the door, the newest shed and not
/// the oldest, and every other event is still written. Nothing already queued is removed, so
/// order is kept.
/// </para>
/// <para>
/// <strong>A hard limit keeps memory bounded</strong> if state-changing events themselves flood, a
/// fault never seen: at <see cref="HardLimit"/> queued, the oldest event is dropped, as before
/// T1.58. Degrade, never crash.
/// </para>
/// <para>
/// <strong>Never silent, never noisy.</strong> Each shed or lost event goes to
/// <see cref="Dropped"/>, which records a decision row. The log writes one Warning when shedding
/// starts and one Information line when the queue is below the capacity again, with the counts:
/// a line for each event would bury the log in the one situation where it matters.
/// </para>
/// </remarks>
public sealed class EventPipeline
{
    /// <summary>
    /// How many events may queue before noise is shed.
    /// </summary>
    /// <remarks>
    /// Impl §4 asks for "a generous capacity". A thousand is roughly a minute of the busiest
    /// traffic this tool is designed for — fifteen sessions each producing an event a second —
    /// which means the queue only fills if the consumer has genuinely stopped, not because the
    /// operator was briefly busy.
    /// </remarks>
    public const int DefaultCapacity = 1024;

    /// <summary>The hard limit is this many times the capacity: 16,384 by default.</summary>
    public const int HardLimitFactor = 16;

    private readonly Channel<InboundEvent> _channel;
    private readonly ILogger _logger;
    private readonly IClock _clock;

    private long _shedCount;
    private long _lostCount;
    private long _shedInEpisode;
    private long _lostInEpisode;
    private long _lastShedTicks;
    private int _behind;

    /// <summary>Creates the pipeline and its channel.</summary>
    /// <param name="logger">Where shedding starts and ends are logged.</param>
    /// <param name="capacity">The queue length at which noise is shed.</param>
    /// <param name="hardLimit">The queue length at which the oldest event is lost; 16 times the capacity if null.</param>
    /// <param name="clock">Stamps the last shed, for the notice; the system clock if null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> is not positive, or <paramref name="hardLimit"/> is below it.
    /// </exception>
    public EventPipeline(ILogger logger, int capacity = DefaultCapacity, int? hardLimit = null, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        var limit = hardLimit ?? checked(capacity * HardLimitFactor);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, capacity, nameof(hardLimit));

        _logger = logger;
        _clock = clock ?? new SystemClock();
        Capacity = capacity;
        HardLimit = limit;

        var options = new BoundedChannelOptions(limit)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };

        _channel = Channel.CreateBounded<InboundEvent>(options, OnLost);
    }

    /// <summary>The queue length at which noise is shed.</summary>
    public int Capacity { get; }

    /// <summary>The queue length at which the oldest event is lost.</summary>
    public int HardLimit { get; }

    /// <summary>The read side. Only <see cref="EventConsumer"/> may read it.</summary>
    public ChannelReader<InboundEvent> Reader => _channel.Reader;

    /// <summary>How many events were lost at the hard limit since the start. Read from any thread.</summary>
    public long DroppedCount => Interlocked.Read(ref _lostCount);

    /// <summary>How many noise events were shed since the start. Read from any thread.</summary>
    public long ShedCount => Interlocked.Read(ref _shedCount);

    /// <summary>When noise was last shed, or null if never. Read from any thread, for the notice.</summary>
    public DateTimeOffset? LastShedAt =>
        Interlocked.Read(ref _lastShedTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Told when the pipeline sheds or loses an event (T1.37, T1.58). Set once at composition; the
    /// decisions recorder is the one listener. Invoked on whichever thread was writing, so a
    /// listener must be thread-safe — the recorder's External is.
    /// </summary>
    public Action<InboundEvent, PipelineDrop>? Dropped { get; set; }

    /// <summary>The sink ingress and Phase 3 publish through.</summary>
    public IEventSink Sink => new ChannelEventSink(this);

    /// <summary>
    /// The kind of a shed event, for the decision row's detail: <c>kind=PostToolBatch</c>, or
    /// <c>kind=Notification type=idle_prompt</c>. Identifiers only: the hook name and the
    /// notification type are wire vocabulary.
    /// </summary>
    public static string KindOf(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        return inboundEvent is Notification notification
            ? $"kind={notification.HookEventName} type={notification.NotificationType}"
            : $"kind={inboundEvent.HookEventName}";
    }

    /// <summary>
    /// Called by the consumer after it drains what it could read. When the queue is below the
    /// capacity again after shedding or losing events, writes the one recovery line.
    /// </summary>
    public void NoteDrained()
    {
        if (Volatile.Read(ref _behind) == 0 || _channel.Reader.Count >= Capacity)
        {
            return;
        }

        if (Interlocked.Exchange(ref _behind, 0) == 0)
        {
            return;
        }

        _logger.Information(
            "The event pipeline caught up: fewer than {Capacity} events are queued again. While it was " +
            "behind it shed {Shed} repeated events and lost {Lost}.",
            Capacity,
            Interlocked.Exchange(ref _shedInEpisode, 0),
            Interlocked.Exchange(ref _lostInEpisode, 0));
    }

    /// <summary>Writes, or sheds noise at the capacity. Never blocks.</summary>
    private bool Publish(InboundEvent inboundEvent)
    {
        if (PipelineNoise.Is(inboundEvent) && _channel.Reader.Count >= Capacity)
        {
            Interlocked.Increment(ref _shedCount);
            Interlocked.Increment(ref _shedInEpisode);
            Interlocked.Exchange(ref _lastShedTicks, _clock.Now.UtcTicks);
            Behind();
            Dropped?.Invoke(inboundEvent, PipelineDrop.Shed);

            // Accepted, in the port's terms: the event is accounted for here, so the caller writes
            // no line for each. /hook answers 200 either way.
            return true;
        }

        return _channel.Writer.TryWrite(inboundEvent);
    }

    private void OnLost(InboundEvent lost)
    {
        Interlocked.Increment(ref _lostCount);
        Interlocked.Increment(ref _lostInEpisode);
        Behind();
        Dropped?.Invoke(lost, PipelineDrop.Lost);
    }

    /// <summary>The first shed or loss of an episode writes the one Warning.</summary>
    private void Behind()
    {
        if (Interlocked.Exchange(ref _behind, 1) == 1)
        {
            return;
        }

        _logger.Warning(
            "The event pipeline is behind: {Capacity} or more events are queued, so it skips repeated " +
            "events (tool batches, and notifications that change nothing) until it catches up. Every other " +
            "event is still delivered. Each skipped event is recorded; the log says when it catches up.",
            Capacity);
    }

    /// <summary>Writes to the channel without ever blocking the caller (Impl §4).</summary>
    private sealed class ChannelEventSink(EventPipeline pipeline) : IEventSink
    {
        /// <inheritdoc/>
        public bool TryPublish(InboundEvent inboundEvent)
        {
            if (inboundEvent is null)
            {
                // The port's contract is that this never throws (T1.6).
                pipeline._logger.Warning("Something published a null event; ignoring it.");
                return false;
            }

            // Never blocks. Below the hard limit nothing is dropped; at it the channel makes room by
            // dropping its oldest entry, which OnLost records. A false here means the channel has been
            // completed, which only happens as the host shuts down.
            return pipeline.Publish(inboundEvent);
        }
    }
}

/// <summary>
/// The events a full queue may shed: those that cannot change what the board shows (T1.58).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Decided by kind, at the door, without reading the Registry</strong>, because the door
/// is a request thread and the Registry has one writer. A <see cref="PostToolBatch"/>, and a
/// <see cref="Notification"/> whose kind moves no state, are noise. The notification kinds come
/// from <see cref="SessionRegistry.TargetOf(NotificationKind)"/>, so the list cannot drift from the
/// rule that applies them.
/// </para>
/// <para>
/// <strong>The safe direction.</strong> A shed <see cref="PostToolBatch"/> that would have resumed a
/// blocked session leaves the row red until the session's next event. A row that is too loud for a
/// moment is the safe failure; a row that is silent while Claude waits is the one this removes.
/// Every event the operator or the window publishes (<see cref="Ack"/>, <see cref="SoundCommand"/>,
/// <see cref="RostersChanged"/>) is never noise.
/// </para>
/// </remarks>
public static class PipelineNoise
{
    /// <summary>Whether <paramref name="inboundEvent"/> may be shed when the queue is full.</summary>
    public static bool Is(InboundEvent inboundEvent) => inboundEvent switch
    {
        PostToolBatch => true,
        Notification notification => SessionRegistry.TargetOf(notification.Kind) is null,
        _ => false,
    };
}
