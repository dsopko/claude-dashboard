using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Core;

/// <summary>
/// The dashboard's world model: every session it has seen, and the state the last event left
/// it in (TS §I.2, §IV.1; Impl §2.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single-writer, and therefore lock-free — this is an invariant, not an oversight.</strong>
/// Exactly one thread calls <see cref="Apply"/>: the event consumer reading the channel
/// (Impl §4). Every producer — Kestrel request threads, and Phase 3's focus inference —
/// reaches this type through that one channel, which is what serializes them. Do not add a
/// lock here to make it "thread-safe": a lock would not make <see cref="Sessions"/> safe to
/// enumerate concurrently anyway, and it would signal that concurrent callers are acceptable
/// when the design says they are not.
/// </para>
/// <para>
/// <strong>Deterministic and replayable.</strong> Every timestamp this type writes comes from
/// the event being applied, never from a clock. That is deliberate: warm restart replays
/// persisted events (Impl §8), and a Registry that read "now" while replaying would rebuild a
/// different world than the one it saved. It is also why this type takes no
/// <see cref="Ports.IClock"/> — there is nothing for it to ask.
/// </para>
/// <para>
/// <strong>Three guards, and they are not redundant.</strong> Delivery is at-least-once and
/// can reorder (TS §I.2), and each guard covers a case the others cannot:
/// </para>
/// <list type="number">
/// <item><description>
/// <em>Timestamp guard</em> — an event older than the session's last-applied stamp is dropped
/// (TS §IV.1). Hook events cannot trip this on their own, because ingress stamps them on
/// arrival and the channel is FIFO, so their stamps are already monotonic; it earns its place
/// on events with independent origins — a manual or inferred-focus <see cref="Ack"/>, and
/// replayed events on warm restart.
/// </description></item>
/// <item><description>
/// <em>Correlation guard</em> — a <see cref="Stop"/> whose <c>prompt_id</c> does not match the
/// session's current exchange is rejected. This is the guard that matters most in practice.
/// A redelivered event carries a <em>fresh, later</em> stamp, so it passes guard 1; and
/// because the stamp is part of record equality it is not equal to the original either, so it
/// would pass any equality-based dedupe. Without correlation, a delayed duplicate
/// <see cref="Stop"/> arriving after a new prompt would drag a live <see cref="SessionState.Working"/>
/// session back to <see cref="SessionState.Unread"/> — a false "finished" chime on a session
/// that is still running, which is precisely the failure this product exists to prevent.
/// </description></item>
/// <item><description>
/// <em>Outcome idempotency</em> — an event that would leave the session exactly as it is has
/// no effect: no state change, no transition-log entry, no notification, and no advance of
/// <see cref="Session.LastActivity"/>. Re-applying the current state is a no-op (TS §IV.1),
/// and because a redelivery leaves no trace it cannot perturb the ordering the UI sorts by.
/// </description></item>
/// </list>
/// </remarks>
public sealed class SessionRegistry(SingleWriterGuard guard)
{
    private readonly Dictionary<SessionId, Session> _sessions = [];

    /// <summary>
    /// The single-writer region this Registry mutates inside (Impl 2.2).
    /// </summary>
    /// <remarks>
    /// Shared with the sound engine when both are composed by the host, so a thread inside one
    /// cannot be inside the other. A Registry built without one gets its own, which is what an
    /// isolated unit test wants.
    /// </remarks>
    private readonly SingleWriterGuard _guard = guard ?? throw new ArgumentNullException(nameof(guard));

    /// <summary>
    /// Every session the Registry has seen, keyed by <c>session_id</c>.
    /// </summary>
    /// <remarks>
    /// A live view, not a snapshot: it reflects each <see cref="Apply"/> immediately. Safe to
    /// read from the single writer thread; enumerating it from another thread while events are
    /// being applied is not supported, per the single-writer invariant on this type.
    /// </remarks>
    public IReadOnlyDictionary<SessionId, Session> Sessions => _sessions;

    /// <summary>
    /// Raised once for each event that actually changed something. Dropped, stale, duplicate
    /// and inapplicable events raise nothing.
    /// </summary>
    public event EventHandler<SessionChangedEventArgs>? SessionChanged;

