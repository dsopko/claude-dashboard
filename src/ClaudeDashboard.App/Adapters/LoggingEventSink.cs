using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// A placeholder <see cref="IEventSink"/> that logs what ingress hands it and keeps nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>T1.9 replaces this.</strong> The real sink writes to the bounded
/// <c>Channel&lt;InboundEvent&gt;</c> that the single event consumer reads (Impl §4). Until
/// that exists there is still a composition to satisfy — ingress cannot be built without
/// something to publish to — and the honest placeholder is one that accepts, records that it
/// accepted, and drops.
/// </para>
/// <para>
/// It always returns <see langword="true"/>: nothing here can be full. That keeps the drop
/// path exercised only where it is real, in T1.9's bounded channel, rather than producing
/// spurious "pipeline full" log lines from a sink that has no capacity to speak of.
/// </para>
/// </remarks>
public sealed class LoggingEventSink(ILogger logger) : IEventSink
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>How many events have been accepted and dropped. Diagnostic only.</summary>
    public int AcceptedCount { get; private set; }

    /// <inheritdoc/>
    public bool TryPublish(InboundEvent inboundEvent)
    {
        if (inboundEvent is null)
        {
            // The port's contract is that this never throws (T1.6), and a real ChannelWriter
            // would not. Refusing is the only way to say "not accepted" without breaking it.
            _logger.Warning("An ingress path published a null event; ignoring it.");
            return false;
        }

        AcceptedCount++;
        _logger.Information(
            "Event {HookEventName} for session {SessionId} received. No pipeline is running yet (T1.9).",
            inboundEvent.HookEventName,
            inboundEvent.SessionId.Value);

        return true;
    }
}
