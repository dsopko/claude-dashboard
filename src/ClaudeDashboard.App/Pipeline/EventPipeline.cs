using System.Threading.Channels;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

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
/// <strong>Bounded, drop-oldest.</strong> Impl §4 offers drop-oldest or block-writer and
/// settles it with a criterion rather than a preference: a burst of fifteen simultaneous events
/// must not stall Kestrel. Block-writer stalls by construction, so drop-oldest it is. It also
/// suits what this program is: a dashboard's job is to show what is true <em>now</em>, so under
/// pressure the newest events are the ones worth keeping, and a stale backlog is the thing to
/// shed. The capacity is generous enough that dropping is a pathology rather than a policy —
/// fifteen concurrent events use one and a half percent of it.
/// </para>
/// <para>
/// <strong>A drop is never silent.</strong> Drop-oldest means <c>TryWrite</c> always succeeds,
/// so T1.8's refusal path — which logs — cannot fire, and the loss would otherwise be invisible.
/// The channel is therefore constructed with a dropped-item callback that logs each casualty.
/// </para>
/// </remarks>
public sealed class EventPipeline
{
    /// <summary>
    /// How many events may queue before the oldest is dropped.
    /// </summary>
    /// <remarks>
    /// Impl §4 asks for "a generous capacity". A thousand is roughly a minute of the busiest
    /// traffic this tool is designed for — fifteen sessions each producing an event a second —
    /// which means the queue only fills if the consumer has genuinely stopped, not because the
    /// operator was briefly busy.
    /// </remarks>
    public const int DefaultCapacity = 1024;

    private readonly Channel<InboundEvent> _channel;
    private readonly ILogger _logger;

    /// <summary>Creates the pipeline and its channel.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public EventPipeline(ILogger logger, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _logger = logger;

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };

        _channel = Channel.CreateBounded<InboundEvent>(options, OnDropped);
    }

    /// <summary>The read side. Only <see cref="EventConsumer"/> may read it.</summary>
    public ChannelReader<InboundEvent> Reader => _channel.Reader;

    /// <summary>How many events the channel has discarded to make room. Diagnostic only.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>The sink ingress and Phase 3 publish through.</summary>
    public IEventSink Sink => new ChannelEventSink(_channel.Writer, _logger);

    private void OnDropped(InboundEvent dropped)
    {
        DroppedCount++;

        _logger.Warning(
            "The event pipeline is full; discarded the oldest queued event {HookEventName} for session " +
            "{SessionId}. {DroppedCount} dropped so far. The consumer is not keeping up.",
            dropped.HookEventName,
            dropped.SessionId.Value,
            DroppedCount);
    }

    /// <summary>Writes to the channel without ever blocking the caller (Impl §4).</summary>
    private sealed class ChannelEventSink(ChannelWriter<InboundEvent> writer, ILogger logger) : IEventSink
    {
        /// <inheritdoc/>
        public bool TryPublish(InboundEvent inboundEvent)
        {
            if (inboundEvent is null)
            {
                // The port's contract is that this never throws (T1.6).
                logger.Warning("Something published a null event; ignoring it.");
                return false;
            }

            // Never blocks, and under drop-oldest never refuses — the channel makes room by
            // discarding its oldest entry, which OnDropped logs. A false here means the channel
            // has been completed, which only happens as the host shuts down.
            return writer.TryWrite(inboundEvent);
        }
    }
}
