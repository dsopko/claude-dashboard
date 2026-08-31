namespace ClaudeDashboard.Core;

/// <summary>
/// The state of a session in the attention model (TS §IV.1; Impl §2.1).
/// </summary>
/// <remarks>
/// TS §IV.1 writes the two "needs you" states hierarchically as
/// <c>NeedsYou.Question</c> and <c>NeedsYou.Permission</c>; Impl §2.1 flattens them to
/// <see cref="NeedsQuestion"/> and <see cref="NeedsPermission"/>. The flat form is used
/// here, per Impl §2.1 — the two spellings denote the same states.
///
/// The declaration order carries no meaning. Display banding is TS §IV.2's ordering
/// (T1.3) and group roll-up is TS §IV.3's severity ranking (see <see cref="Group"/>);
/// neither is the enum's ordinal, so nothing here forecloses either.
///
/// The explicit numeric values are part of the persisted representation (Impl §8) and
/// must not be renumbered once history exists on disk.
/// </remarks>
public enum SessionState
{
    /// <summary>Claude is working the turn. Entered on <c>UserPromptSubmit</c>.</summary>
    Working = 1,

    /// <summary>Blocked on the operator approving a permission prompt.</summary>
    NeedsPermission = 2,

    /// <summary>Blocked on the operator answering a question.</summary>
    NeedsQuestion = 3,

    /// <summary>The turn died on an error; <see cref="Session.ErrorKind"/> records which.</summary>
    Error = 4,

    /// <summary>Finished, but the operator has not seen the result yet.</summary>
    Unread = 5,

    /// <summary>The result has been acknowledged — seen, and no longer competing for attention.</summary>
    Acked = 6,

    /// <summary>The session terminated; scheduled for removal.</summary>
    Ended = 7,

    /// <summary>
    /// Nothing has been heard from this session for the silence threshold while it was
    /// <see cref="Working"/>. The badge reads INTERRUPTED because pressing Escape is
    /// overwhelmingly the cause and the operator asked for that word — but what the dashboard
    /// observed is silence, and a long tool call looks the same.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Elapsed silence is the only signal there is</strong> (issue #28). Claude Code posts
    /// nothing when a turn is interrupted: confirmed against its published documentation on
    /// 2026-08-31 — <c>Stop</c> carries no <c>stop_reason</c>, <c>StopFailure</c> is API errors
    /// only, and none of the twelve <c>Notification</c> matchers concerns interruption. The
    /// transcript records it and is rejected: TS §II.3 says it is written asynchronously and lags
    /// the live turn, and the design deliberately does not depend on it.
    /// </para>
    /// <para>
    /// <strong>Entered from <see cref="Working"/> and from nowhere else.</strong> Never from
    /// <see cref="NeedsPermission"/>, <see cref="NeedsQuestion"/> or <see cref="Error"/> — those
    /// are sessions asking for the operator, and timing one out would hide the request. Design §4:
    /// an absence of activity may de-escalate a session and must never escalate one.
    /// </para>
    /// <para>
    /// <strong>Not sticky and not terminal.</strong> The next event of any kind puts the session
    /// where that event says it belongs, so a session marked wrongly corrects itself the moment it
    /// speaks.
    /// </para>
    /// </remarks>
    Interrupted = 8,
}
