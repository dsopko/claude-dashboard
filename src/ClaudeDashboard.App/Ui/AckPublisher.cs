using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// How a row asks for a session to be acknowledged (Design Document §4 tier 2; TS §I.3).
/// </summary>
/// <remarks>
/// An interface so a row depends on "somewhere to send an ack" rather than on the pipeline —
/// and so a test can watch what was sent without standing one up.
/// </remarks>
public interface IAckPublisher
{
    /// <summary>Asks for <paramref name="session"/> to be acknowledged.</summary>
    /// <returns>
    /// <see langword="true"/> if the request was accepted for delivery. Not whether the session
    /// was acknowledged — that is the Registry's answer and it arrives later, through the
    /// projection, like every other change.
    /// </returns>
    bool Acknowledge(Session session);
}

/// <summary>
/// Publishes a manual acknowledgment into the event channel (TS §I.3; Impl §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole architectural point of the manual tier.</strong> An ack is an event
/// through the channel, never a direct poke at the Registry from the UI. TS §I.3 requires every
/// ack source — the next prompt, this click, Phase 3's focus inference — to travel one path, and
/// T1.2 added the <see cref="Ack"/> variant so a synthetic ack rides the same channel as a hook.
/// The alternative is a second writer, and the Registry is lock-free on the assumption that there
/// is one.
/// </para>
/// <para>
/// <strong>The architecture will not catch a violation, which is why the rule matters.</strong>
/// T1.2b made the single-writer guard mutual exclusion rather than thread affinity, so a
/// <c>Registry.Apply</c> called straight from the dispatcher succeeds whenever the consumer
/// happens to be idle and throws only when the two overlap. It would pass a test run and fail in
/// front of the operator, on a busy afternoon, which is the worst way for it to fail. So this
/// type holds an <see cref="IEventSink"/> and nothing else; there is no Registry here to poke,
/// and a test asserts that nothing under <c>Ui</c> holds one either.
/// </para>
/// <para>
/// <strong>A refusal is logged, not thrown and not shown.</strong> The channel is bounded
/// (Impl §4), so a publish can be declined. The operator is told nothing because there is nothing
/// truthful to say on the row — the session is exactly as it was — but a declined ack is worth a
/// line in the file, since the alternative is a click that visibly did nothing and no record of
/// why.
/// </para>
/// </remarks>
public sealed class AckPublisher(IEventSink sink, IClock clock, ILogger logger) : IAckPublisher
{
    private readonly IEventSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>How many acks have been accepted for delivery. Diagnostic only.</summary>
    public long PublishedCount { get; private set; }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public bool Acknowledge(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // What an acknowledgment is belongs to Core, including which states one applies to. This
        // publishes what Core builds and decides nothing about it — a session the Registry will
        // decline is still published, and the Registry declines it, because "does this apply"
        // must have exactly one answer and it is not the host's.
        var accepted = _sink.TryPublish(Acknowledgment.For(session, _clock.Now, AckSource.Manual));

        if (!accepted)
        {
            _logger.Warning(
                "The acknowledgment of session {SessionId} was refused by the pipeline and the row is unchanged.",
                session.Id.Value);

            return false;
        }

        PublishedCount++;
        return true;
    }
}
