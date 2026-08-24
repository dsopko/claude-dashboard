using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// How ingress hands a normalized event to the pipeline (Impl §1.3, §4). Implemented in App
/// over the bounded <c>Channel&lt;InboundEvent&gt;</c> the event consumer reads (T1.9);
/// implemented in Tests by a sink that records what it was handed.
/// </summary>
/// <remarks>
/// Every producer writes through this one seam: Kestrel request threads handling hooks, and
/// the synthetic acknowledgment events focus inference raises in Phase 3, which enter the
/// <em>same</em> channel so that all ack sources travel one path (Impl §4; TS §I.3).
/// </remarks>
public interface IEventSink
{
    /// <summary>
    /// Hands <paramref name="inboundEvent"/> to the pipeline if it can be accepted right now.
    /// </summary>
    /// <param name="inboundEvent">The normalized event.</param>
    /// <returns>
    /// <see langword="true"/> if the event was accepted; <see langword="false"/> if the
    /// pipeline could not take it. A <see langword="false"/> is a dropped event, worth a log
    /// line — it is never worth failing the caller over, and ingress still answers
    /// <c>200</c> either way, because the dashboard is a pure observer that must not affect
    /// the Claude turn that produced the event (Impl §3.3).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately synchronous, non-blocking, and refusable.</strong> Impl §4 puts a
    /// <em>bounded</em> channel behind this port, and Execution Plan Part 1 forbids blocking
    /// the request thread; an awaitable publish would invite exactly the stall that bounding
    /// the channel exists to prevent — a burst of simultaneous events backing up into
    /// Kestrel. A <c>bool</c> return maps directly onto <c>ChannelWriter.TryWrite</c>, cannot
    /// block, and makes the drop visible instead of silent. If T1.9 configures the channel to
    /// drop its oldest entry when full, this simply always returns <see langword="true"/>;
    /// the contract still holds.
    /// </para>
    /// <para><strong>Never throws.</strong> A full or completed pipeline is a
    /// <see langword="false"/>, not an exception.</para>
    /// </remarks>
    bool TryPublish(InboundEvent inboundEvent);
}
