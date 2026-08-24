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
}
