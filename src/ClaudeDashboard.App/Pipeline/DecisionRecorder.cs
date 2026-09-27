using System.Collections.Concurrent;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// Assembles the decisions record: every judgement the dashboard makes while handling one event
/// or one tick, gathered on the consumer thread and handed to the archive as one record
/// (T1.37, issue #48).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A scribe, not a judge.</strong> Nothing here decides anything. The Registry's rows
/// are derived from what <c>Apply</c> returned and what the session was before and after — read
/// on the same thread that owns the Registry, at the moment of the decision. The sound rows
/// arrive through <see cref="IDecisionSink"/>, the engine saying itself what it decided. The
/// recorder's whole job is to put them beside the event that caused them.
/// </para>
/// <para>
/// <strong>Scoped to the consumer thread.</strong> <see cref="BeginEvent"/> or
/// <see cref="BeginTick"/> opens a scope, the decisions accumulate, and
/// <see cref="Complete"/> builds the <see cref="ArchiveRecord"/> and offers it — one
/// non-blocking hand-off, or two when decisions born on other threads are pending. Those — a
/// channel drop on the ingress thread, the tray light on the dispatcher, a <c>/show</c> on a
/// Kestrel thread — go through <see cref="External"/>, a concurrent queue the next scope
/// drains, and leave as their own record first, with <c>event_id NULL</c>, so they never
/// borrow the scope's event id. Neither hand-off blocks.
/// </para>
/// <para>
/// <strong>Identifiers and enums only, throughout (T1.24).</strong> Never a title, prompt,
/// payload or message body. The one text that comes near — the exception in
/// <see cref="ApplyFailed"/> — is recorded as its TYPE name alone.
/// </para>
/// </remarks>
/// <summary>
/// The narrow face of <see cref="DecisionRecorder"/> for decisions born off the consumer thread
/// (T1.37).
/// </summary>
/// <remarks>
/// Deliberately not registered in the container: the UI types that take it do so as an optional
/// parameter, which the composition guard permits only for a type that is not a service. The
/// factory in <c>AppHost</c> passes the recorder explicitly, and a composition test asserts it
/// arrived — the two halves of "optional in tests, mandatory in the product".
/// </remarks>
public interface IDecisionLog
{
    /// <summary>Records a decision made off the consumer thread. Thread-safe.</summary>
    void External(Decision decision);
}

public sealed class DecisionRecorder : IDecisionSink, IDecisionLog
{
    private readonly SessionRegistry _registry;
    private readonly RosterStore _rosters;
    private readonly EventArchive _archive;
    private readonly ILogger _logger;
    private readonly List<Decision> _buffer = [];
    private readonly List<Decision> _externalBuffer = [];
    private readonly ConcurrentQueue<Decision> _external = new();

    private InboundEvent? _current;
    private DateTimeOffset _now;
    private bool _inScope;
    private DateTimeOffset? _muteExpiryArmed;