    /// <summary>
    /// Applies <paramref name="inboundEvent"/> to the session it names.
    /// </summary>
    /// <returns>
    /// What was done with it. Only <see cref="ApplyOutcome.Applied"/> changed the Registry; the
    /// rest are declines, of which <see cref="ApplyOutcome.Uncorrelated"/> is the only one that
    /// should not be happening. See <see cref="ApplyOutcome"/> for why that distinction is worth
    /// a type.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="inboundEvent"/> is null.</exception>
    public ApplyOutcome Apply(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        using var writing = _guard.Enter("applying an event to the Registry");

        if (inboundEvent.SessionId.IsEmpty)
        {
            // An event naming no session cannot be filed against one. `SessionId` is a struct,
            // so `default` is reachable however carefully ingress is written.
            return ApplyOutcome.Ignored;
        }

        if (!_sessions.TryGetValue(inboundEvent.SessionId, out var current))
        {
            var created = Create(inboundEvent);
            if (created is null)
            {
                return ApplyOutcome.Ignored;
            }

            _sessions[created.Id] = created;
            SessionChanged?.Invoke(this, new SessionChangedEventArgs(SessionChangeKind.Added, created));
            return ApplyOutcome.Applied;
        }

        if (inboundEvent.Timestamp < current.LastActivity)
        {
            return ApplyOutcome.Stale;
        }

        var (next, outcome) = Transition(current, inboundEvent);

        // The title latch runs whatever the transition decided. See Latched for why that is not
        // an optimisation but the only thing that makes the feature work at all.
        var titled = Latched(next ?? current, inboundEvent);

        if (next is null && titled is null)
        {
            return outcome;
        }

        var applied = titled ?? next!;

        _sessions[applied.Id] = applied;
        SessionChanged?.Invoke(this, new SessionChangedEventArgs(SessionChangeKind.Updated, applied));
        return ApplyOutcome.Applied;
    }

    // ---- The title latch ---------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="inboundEvent"/>'s <c>session_title</c> to <paramref name="session"/>,
    /// or returns null if the title it holds should not change (issue #18).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THIS RUNS WHATEVER THE TRANSITION DECIDED, AND THAT IS THE WHOLE FEATURE.</strong>
    /// The events that carry a title are, in the main, the events that decline. A
    /// <see cref="PostToolBatch"/> on an already-<see cref="SessionState.Working"/> session is
    /// <see cref="ApplyOutcome.Ignored"/> — that is 799 of the 1,210 payloads in the archive. An
    /// <c>idle_prompt</c> <see cref="Notification"/> is <see cref="ApplyOutcome.Ignored"/> too, and
    /// <see cref="Moved"/> returns null whenever nothing else differs. Latch inside the transition
    /// table and every one of those titles is dropped on the floor, while the tests stay green,
    /// because a test that hands the title to a state-<em>changing</em> event never walks the path
    /// that loses it.
    /// </para>
    /// <para>
    /// So a title alone can produce an <see cref="ApplyOutcome.Applied"/> and a
    /// <see cref="SessionChanged"/>. Nothing keys on that outcome for behaviour — the consumer
    /// uses it for counters and a log level — so this widens what "applied" covers without
    /// changing what anything does with it.
    /// </para>
    /// <para>
    /// <strong>The rule.</strong> Absent, null or whitespace-only leaves the latched value alone,
    /// so a <see cref="Stop"/> — which never carries a title — cannot blank a row. A non-empty
    /// title that differs replaces it, which is what lands all three renames Claude Code documents,
    /// including the startup collision variant the operator never asked for. An identical title is
    /// a no-op. There is no way to <em>remove</em> a title: nothing on the wire says "untitled", so
    /// the dashboard cannot represent one.
    /// </para>
    /// <para>
    /// <strong>A title change never advances <see cref="Session.LastActivity"/> or
    /// <see cref="Session.EnteredAt"/></strong>, which is why this returns
    /// <c>session with { Title = … }</c> and not a <see cref="Moved"/>. <c>LastActivity</c> is the
    /// sort key for the Working and Quiet bands and <c>EnteredAt</c> is the age clock, so a
    /// rename that reordered the list or reset an age would be a cosmetic fact rewriting an
    /// attention fact. This is the same refusal <see cref="Moved"/> already makes for
    /// redeliveries, and it is stated here so that nobody later "fixes" the omission.
    /// </para>
    /// <para>
    /// <strong>THERE IS NO ORDERING GUARD ON THE TITLE, NONE IS AVAILABLE, AND HERE IS WHY.</strong>
    /// Ingress stamps events from <c>IClock</c> at <em>arrival</em>, not occurrence — hook payloads
    /// carry no timestamp of their own — and <see cref="Apply"/> has one writer behind a FIFO
    /// channel, so arrival order is total and stamps are monotonic <em>because of</em> that order.
    /// A stamp comparison here could therefore never disagree with arrival order; it would be a
    /// restatement wearing the name of a guard. The wire offers nothing else: no occurrence time,
    /// no title version, no sequence number.
    /// </para>
    /// <para>
    /// And underneath that is the part no field would fix. <strong>A stale title arriving late and
    /// a genuine rename back to a previous name are the same observation, byte for byte.</strong>
    /// Any rule that rejects the first rejects the second, and Claude Code documents the second as
    /// real. So the rule is the most recently arrived different non-empty title, and the residual
    /// is accepted: a rename racing a same-session event inside the gap between two loopback posts
    /// can show the previous title until the next event carrying the new one. Self-healing,
    /// cosmetic, and never wrong about state.
    /// </para>
    /// </remarks>
    private static Session? Latched(Session session, InboundEvent inboundEvent)
    {
        if (TitleOn(inboundEvent) is not { } incoming ||
            string.Equals(incoming, session.Title, StringComparison.Ordinal))
        {
            return null;
        }

        return session with { Title = incoming };
    }

