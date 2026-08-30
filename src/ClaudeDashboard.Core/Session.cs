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
    private readonly SessionId _id;
    private readonly GroupKey _group;

    /// <summary>Claude Code's <c>session_id</c>; the Registry key (TS §II.3).</summary>
    /// <exception cref="ArgumentException">Set to a <c>default</c> id, which names no session.</exception>
    public required SessionId Id
    {
        get => _id;
        init => _id = value.IsEmpty
            ? throw new ArgumentException("A session must have an id.", nameof(value))
            : value;
    }

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
    /// The key of the group this session's <em>observable reality</em> puts it in — its
    /// workspace, or itself when no workspace is known. Derived, never operator-assigned
    /// (TS §IV.3). Deriving it is the group resolver's job (T1.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The name says what the value is rather than what a reader might hope it is.</strong>
    /// This was called <c>Group</c>, which promised "the group" — and a name that promises more
    /// than the thing behind it is how a reader ends up with the wrong value and no error. Issue
    /// #16 adds a second and truer notion above this one, so the promise is being narrowed to the
    /// truth before anything can be written against the wider reading.
    /// </para>
    /// <para>
    /// This is a rename and nothing else: the value, the guard and every use of it are unchanged.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Set to a <c>default</c> key, which names no group.</exception>
    public required GroupKey WorkspaceGroup
    {
        get => _group;
        init => _group = value.IsEmpty
            ? throw new ArgumentException("A session must belong to a group.", nameof(value))
            : value;
    }

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

    /// <summary>
    /// The session's title as Claude Code last reported it — a name the operator set with
    /// <c>--name</c> or <c>/rename</c>, or one Claude Code generated — or null if none has ever
    /// arrived (issue #18).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Latched, not read per event.</strong> Only 72 of 1,210 archived payloads carry a
    /// title and <c>Stop</c> never does, so a row that has just finished has no title on the
    /// event that finished it. The latch rule lives in <see cref="SessionRegistry"/>.
    /// </para>
    /// <para>
    /// <strong>Verbatim, and it can carry the operator's words.</strong> A session the operator
    /// did not name gets a title written by a background model call summarising their first
    /// prompt, so this is prose rather than an identifier: rendered and escaped, never logged and
    /// never interpreted (TS §II.5). Folding and truncation for display are the view model's, and
    /// this value is untouched by them.
    /// </para>
    /// <para>
    /// Null and empty mean the same thing to every reader — no title has been seen — and the
    /// latch never writes empty, so a caller need not tell them apart.
    /// </para>
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>The recent state history, oldest first. Never null; empty by default.</summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public TransitionLog Transitions
    {
        get => _transitions;
        init => _transitions = value ?? throw new ArgumentNullException(nameof(value));
    }
}