    /// <summary>Creates the recorder.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DecisionRecorder(SessionRegistry registry, RosterStore rosters, EventArchive archive, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rosters);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _rosters = rosters;
        _archive = archive;
        _logger = logger;
    }

    /// <summary>How many decisions have been recorded. Diagnostic only.</summary>
    public long RecordedCount { get; private set; }

    // ---- Scopes -------------------------------------------------------------------------------

    /// <summary>Opens the scope for one event. Consumer thread only.</summary>
    public void BeginEvent(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        _current = inboundEvent;
        _now = inboundEvent.Timestamp;
        _inScope = true;
        _buffer.Clear();
        DrainExternal();
    }

    /// <summary>Opens the scope for a tick or a settle pass. Consumer thread only.</summary>
    public void BeginTick(DateTimeOffset now)
    {
        _current = null;
        _now = now;
        _inScope = true;
        _buffer.Clear();
        DrainExternal();

        // A timed global mute that has lapsed produces no event by design — "a predicate, not a
        // timer" — so the lapse is observed here, on the tick, from the instant it was armed for.
        if (_muteExpiryArmed is { } until && now >= until)
        {
            _muteExpiryArmed = null;
            Add(new Decision(now, null, DecisionKind.MuteExpired, Detail: $"until={until:o}"));
        }
    }

    /// <summary>
    /// Closes the scope: builds the record and offers it to the archive. Consumer thread only.
    /// </summary>
    /// <remarks>
    /// A hook event rides as the record's event row — the Ack included, empty payload and all;
    /// its absence from the table was exactly the hole issue #48's investigation fell into. The
    /// synthetic channel riders — sound commands, roster-edit wakes — carry no payload worth a
    /// row and land as decisions with <c>event_id NULL</c>.
    /// </remarks>
    public void Complete()
    {
        if (!_inScope)
        {
            return;
        }

        _inScope = false;

        // Decisions born on other threads go out as their own record, before the scoped one:
        // they happened before this scope opened, and they were not caused by its event. Ridden
        // into the event's record they would be written under its event_id — a false attribution
        // the join would then repeat forever. Their own record carries event_id NULL, which is
        // the truth: nothing in the events table caused them.
        if (_externalBuffer.Count > 0)
        {
            _archive.TryArchive(new ArchiveRecord(null, [.. _externalBuffer]));
            _externalBuffer.Clear();
        }

        var eventRow = _current is null or SoundCommand or RostersChanged ? null : _current;
        var record = new ArchiveRecord(eventRow, [.. _buffer]);

        _current = null;
        _buffer.Clear();

        if (record.IsEmpty)
        {
            return;
        }

        _archive.TryArchive(record);
    }

    // ---- Registry rows, derived where the consumer already stands -----------------------------

    /// <summary>
    /// Records what the Registry did with the event, from its outcome and the session before and
    /// after — read on the one thread that owns the Registry, at the moment of the decision.
    /// </summary>
    public void RecordOutcome(InboundEvent inboundEvent, Session? before, ApplyOutcome outcome, Session? after)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        var id = inboundEvent.SessionId.Value;

        // Seen, whatever the Stop went on to decide: the type is what the row reports, by count
        // only, so it can be classified later from the causing event's payload (T1.41).
        if (inboundEvent is Stop { UnrecognisedBackgroundTasks: > 0 } unseen)
        {
            Add(new Decision(
                _now,
                id,
                DecisionKind.TaskTypeUnrecognised,
                Reason: nameof(TaskTypeReason.UnrecognisedType),
                Detail: $"count={unseen.UnrecognisedBackgroundTasks}"));
        }

        if (!outcome.Changed())
        {
            Add(new Decision(
                _now,
                id,
                inboundEvent is Ack ? DecisionKind.AckDeclined : DecisionKind.EventDeclined,
                FromState: before?.State.ToString(),
                Reason: outcome.ToString()));

            return;
        }

        if (inboundEvent is Ack ack)
        {
            Add(new Decision(_now, id, DecisionKind.AckApplied, Reason: ack.Source.ToString()));
        }

        if (before is null)
        {
            Add(new Decision(
                _now, id, DecisionKind.SessionAdded, ToState: after?.State.ToString()));
        }
        else if (inboundEvent is SessionStart start)
        {
            // The known matchers by their wire spelling, and "other" for anything else: the raw
            // source is payload text, and payload text never reaches a decision row (T1.24).
            Add(new Decision(
                _now,
                id,
                DecisionKind.SessionRefreshed,
                Reason: start.ParsedSource.ToWireValue() ?? "other"));
        }

        if (before is not null && after is not null && before.State != after.State)
        {
            Add(new Decision(
                _now,
                id,
                after.State == SessionState.Ended ? DecisionKind.SessionEnded : DecisionKind.StateMoved,
                FromState: before.State.ToString(),
                ToState: after.State.ToString(),
                Reason: MeaningOf(inboundEvent, before)?.ToString()));
        }

        if (before is not null && after is not null && before.WorkspaceGroup != after.WorkspaceGroup)
        {
            Add(new Decision(
                _now,
                id,
                DecisionKind.GroupRederived,
                Detail: $"from={before.WorkspaceGroup.Value} to={after.WorkspaceGroup.Value}"));
        }
    }

    /// <summary>
    /// What a prompt that moved a session meant, for the move's <c>reason</c> (T1.41, issue #52),
    /// or null for anything else.
    /// </summary>
    /// <remarks>
    /// Read from the event and the state it left, exactly as the Registry's transition cause is:
    /// a machine prompt is never an acknowledgment; the operator's own prompt is one when the
    /// state it left had something to acknowledge. See <see cref="PromptMeaning"/>.
    /// </remarks>
    private static PromptMeaning? MeaningOf(InboundEvent inboundEvent, Session before) => inboundEvent switch
    {
        UserPromptSubmit { IsMachinePrompt: true } => PromptMeaning.MachinePrompt,
        UserPromptSubmit when Acknowledgment.Applies(before.State) => PromptMeaning.AutoAcknowledgment,
        _ => null,
    };

    /// <summary>The silence sweep moved a session (T1.30's rows, now durable).</summary>
    public void Swept(SilentSession silent) =>
        Add(new Decision(
            _now,
            silent.Session.Id.Value,
            DecisionKind.SilenceSwept,
            FromState: nameof(SessionState.Working),
            ToState: nameof(SessionState.Interrupted),
            Reason: SilenceWatch.Cause,
            Detail: $"silentMinutes={(int)silent.Silence.TotalMinutes}"));

    /// <summary>Applying the event threw; the TYPE is recorded, never the message.</summary>
    public void ApplyFailed(InboundEvent inboundEvent, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);
        ArgumentNullException.ThrowIfNull(exception);

        Add(new Decision(
            _now,
            inboundEvent.SessionId.Value,
            DecisionKind.ApplyFailed,
            Reason: exception.GetType().Name));
    }

    /// <summary>A global sound command was applied (the consumer's own switch).</summary>
    public void SoundCommandApplied(SoundCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        Add(new Decision(
            _now,
            null,
            DecisionKind.MuteApplied,
            Reason: command.Kind.ToString(),
            Detail: command.Until is { } until ? $"until={until:o}" : null));

        // Arm the lapse watch for a timed mute; an explicit unmute disarms it, so a lapse row
        // never follows an unmute the operator already made.
        _muteExpiryArmed = command.Kind == SoundCommandKind.MuteAll ? command.Until : null;
    }

    /// <summary>The operator edited a roster.</summary>
    public void RosterEdited() => Add(new Decision(_now, null, DecisionKind.RosterEdited));

    // ---- The sound engine's own rows (IDecisionSink) ------------------------------------------

    /// <inheritdoc/>
    public void SoundPlayed(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        int rung,
        TimeSpan waited)
    {
        switch (kind)
        {
            case SoundDecisionKind.Notice:
                Add(new Decision(
                    _now, session.Value, DecisionKind.NoticePlayed, Reason: sound.ToString()));
                break;

            case SoundDecisionKind.Nudge:
                Add(new Decision(
                    _now,
                    session.Value,
                    DecisionKind.NudgePlayed,
                    Reason: sound.ToString(),
                    Detail: $"rung={rung} waitedMinutes={(int)waited.TotalMinutes}"));
                break;

            case SoundDecisionKind.GroupNotice:
            case SoundDecisionKind.GroupNudge:
                Add(new Decision(
                    _now,
                    null,
                    DecisionKind.GroupNoticePlayed,
                    Reason: kind == SoundDecisionKind.GroupNudge ? "nudge" : "notice",
                    Detail: $"group={group.Value} members={MembersOf(group)}"));
                break;
        }
    }

    /// <inheritdoc/>
    public void SoundSuppressed(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        SuppressionReason reason) =>
        Add(new Decision(
            _now,
            session.IsEmpty ? null : session.Value,
            DecisionKind.NoticeSuppressed,
            Reason: reason.ToString(),
            Detail: $"kind={kind} sound={sound}"));

    // ---- Rows born on other threads -----------------------------------------------------------

    /// <summary>
    /// Records a decision made off the consumer thread — a channel drop, the tray light, a
    /// surfaced window. Thread-safe; the next scope carries it, with <c>event_id NULL</c>.
    /// </summary>
    public void External(Decision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        _external.Enqueue(decision);
    }

    // ---- Internals ----------------------------------------------------------------------------

    private void Add(Decision decision)
    {
        if (!_inScope)
        {
            // A sink call with no scope open would be a wiring fault; the row is kept rather
            // than lost, riding the next scope like an external one.
            _external.Enqueue(decision);
            return;
        }

        _buffer.Add(decision);
        RecordedCount++;

        // The same row at Debug, so an operator chasing a sound can tail the log with
        // logging.minimumLevel=Debug and read what the table records. Identifiers only.
        _logger.Debug(
            "Decision {Kind} session={SessionId} {FromState}->{ToState} reason={Reason} detail={Detail}",
            decision.Kind,
            decision.SessionId ?? "(none)",
            decision.FromState ?? "-",
            decision.ToState ?? "-",
            decision.Reason ?? "-",
            decision.Detail ?? "-");
    }

    private void DrainExternal()
    {
        while (_external.TryDequeue(out var decision))
        {
            _externalBuffer.Add(decision);
            RecordedCount++;
        }
    }

    /// <summary>The member ids of a roster group, for the group notice's detail.</summary>
    /// <remarks>
    /// The engine knows a roster group only by its key; the ids come from the Registry, read on
    /// the consumer thread at the moment of the notice. Ids, never titles.
    /// </remarks>
    private string MembersOf(GroupKey group) =>
        string.Join(
            ",",
            _registry.Sessions.Values
                .Where(session => GroupKeys.Effective(session, _rosters.Book) == group)
                .Select(session => session.Id.Value)
                .OrderBy(id => id, StringComparer.Ordinal));
}