    /// <summary>
    /// The usable title on <paramref name="inboundEvent"/>, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// Whitespace-only counts as none. It is normalized here rather than at ingress so that the
    /// rule holds for a replayed event and for a hand-built one in a test, not only for a payload
    /// that happened to come through the mapper. The value itself is never trimmed or reshaped —
    /// that is display work, and the domain keeps what arrived.
    /// </remarks>
    private static string? TitleOn(InboundEvent inboundEvent) =>
        string.IsNullOrWhiteSpace(inboundEvent.SessionTitle) ? null : inboundEvent.SessionTitle;

    /// <summary>A transition's result: the session it produced, or why it produced none.</summary>
    private readonly record struct Transitioned(Session? Next, ApplyOutcome Outcome)
    {
        /// <summary>The transition happened.</summary>
        public static Transitioned To(Session next) => new(next, ApplyOutcome.Applied);

        /// <summary>It did not, for <paramref name="outcome"/>.</summary>
        public static Transitioned Declined(ApplyOutcome outcome) => new(null, outcome);

        /// <summary>
        /// The transition produced no change, which is a duplicate — re-applying the current
        /// state is a no-op (TS §IV.1).
        /// </summary>
        public static Transitioned FromMove(Session? moved) =>
            moved is null ? Declined(ApplyOutcome.Duplicate) : To(moved);
    }

    // ---- Creating a session the Registry has never seen -----------------------------------

    /// <summary>
    /// Builds a session from the first event seen for an id, or returns null if this event
    /// should not bring one into existence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hook events create implicitly rather than being dropped for want of a preceding
    /// <see cref="SessionStart"/>. TS §I.2 is explicit that "a session exists in the Registry
    /// because the system saw an event from it", and TS §IV.7's bottom rung — event stream
    /// only — promises the product still "shows sessions from their next event on". Dropping
    /// non-start events would break both: a session already running when the dashboard starts,
    /// or one whose <see cref="SessionStart"/> was reordered behind its <see cref="Stop"/>,
    /// would stay invisible until it happened to restart.
    /// </para>
    /// <para>
    /// A synthetic <see cref="Ack"/> is the exception and creates nothing. It is not an
    /// observation of a session — it is the dashboard reporting that the operator saw
    /// something, and it can only originate from the acknowledge affordance or from focus
    /// inference, both of which require a session that already exists. An ack for an unknown
    /// id is a race or a bug, and materializing a blank acknowledged row from one would put a
    /// session on screen that nothing has ever been observed about.
    /// </para>
    /// </remarks>
    private static Session? Create(InboundEvent inboundEvent)
    {
        var state = InitialState(inboundEvent);
        if (state is not { } initial)
        {
            return null;
        }

        return new Session
        {
            Id = inboundEvent.SessionId,
            State = initial,
            Latest = InitialExchange(inboundEvent),
            Cwd = inboundEvent.Cwd,
            WorkspaceGroup = DeriveGroup(inboundEvent.Cwd, inboundEvent.SessionId),
            EnteredAt = inboundEvent.Timestamp,
            LastActivity = inboundEvent.Timestamp,
            ErrorKind = (inboundEvent as StopFailure)?.ErrorKind,

            // The very first event a session is seen on may already carry the title, so the latch
            // starts here rather than waiting for a second event to change something.
            Title = TitleOn(inboundEvent),

            Transitions = TransitionLog.Empty.Append(
                new StateTransition(initial, initial, inboundEvent.Timestamp, inboundEvent.HookEventName)),
        };
    }

