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
    /// When an event for this session last <strong>arrived</strong>, whatever the Registry then
    /// decided to do with it (issue #28).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THIS IS NOT <see cref="LastActivity"/>, AND THE DIFFERENCE IS THE WHOLE REASON IT
    /// EXISTS.</strong> <c>LastActivity</c> is written only by a transition that changes
    /// something, so an event the Registry <em>ignores</em> leaves it untouched — and a
    /// <c>PostToolBatch</c> on a session already <see cref="SessionState.Working"/> is ignored, at
    /// 799 of the 1,210 payloads in the archive. Measured, not read: a prompt at 09:00 followed by
    /// a tool batch and an ignored notification over the next hour left <c>LastActivity</c> at
    /// 09:00.
    /// </para>
    /// <para>
    /// <strong>So a silence timeout built on <c>LastActivity</c> would grey out every long
    /// turn</strong>, not merely the long tool call the design anticipated. A session emitting a
    /// batch every four seconds for eleven minutes would be marked
    /// <see cref="SessionState.Interrupted"/> while visibly working — the expensive false
    /// positive, arriving on ordinary work.
    /// </para>
    /// <para>
    /// <strong>Widening <c>LastActivity</c> instead was rejected.</strong> It is the sort key for
    /// the Working and Quiet bands, and its refusal to advance on a redelivery is deliberate and
    /// pinned by test — widening it would trade a silent detection bug for a silent display bug.
    /// </para>
    /// <para>
    /// <strong>Advanced on every non-stale event for an existing session, whatever the outcome</strong>
    /// — applied, ignored or duplicate alike. Not advanced by a stale event, which the Registry
    /// drops before touching anything, and <strong>not by a synthetic <c>Ack</c></strong>: that is
    /// the dashboard talking to itself, and a field named for hearing from a session must not move
    /// when the session said nothing. That exclusion cannot change any outcome today — an
    /// acknowledged session is not <see cref="SessionState.Working"/> — and it is there so the
    /// name stays true for whoever reads it next.
    /// </para>
    /// <para>
    /// <strong>Read by the silence sweep, and by the clock of the row it sweeps.</strong> The sweep
    /// judges silence against it. Since T1.47 (the operator's ruling of 2026-09-29) the sweep also
    /// copies it into <see cref="ClockAnchor"/>, so an Interrupted row counts from the last event
    /// heard rather than from the sweep, which came a threshold later. The state endpoint reports
    /// it as a value. <strong>It is not an ordering key and must not become one</strong>: ordering
    /// belongs to <c>LastActivity</c>, whose meaning is narrower on purpose, and to
    /// <see cref="EnteredAt"/>.
    /// </para>
    /// </remarks>
    public required DateTimeOffset LastHeardAt { get; init; }

    /// <summary>
    /// The moment that mattered in the current state, for the row's clock only, or null where the
    /// row reads <see cref="EnteredAt"/> (T1.47, issues #59 and the rulings of 2026-09-29).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Set by the Registry on every state change, and read by the row's clock and by
    /// nothing else.</strong> The sort order, the nudge ladder and the roster settle read
    /// <see cref="EnteredAt"/>, as T1.40 ruled, and <c>DisplayOnlyAnchorTests</c> holds them to it.
    /// </para>
    /// <list type="table">
    /// <listheader><term>Entering</term><description>The anchor</description></listheader>
    /// <item><term>Unread</term><description>The finish, <see cref="Exchange.AnsweredAt"/> — the same
    /// instant as <see cref="EnteredAt"/>, because the Stop that answers is the one that enters.</description></item>
    /// <item><term>Interrupted</term><description>The last event heard, <see cref="LastHeardAt"/>,
    /// copied by the sweep. The sweep itself comes a threshold later.</description></item>
    /// <item><term>Acked, Ended</term><description>An acknowledgment or a close never restarts the
    /// clock: the anchor of the state left, carried over, so Unread → Acked → Ended still reads the
    /// finish. From Working or Waiting, mid-turn, there is nothing to carry, and it is the ack or
    /// the close.</description></item>
    /// <item><term>Every other state</term><description>The instant it was entered.</description></item>
    /// </list>
    /// <para>
    /// <strong>Stored, not derived, because one case cannot be derived.</strong> Interrupted, then
    /// Ended, must still read the silence. <c>SessionEnd</c> advances <see cref="LastHeardAt"/>, and
    /// the transition log records only the sweep's instant, so the time of the last event heard
    /// would be gone by then. Carrying the anchor across the move keeps it.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ClockAnchor { get; init; }

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
    /// <strong>Verbatim.</strong> A session the operator did not name gets a title written by a background model
    /// call summarising their first prompt, so this is prose rather than an identifier: rendered and escaped, and
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

    /// <summary>
    /// The background tasks the session is waiting on: what the last <c>Stop</c> left running,
    /// provided no prompt has arrived since (T1.41, issue #52). Never null; empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Non-empty means exactly this:</strong> the last Stop left allowed work running, and
    /// nothing has woken the session since. Set by every Stop that moves the session, cleared by
    /// every prompt that does — the reviewer's ruling on T1.41. That is what lets a
    /// <c>PostToolBatch</c> return a session to <see cref="SessionState.Waiting"/> rather than
    /// Working after a subagent's own permission prompt, question or error: the batch is the
    /// subagent's, and the parent is still waiting on it.
    /// </para>
    /// <para>
    /// It is what the row's "Waiting on" block shows while the session is Waiting. The tasks'
    /// first-seen instants survive a prompt in <see cref="ListedTasks"/>, not here.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public WaitingTasks WaitingOn
    {
        get => _waitingOn;
        init => _waitingOn = value ?? throw new ArgumentNullException(nameof(value));
    }

    private readonly WaitingTasks _waitingOn = WaitingTasks.Empty;

    /// <summary>
    /// The tasks the last <c>Stop</c> listed, with the instant each was first listed — kept across
    /// prompts so a task keeps its age (T1.41). Never null; empty by default.
    /// </summary>
    /// <remarks>
    /// Replaced by every Stop that moves the session and by nothing else. The next Stop reads it to
    /// carry each still-listed task's first-seen instant forward, and drops the ids it no longer
    /// lists. Separate from <see cref="WaitingOn"/> because a prompt must clear that one and must
    /// not reset a task's age: a build still running after the wake-up is the same build, and the
    /// "Waiting on" line says how long it has run. Read for nothing else.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public WaitingTasks ListedTasks
    {
        get => _listedTasks;
        init => _listedTasks = value ?? throw new ArgumentNullException(nameof(value));
    }

    private readonly WaitingTasks _listedTasks = WaitingTasks.Empty;

    /// <summary>
    /// The prompts of the scheduled jobs the session's latest <c>Stop</c> listed (T1.44). Never
    /// null; none by default.
    /// </summary>
    /// <remarks>
    /// Replaced by every Stop the Registry applies. A prompt that exactly equals one of these is
    /// the session's own cron firing — a tick — rather than anything anyone typed. See
    /// <see cref="QuietTicks"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public ScheduledPrompts ScheduledPrompts
    {
        get => _scheduledPrompts;
        init => _scheduledPrompts = value ?? throw new ArgumentNullException(nameof(value));
    }

    private readonly ScheduledPrompts _scheduledPrompts = ScheduledPrompts.None;

    /// <summary>
    /// What the row showed before a tick of the session's own scheduled job began, while that tick
    /// is running; null otherwise (T1.44).
    /// </summary>
    /// <remarks>
    /// Taken when a tick's prompt arrives, cleared by any other prompt and by the tick's Stop. If
    /// that Stop's reply is exactly the sentinel, the row is put back to this — state, answer,
    /// entry instant and what it waited on — as though the tick never happened.
    /// </remarks>
    public TickSnapshot? PreTick { get; init; }
}
