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
public sealed class SessionRegistry
{
    private readonly Dictionary<SessionId, Session> _sessions = [];

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
    /// <see langword="true"/> if the Registry changed; <see langword="false"/> if the event was
    /// dropped as stale, duplicate, uncorrelated, or inapplicable in the session's current
    /// state. A <see langword="false"/> is normal traffic, not an error.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="inboundEvent"/> is null.</exception>
    public bool Apply(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        if (inboundEvent.SessionId.IsEmpty)
        {
            // An event naming no session cannot be filed against one. `SessionId` is a struct,
            // so `default` is reachable however carefully ingress is written.
            return false;
        }

        if (!_sessions.TryGetValue(inboundEvent.SessionId, out var current))
        {
            var created = Create(inboundEvent);
            if (created is null)
            {
                return false;
            }

            _sessions[created.Id] = created;
            SessionChanged?.Invoke(this, new SessionChangedEventArgs(SessionChangeKind.Added, created));
            return true;
        }

        if (inboundEvent.Timestamp < current.LastActivity)
        {
            return false;
        }

        var next = Transition(current, inboundEvent);
        if (next is null)
        {
            return false;
        }

        _sessions[next.Id] = next;
        SessionChanged?.Invoke(this, new SessionChangedEventArgs(SessionChangeKind.Updated, next));
        return true;
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
            Group = DeriveGroup(inboundEvent.Cwd, inboundEvent.SessionId),
            EnteredAt = inboundEvent.Timestamp,
            LastActivity = inboundEvent.Timestamp,
            ErrorKind = (inboundEvent as StopFailure)?.ErrorKind,
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
    private static Session? Transition(Session current, InboundEvent inboundEvent) => inboundEvent switch
    {
        SessionStart start => ApplySessionStart(current, start),
        UserPromptSubmit prompt => ApplyUserPromptSubmit(current, prompt),
        Notification notification => ApplyNotification(current, notification),
        Stop stop => ApplyStop(current, stop),
        StopFailure failure => ApplyStopFailure(current, failure),
        SessionEnd end => ApplySessionEnd(current, end),
        CwdChanged cwd => ApplyCwdChanged(current, cwd),
        Ack ack => ApplyAck(current, ack),
        _ => null,
    };

    /// <summary>
    /// <c>SessionStart</c> refreshes an existing session without disturbing its state — a
    /// <c>resume</c> or <c>fork</c> "surfaces a pre-existing one" (Impl §9.1), and a session
    /// that was mid-turn is still mid-turn.
    /// </summary>
    private static Session? ApplySessionStart(Session current, SessionStart start) =>
        current.State == SessionState.Ended
            ? Revived(current, start)
            : RelocatedIfMoved(current, start);

    /// <summary>
    /// <c>UserPromptSubmit</c> → <see cref="SessionState.Working"/> from <em>any</em> state
    /// (TS §IV.1), and in doing so auto-acknowledges whatever was pending: the operator cannot
    /// have typed a new prompt without having seen the previous result (TS §II.2).
    /// </summary>
    private static Session? ApplyUserPromptSubmit(Session current, UserPromptSubmit prompt)
    {
        if (IsDuplicatePrompt(current, prompt))
        {
            return null;
        }

        var exchange = new Exchange
        {
            Prompt = prompt.Prompt,
            PromptId = prompt.PromptId,
            StartedAt = prompt.Timestamp,
        };

        return Moved(
            current,
            SessionState.Working,
            prompt,
            latest: exchange,
            errorKind: null,
            cause: AutoAcks(current.State)
                ? $"{prompt.HookEventName} (auto-ack of {current.State})"
                : prompt.HookEventName);
    }

    private static Session? ApplyNotification(Session current, Notification notification)
    {
        if (current.State == SessionState.Ended)
        {
            return null;
        }

        // agent_completed is corroboration only — Stop is the authoritative "finished" signal
        // (Impl §9.1 marks it optional). An unrecognized type degrades to no effect.
        return TargetOf(notification) is { } target
            ? Moved(current, target, notification)
            : null;
    }

    private static Session? ApplyStop(Session current, Stop stop)
    {
        if (current.State == SessionState.Ended)
        {
            return null;
        }

        if (!Correlates(current, stop.PromptId))
        {
            // This Stop belongs to a turn that is already over. See guard 2 on the type.
            return null;
        }

        if (current.State == SessionState.Unread && current.Latest.IsAnswered)
        {
            // Already recorded this turn's answer.
            return null;
        }

        var answered = current.Latest with
        {
            Answer = stop.LastAssistantMessage,
            AnsweredAt = stop.Timestamp,
        };

        return Moved(current, SessionState.Unread, stop, latest: answered, errorKind: null);
    }

    private static Session? ApplyStopFailure(Session current, StopFailure failure)
    {
        if (current.State == SessionState.Ended)
        {
            return null;
        }

        if (current.State == SessionState.Error &&
            string.Equals(current.ErrorKind, failure.ErrorKind, StringComparison.Ordinal))
        {
            return null;
        }

        return Moved(current, SessionState.Error, failure, errorKind: failure.ErrorKind);
    }

    /// <summary><c>SessionEnd</c> → <see cref="SessionState.Ended"/> from any state (TS §IV.1).</summary>
    private static Session? ApplySessionEnd(Session current, SessionEnd end) =>
        current.State == SessionState.Ended ? null : Moved(current, SessionState.Ended, end);

    private static Session? ApplyCwdChanged(Session current, CwdChanged moved) =>
        current.State == SessionState.Ended ? null : RelocatedIfMoved(current, moved);

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
    private static Session? ApplyAck(Session current, Ack ack) =>
        IsAcknowledgeable(current.State)
            ? Moved(current, SessionState.Acked, ack, errorKind: null, cause: $"{ack.HookEventName} ({ack.Source})")
            : null;

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
            Group = DeriveGroup(inboundEvent.Cwd, current.Id),
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
            Group = DeriveGroup(start.Cwd, current.Id),
            ErrorKind = null,
            EnteredAt = start.Timestamp,
            LastActivity = start.Timestamp,
            Transitions = current.Transitions.Append(
                new StateTransition(current.State, SessionState.Acked, start.Timestamp, start.HookEventName)),
        };