    private static SessionState? InitialState(InboundEvent inboundEvent) => inboundEvent switch
    {
        // A session that has just started, or merely changed directory, is idle: it exists,
        // and nothing about it wants the operator. TS §IV.2's Quiet band is "Acked, idle", but
        // `SessionState` has no Idle member, so Acked carries both meanings.
        SessionStart or CwdChanged => SessionState.Acked,
        UserPromptSubmit => SessionState.Working,
        Notification notification => TargetOf(notification),
        Stop => SessionState.Unread,
        StopFailure => SessionState.Error,
        SessionEnd => SessionState.Ended,

        // Ack: see Create. Anything unrecognized: degrade rather than guess.
        _ => null,
    };

    private static Exchange InitialExchange(InboundEvent inboundEvent) => inboundEvent switch
    {
        UserPromptSubmit prompt => new Exchange
        {
            Prompt = prompt.Prompt,
            PromptId = prompt.PromptId,
            StartedAt = prompt.Timestamp,
        },

        // A Stop seen before any prompt: the answer is known, the question never was.
        Stop stop => new Exchange
        {
            Prompt = string.Empty,
            Answer = stop.LastAssistantMessage,
            PromptId = stop.PromptId,
            StartedAt = stop.Timestamp,
            AnsweredAt = stop.Timestamp,
        },

        _ => new Exchange { Prompt = string.Empty, StartedAt = inboundEvent.Timestamp },
    };

    // ---- The transition table --------------------------------------------------------------

    /// <summary>
    /// Applies an event to an existing session, or returns null if it has no effect.
    /// </summary>
    private static Transitioned Transition(Session current, InboundEvent inboundEvent) => inboundEvent switch
    {
        SessionStart start => ApplySessionStart(current, start),
        UserPromptSubmit prompt => ApplyUserPromptSubmit(current, prompt),
        Notification notification => ApplyNotification(current, notification),
        Stop stop => ApplyStop(current, stop),
        StopFailure failure => ApplyStopFailure(current, failure),
        SessionEnd end => ApplySessionEnd(current, end),
        CwdChanged cwd => ApplyCwdChanged(current, cwd),
        Ack ack => ApplyAck(current, ack),
        PostToolBatch batch => ApplyPostToolBatch(current, batch),
        _ => Transitioned.Declined(ApplyOutcome.Ignored),
    };

    /// <summary>
    /// The turn is running again: a session blocked on the operator, or stopped by an error,
    /// returns to <see cref="SessionState.Working"/> (TS §IV.1, 2026-08-25; issue #2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only the three states that were waiting.</strong> A batch of tool calls resolving
    /// proves the session is executing, which contradicts "blocked on the operator" and
    /// "stopped" — but it says nothing about a session that has already finished.
    /// </para>
    /// <para>
    /// <strong><see cref="SessionState.Unread"/> must never resume, and that is the load-bearing
    /// half.</strong> Un-reading a finished result is issue #1's failure mirrored, and the worse
    /// direction: #1 was loud and wrong — everything turned red — whereas this would be quiet and
    /// wrong, quietly emptying the band the dashboard exists to fill. A late batch arriving after
    /// a <c>Stop</c> is exactly the shape that would do it.
    /// </para>
    /// <para>
    /// <strong>Accepted residual, written down rather than noticed later.</strong> Nothing fires
    /// when the operator approves — there is no <c>PermissionGranted</c> hook — so the row stays
    /// red from the approval until the tool <em>finishes</em>. This shortens the gap from "the
    /// rest of the turn" to "the rest of this tool call"; with the hooks that exist it cannot be
    /// closed further, only shortened again if a signal that fires at the decision ever appears.
    /// </para>
    /// <para>
    /// Every other state declines as <see cref="ApplyOutcome.Ignored"/>, which is the common case
    /// by a wide margin: an already-<see cref="SessionState.Working"/> session produces one of
    /// these per tool batch and must go on being Working without a transition being recorded for
    /// each.
    /// </para>
    /// </remarks>
    private static Transitioned ApplyPostToolBatch(Session current, PostToolBatch batch) =>
        current.State is SessionState.NeedsPermission or SessionState.NeedsQuestion or SessionState.Error
            ? Transitioned.FromMove(Moved(current, SessionState.Working, batch))
            : Transitioned.Declined(ApplyOutcome.Ignored);

