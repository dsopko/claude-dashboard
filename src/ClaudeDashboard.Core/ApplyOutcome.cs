namespace ClaudeDashboard.Core;

/// <summary>
/// What <see cref="SessionRegistry.Apply"/> did with an event.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Four of these are routine and one is an alarm, which is the whole reason the type
/// exists.</strong> While this was a <c>bool</c>, "the Registry declined it" carried both
/// <see cref="Stale"/> — which happens thousands of times a day and is the guard working — and
/// <see cref="Uncorrelated"/>, which should essentially never happen and means completions are
/// being rejected. One value cannot be logged at two levels: log it loudly and the file drowns
/// in normal traffic, log it quietly and the alarm never reaches the file at all. The second is
/// what was actually happening.
/// </para>
/// <para>
/// The stakes are not hypothetical. Whether Claude Code's <c>Stop</c> echoes the prompt's
/// <c>prompt_id</c> is unverified (T1.8). If it does not, <see cref="Uncorrelated"/> is not a
/// rare alarm but <em>every completion</em> — every session stuck in
/// <see cref="SessionState.Working"/> forever — and the difference between this enum and a
/// <c>bool</c> is the difference between one obvious warning and an empty log.
/// </para>
/// </remarks>
public enum ApplyOutcome
{
    /// <summary>The Registry changed: a session was created or updated.</summary>
    Applied = 1,

    /// <summary>
    /// The event does not apply in the session's current state — an acknowledgment of a session
    /// that is still working, a corroborating <c>agent_completed</c>, anything at all for an
    /// ended session, or an event naming no session. Routine.
    /// </summary>
    Ignored = 2,

    /// <summary>
    /// Older than the session's last-applied stamp, so it was dropped (TS §IV.1). Routine, and
    /// the guard doing its job.
    /// </summary>
    Stale = 3,

    /// <summary>
    /// Applying it would have left the session exactly as it is — re-applying the current state
    /// is a no-op (TS §IV.1). Routine: at-least-once delivery makes this common.
    /// </summary>
    Duplicate = 4,

    /// <summary>
    /// <strong>The alarming one.</strong> A <c>Stop</c> whose <c>prompt_id</c> does not match the
    /// turn the session is tracking, so it was rejected as belonging to an older turn.
    /// </summary>
    /// <remarks>
    /// Rare and suspicious in ones and twos — a genuinely delayed duplicate, which is exactly
    /// what the guard is for. Systematic if it is every completion, which would mean the
    /// correlation assumption itself is wrong rather than that anything is being redelivered.
    /// </remarks>
    Uncorrelated = 5,
}

/// <summary>Whether an outcome is worth an operator's attention.</summary>
public static class ApplyOutcomes
{
    /// <summary>True for the one outcome that should not be happening.</summary>
    public static bool IsAlarming(this ApplyOutcome outcome) => outcome == ApplyOutcome.Uncorrelated;

    /// <summary>True when the Registry changed.</summary>
    public static bool Changed(this ApplyOutcome outcome) => outcome == ApplyOutcome.Applied;
}