    // ---- Predicates -------------------------------------------------------------------------

    private static SessionState? TargetOf(Notification notification) => notification.Kind switch
    {
        NotificationKind.PermissionPrompt => SessionState.NeedsPermission,
        NotificationKind.IdlePrompt or NotificationKind.AgentNeedsInput => SessionState.NeedsQuestion,
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

    /// <summary>The states a new prompt implicitly acknowledges (TS §IV.1).</summary>
    private static bool AutoAcks(SessionState state) =>
        state is SessionState.Unread
              or SessionState.NeedsPermission
              or SessionState.NeedsQuestion
              or SessionState.Error;

    /// <summary>
    /// The states an explicit acknowledgment applies to.
    /// </summary>
    /// <remarks>
    /// TS §IV.1 draws <c>Ack</c> from <see cref="SessionState.Unread"/> and the two Needs-You
    /// states. <see cref="SessionState.Error"/> is included because TS §IV.2 bands Error with
    /// Needs You, and because an operator who has read a failed turn must be able to dismiss
    /// it — otherwise an Error row can only be cleared by submitting another prompt.
    /// </remarks>
    private static bool IsAcknowledgeable(SessionState state) =>
        state is SessionState.Unread
              or SessionState.NeedsPermission
              or SessionState.NeedsQuestion
              or SessionState.Error;

    /// <summary>
    /// The Phase 1 group key: the workspace, falling back to the session's own id when no
    /// <c>cwd</c> is known, so an ungroupable session groups alone rather than pooling with
    /// every other directory-less one.
    /// </summary>
    /// <remarks>
    /// Provisional and minimal. TS §IV.3 makes <c>cwd</c> the Phase 1 key, and
    /// <see cref="Session.Group"/> cannot be left unset, so the Registry has to produce one.
    /// The group <em>resolver</em> — partitioning sessions into <see cref="Group"/> containers,
    /// and any path normalization that implies — is T1.4's, and this is expected to defer to it.
    /// </remarks>
    private static GroupKey DeriveGroup(string cwd, SessionId sessionId) =>
        string.IsNullOrWhiteSpace(cwd) ? new GroupKey(sessionId.Value) : new GroupKey(cwd);
}