    /// <summary>
    /// <c>SessionStart</c> refreshes an existing session without disturbing its state — a
    /// <c>resume</c> or <c>fork</c> "surfaces a pre-existing one" (Impl §9.1), and a session
    /// that was mid-turn is still mid-turn.
    /// </summary>
    private static Transitioned ApplySessionStart(Session current, SessionStart start) =>
        current.State == SessionState.Ended
            ? Transitioned.To(Revived(current, start))
            : Transitioned.FromMove(RelocatedIfMoved(current, start));

    /// <summary>
    /// <c>UserPromptSubmit</c> → <see cref="SessionState.Working"/> from <em>any</em> state
    /// (TS §IV.1), and in doing so auto-acknowledges whatever was pending: the operator cannot
    /// have typed a new prompt without having seen the previous result (TS §II.2).
    /// </summary>
    private static Transitioned ApplyUserPromptSubmit(Session current, UserPromptSubmit prompt)
    {
        if (IsDuplicatePrompt(current, prompt))
        {
            return Transitioned.Declined(ApplyOutcome.Duplicate);
        }

        var exchange = new Exchange
        {
            Prompt = prompt.Prompt,
            PromptId = prompt.PromptId,
            StartedAt = prompt.Timestamp,
        };

        return Transitioned.FromMove(Moved(
            current,
            SessionState.Working,
            prompt,
            latest: exchange,
            errorKind: null,
            cause: Acknowledgment.Applies(current.State)
                ? $"{prompt.HookEventName} (auto-ack of {current.State})"
                : prompt.HookEventName));
    }

    private static Transitioned ApplyNotification(Session current, Notification notification)
    {
        if (current.State == SessionState.Ended)
        {
            return Transitioned.Declined(ApplyOutcome.Ignored);
        }

        // A notification that moves nothing is declined rather than applied: idle_prompt and
        // agent_completed by design, an unrecognised type by degradation. See TargetOf.
        return TargetOf(notification) is { } target
            ? Transitioned.FromMove(Moved(current, target, notification))
            : Transitioned.Declined(ApplyOutcome.Ignored);
    }

    private static Transitioned ApplyStop(Session current, Stop stop)
    {
        if (current.State == SessionState.Ended)
        {
            return Transitioned.Declined(ApplyOutcome.Ignored);
        }

        if (!Correlates(current, stop.PromptId))
        {
            // This Stop belongs to a turn that is already over. See guard 2 on the type — and
            // ApplyOutcome.Uncorrelated for why this decline is reported differently from the
            // routine ones.
            return Transitioned.Declined(ApplyOutcome.Uncorrelated);
        }

        if (current.State == SessionState.Unread && current.Latest.IsAnswered)
        {
            // Already recorded this turn's answer.
            return Transitioned.Declined(ApplyOutcome.Duplicate);
        }

        var answered = current.Latest with
        {
            Answer = stop.LastAssistantMessage,
            AnsweredAt = stop.Timestamp,
        };

        return Transitioned.FromMove(
            Moved(current, SessionState.Unread, stop, latest: answered, errorKind: null));
    }

    private static Transitioned ApplyStopFailure(Session current, StopFailure failure)
    {
        if (current.State == SessionState.Ended)
        {
            return Transitioned.Declined(ApplyOutcome.Ignored);
        }

        if (current.State == SessionState.Error &&
            string.Equals(current.ErrorKind, failure.ErrorKind, StringComparison.Ordinal))
        {
            return Transitioned.Declined(ApplyOutcome.Duplicate);
        }

        return Transitioned.FromMove(
            Moved(current, SessionState.Error, failure, errorKind: failure.ErrorKind));
    }

