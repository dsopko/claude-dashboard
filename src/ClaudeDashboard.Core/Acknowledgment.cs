using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Core;

/// <summary>
/// What acknowledgment means (Design Document §4; TS §I.3, §IV.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One rule for all three tiers.</strong> Design Document §4 defines acknowledgment as a
/// single transition — Unread or Needs-You to Acked — reached three ways: automatically when the
/// operator submits the next prompt, manually from the row, and by inferred focus in a later
/// phase. Which states it applies to cannot depend on which tier asked, so the predicate lives
/// here and every tier reads it.
/// </para>
/// <para>
/// <strong>Why it was extracted.</strong> The Registry held two private copies of it — one for
/// the automatic tier and one for the manual one — identical, and free to drift. That is the same
/// shape as the drift <see cref="AttentionOrder"/> was created to end, where TS §IV.2 and §IV.3
/// disagreed about where <see cref="SessionState.Error"/> sat and the code inherited the
/// disagreement. Two tiers of one transition disagreeing about what can be acknowledged would be
/// worse than cosmetic: a row would offer an Ack that did nothing, or refuse one the next prompt
/// would have performed anyway.
/// </para>
/// <para>
/// <strong>It is also what the UI asks.</strong> A row's acknowledge affordance is offered
/// exactly where this says acknowledgment applies. The host does not restate the rule — it asks
/// for it, the way it asks <see cref="AttentionOrder.BandOf"/> which band a state displays in.
/// </para>
/// </remarks>
public static class Acknowledgment
{
    /// <summary>
    /// Whether an acknowledgment applies to <paramref name="state"/> — whether there is anything
    /// there to acknowledge.
    /// </summary>
    /// <remarks>
    /// TS §IV.1 draws <c>Ack</c> from <see cref="SessionState.Unread"/> and the two Needs-You
    /// states. <see cref="SessionState.Error"/> is included because TS §IV.2 bands Error with
    /// Needs You, and because an operator who has read a failed turn must be able to dismiss it —
    /// otherwise an Error row can only be cleared by submitting another prompt.
    /// <see cref="SessionState.Working"/> is excluded because nothing has finished, and
    /// <see cref="SessionState.Ended"/> because nothing is still competing for attention.
    /// </remarks>
    public static bool Applies(SessionState state) =>
        state is SessionState.Unread
              or SessionState.NeedsPermission
              or SessionState.NeedsQuestion
              or SessionState.Error;

    /// <summary>
    /// The event that acknowledges <paramref name="session"/> (TS §I.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An event, not a mutation.</strong> This builds something to publish; it changes
    /// nothing. TS §I.3 requires every ack source — the next prompt, a click on the row, Phase 3's
    /// focus inference — to travel the one path into the Registry, so the manual tier's job is to
    /// produce this and hand it to <c>IEventSink</c>, exactly as ingress does with a hook.
    /// </para>
    /// <para>
    /// The <c>cwd</c> is carried over from the session rather than left empty, so that an ack
    /// cannot move a session out of its group as a side effect of acknowledging it.
    /// </para>
    /// </remarks>
    /// <param name="session">The session being acknowledged.</param>
    /// <param name="now">When the operator acknowledged it.</param>
    /// <param name="source">Which tier raised it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public static Ack For(Session session, DateTimeOffset now, AckSource source)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new Ack
        {
            SessionId = session.Id,
            Timestamp = now,
            Cwd = session.Cwd,
            Source = source,
        };
    }
}
