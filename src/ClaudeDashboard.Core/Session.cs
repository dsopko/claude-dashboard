namespace ClaudeDashboard.Core;

/// <summary>
/// One Claude Code session as the dashboard knows it (TS §IV.1; Impl §2.1).
/// </summary>
/// <remarks>
/// Immutable. Applying an event produces a new <see cref="Session"/> rather than mutating
/// one, which is what lets the Registry stay single-writer and lock-free (Impl §2.2, §4).
/// This type carries no transition logic — the state machine is T1.2's.
///
/// TS §IV.1: "Every state carries: the latest exchange (prompt text; answer text once
/// known), entry timestamp (for age display and nudge timing), workspace, and derived group."
/// </remarks>
public sealed record Session
{
    private readonly string _cwd = string.Empty;
    private readonly Exchange _latest = null!;
    private readonly TransitionLog _transitions = TransitionLog.Empty;

    /// <summary>Claude Code's <c>session_id</c>; the Registry key (TS §II.3).</summary>
    public required SessionId Id { get; init; }

    /// <summary>Where this session sits in the attention model.</summary>
    public required SessionState State { get; init; }

    /// <summary>The latest exchange — the row's context line, and an expanded row's payload.</summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required Exchange Latest
    {
        get => _latest;
        init => _latest = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The session's workspace (<c>cwd</c>). May be empty when a payload omitted it; never
    /// null. It can change mid-session, so the group is re-derived rather than fixed at
    /// start (TS §II.3, §IV.3).
    /// </summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required string Cwd
    {
        get => _cwd;
        init => _cwd = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The key of the group this session rolls up into — derived, never operator-assigned
    /// (TS §IV.3). Deriving it is the group resolver's job (T1.4).
    /// </summary>
    public required GroupKey Group { get; init; }

    /// <summary>
    /// When the session entered <see cref="State"/>. Drives age display and nudge timing,
    /// and is the sort key for the Needs-You and Unread bands (TS §IV.1, §IV.2).
    /// </summary>
    public required DateTimeOffset EnteredAt { get; init; }

    /// <summary>
    /// When this session was last heard from, whether or not the state changed. The sort key
    /// for the Working and Quiet bands (TS §IV.2).
    /// </summary>
    public required DateTimeOffset LastActivity { get; init; }

    /// <summary>
    /// The failure that put the session in <see cref="SessionState.Error"/>, as the raw
    /// matcher value from <c>StopFailure</c> (<c>rate_limit</c>, <c>overloaded</c>, …), or
    /// null in every other state.
    /// </summary>
    /// <remarks>
    /// Kept as the raw string rather than an enum because Impl §9.1's matcher list is
    /// explicitly open-ended ("…"): a kind this build has never heard of must still reach the
    /// operator intact rather than collapsing to "Unknown". Callers that want the parsed form
    /// have <see cref="Events.StopFailureKinds.Parse"/>.
    /// </remarks>
    public string? ErrorKind { get; init; }

    /// <summary>The recent state history, oldest first. Never null; empty by default.</summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public TransitionLog Transitions
    {
        get => _transitions;
        init => _transitions = value ?? throw new ArgumentNullException(nameof(value));
    }
}