    /// <summary><c>SessionEnd</c> → <see cref="SessionState.Ended"/> from any state (TS §IV.1).</summary>
    private static Transitioned ApplySessionEnd(Session current, SessionEnd end) =>
        current.State == SessionState.Ended
            ? Transitioned.Declined(ApplyOutcome.Duplicate)
            : Transitioned.FromMove(Moved(current, SessionState.Ended, end));

    private static Transitioned ApplyCwdChanged(Session current, CwdChanged moved) =>
        current.State == SessionState.Ended
            ? Transitioned.Declined(ApplyOutcome.Ignored)
            : Transitioned.FromMove(RelocatedIfMoved(current, moved));

    /// <summary>
    /// A manual or inferred-focus acknowledgment → <see cref="SessionState.Acked"/>
    /// (TS §IV.1).
    /// </summary>
    /// <remarks>
    /// Only from a state that has something to acknowledge. Acking a
    /// <see cref="SessionState.Working"/> session is meaningless — nothing has finished — and
    /// acking an <see cref="SessionState.Ended"/> one changes nothing that is still competing
    /// for attention.
    /// </remarks>
    private static Transitioned ApplyAck(Session current, Ack ack) =>
        Acknowledgment.Applies(current.State)
            ? Transitioned.FromMove(Moved(
                current, SessionState.Acked, ack, errorKind: null, cause: $"{ack.HookEventName} ({ack.Source})"))
            : Transitioned.Declined(ApplyOutcome.Ignored);

    // ---- Shared transition mechanics --------------------------------------------------------

    /// <summary>
    /// Produces the session that results from moving to <paramref name="to"/>, appending the
    /// transition to the log and advancing <see cref="Session.LastActivity"/>. Returns null if
    /// nothing would actually differ.
    /// </summary>
    private static Session? Moved(
        Session current,
        SessionState to,
        InboundEvent inboundEvent,
        Exchange? latest = null,
        string? errorKind = null,
        string? cause = null)
    {
        var exchange = latest ?? current.Latest;
        var stateChanged = current.State != to;

        if (!stateChanged && exchange == current.Latest &&
            string.Equals(errorKind, current.ErrorKind, StringComparison.Ordinal))
        {
            // Re-applying the current state is a no-op (TS §IV.1) — and leaves no trace, so a
            // redelivery cannot bump the session up the recency ordering.
            return null;
        }

        return current with
        {
            State = to,
            Latest = exchange,
            ErrorKind = errorKind,

            // Only a real state change restarts the age clock the Needs-You and Unread bands
            // sort on; enriching the exchange in place must not make a session look newer.
            EnteredAt = stateChanged ? inboundEvent.Timestamp : current.EnteredAt,
            LastActivity = inboundEvent.Timestamp,
            Transitions = current.Transitions.Append(
                new StateTransition(current.State, to, inboundEvent.Timestamp, cause ?? inboundEvent.HookEventName)),
        };
    }

    /// <summary>Updates <c>cwd</c> and the derived group if the directory actually moved.</summary>
    private static Session? RelocatedIfMoved(Session current, InboundEvent inboundEvent)
    {
        if (string.Equals(current.Cwd, inboundEvent.Cwd, StringComparison.Ordinal))
        {
            return null;
        }

        return current with
        {
            Cwd = inboundEvent.Cwd,
            WorkspaceGroup = DeriveGroup(inboundEvent.Cwd, current.Id),
            LastActivity = inboundEvent.Timestamp,
        };
    }

    /// <summary>
    /// A <see cref="SessionStart"/> for a session the Registry had marked
    /// <see cref="SessionState.Ended"/>: it is demonstrably alive again.
    /// </summary>
    /// <remarks>
    /// Reachable in normal use — <c>SessionEnd</c>'s matchers include <c>resume</c>, and
    /// <c>SessionStart</c>'s include <c>resume</c> and <c>fork</c>, so one session id can
    /// legitimately end and start again. Leaving it dimmed as Ended would be simply wrong.
    /// </remarks>
    private static Session Revived(Session current, SessionStart start) =>
        current with
        {
            State = SessionState.Acked,
            Cwd = start.Cwd,
            WorkspaceGroup = DeriveGroup(start.Cwd, current.Id),
            ErrorKind = null,
            EnteredAt = start.Timestamp,
            LastActivity = start.Timestamp,
            Transitions = current.Transitions.Append(
                new StateTransition(current.State, SessionState.Acked, start.Timestamp, start.HookEventName)),
        };

    // ---- Predicates -------------------------------------------------------------------------

    /// <summary>
    /// The state a notification moves a session to, or null if it moves it nowhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two kinds are recognised and deliberately inert, which is not the same as
    /// unrecognised.</strong> They are named rather than left to the catch-all because the
    /// catch-all means "this build does not know what this is" — a genuinely different claim, and
    /// the next reader has no way to tell a considered no-op from an oversight if both arrive at
    /// the same line.
    /// </para>
    /// <para>
    /// <strong><see cref="NotificationKind.IdlePrompt"/> is not a question (TS §II.2, corrected
    /// 2026-08-24 — issue #1).</strong> <c>agent_needs_input</c> is a request; <c>idle_prompt</c>
    /// is the absence of one. Claude Code emits it because a session has been sitting untouched,
    /// and every session that finishes eventually is — so treating it as a question promoted
    /// every Unread to red-and-blinking Needs You about ninety seconds after it finished. On one
    /// day of real use that was 207 notifications against 13 permission requests, so it was the
    /// steady state rather than an edge. Idleness is already modelled: an unread result is
    /// <see cref="SessionState.Unread"/> and a read one is quiet, and neither is a question.
    /// </para>
    /// <para>
    /// <see cref="NotificationKind.AgentCompleted"/> is corroboration only —
    /// <see cref="Stop"/> is the authoritative "finished" signal (Impl §9.1 marks it optional) —
    /// so it moves nothing either, and now says so.
    /// </para>
    /// </remarks>
    private static SessionState? TargetOf(Notification notification) => notification.Kind switch
    {
        NotificationKind.PermissionPrompt => SessionState.NeedsPermission,
        NotificationKind.AgentNeedsInput => SessionState.NeedsQuestion,

        // Recognised, and deliberately moves nothing. See the remarks.
        NotificationKind.IdlePrompt or NotificationKind.AgentCompleted => null,

        // Unrecognised: degrade rather than guess.
        _ => null,
    };

    /// <summary>
    /// Whether a <see cref="Stop"/> belongs to the turn the session is currently tracking.
    /// </summary>
    /// <remarks>
    /// Correlation needs a <c>prompt_id</c> on both sides. When either is absent the guard
    /// cannot fire and the event is allowed through — refusing an uncorrelatable
    /// <see cref="Stop"/> would silently lose real completions, which is a worse failure than
    /// the duplicate this guard exists to catch.
    /// </remarks>
    private static bool Correlates(Session current, string? promptId) =>
        promptId is not { } incoming ||
        current.Latest.PromptId is not { } tracked ||
        string.Equals(incoming, tracked, StringComparison.Ordinal);

    /// <remarks>
    /// Dedupes on <c>prompt_id</c> where there is one. Where there is not, an identical prompt
    /// on an already-<see cref="SessionState.Working"/> session is treated as a redelivery: a
    /// genuine resubmission of byte-identical text loses only a restarted age clock, whereas
    /// treating a redelivery as new work restarts that clock wrongly on every duplicate.
    /// </remarks>
    private static bool IsDuplicatePrompt(Session current, UserPromptSubmit prompt) =>
        prompt.PromptId is { } incoming
            ? string.Equals(current.Latest.PromptId, incoming, StringComparison.Ordinal)
            : current.State == SessionState.Working &&
              current.Latest.PromptId is null &&
              string.Equals(current.Latest.Prompt, prompt.Prompt, StringComparison.Ordinal);

    /// <summary>
    /// The key a session groups under.
    /// </summary>
    /// <remarks>
    /// The Registry stamps a key onto every session because <see cref="Session.WorkspaceGroup"/> cannot
    /// be left unset, but it does not <em>decide</em> the key: the policy — including
    /// normalization and the no-workspace case — belongs to <see cref="GroupKeys"/>, which is
    /// its only home. Keeping a second rule here is exactly how the Registry and the resolver
    /// would drift into disagreeing about which group a session is in.
    /// </remarks>
    private static GroupKey DeriveGroup(string cwd, SessionId sessionId) =>
        GroupKeys.ForSession(cwd, sessionId);
}
