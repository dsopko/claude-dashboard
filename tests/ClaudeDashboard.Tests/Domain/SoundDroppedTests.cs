using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// A sound the player dropped is recorded as dropped, and the sound rules do not change (T1.55,
/// issue #72).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The record tells the truth</strong> (the operator's ruling of 2026-10-03). A dropped
/// notice, nudge or group sound becomes <see cref="IDecisionSink.SoundDropped"/>, with the reason,
/// and never <see cref="IDecisionSink.SoundPlayed"/>. A queued one is played, as before.
/// </para>
/// <para>
/// <strong>The rules do not change.</strong> A dropped notice still counts as announced and the
/// ladder still advances, so the schedule with no output is the schedule with one. Nothing is
/// replayed when a device returns.
/// </para>
/// </remarks>
public sealed class SoundDroppedTests
{
    private const string Cwd = @"C:\projects\dashboard";

    private static readonly DateTimeOffset Start = FakeClock.DefaultStart;
    private static readonly GroupKey RosterKey = GroupKeys.ForRoster("orchestration");

    private readonly RecordingSoundPlayer _player = new();
    private readonly FakeClock _clock = new();
    private readonly SoundLog _log = new();
    private readonly SoundPolicyEngine _engine;

    public SoundDroppedTests() =>
        _engine = new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), new SoundPolicyOptions(), _log);

    [Theory]
    [InlineData(SoundOutcome.NoOutput)]
    [InlineData(SoundOutcome.Failed)]
    public void A_dropped_notice_is_recorded_dropped_and_not_played(SoundOutcome outcome)
    {
        _player.Outcome = outcome;

        _engine.ChangedInWorkspaceGroup(SessionIn(SessionState.NeedsPermission, Start));

        var dropped = Assert.Single(_log.Dropped);
        Assert.Equal(SoundDecisionKind.Notice, dropped.Kind);
        Assert.Equal(SoundId.Permission, dropped.Sound);
        Assert.Equal(outcome, dropped.Outcome);
        Assert.Empty(_log.Played);
    }

    /// <summary>The control: with output, the notice is played, as before T1.55.</summary>
    [Fact]
    public void A_queued_notice_is_recorded_played()
    {
        _engine.ChangedInWorkspaceGroup(SessionIn(SessionState.NeedsPermission, Start));

        Assert.Single(_log.Played, entry => entry.Kind == SoundDecisionKind.Notice);
        Assert.Empty(_log.Dropped);
    }

    [Fact]
    public void A_dropped_nudge_is_recorded_dropped_with_its_rung()
    {
        _engine.ChangedInWorkspaceGroup(SessionIn(SessionState.NeedsPermission, Start));
        _player.Outcome = SoundOutcome.NoOutput;

        _engine.Evaluate(Start.AddMinutes(2));

        var dropped = Assert.Single(_log.Dropped);
        Assert.Equal(SoundDecisionKind.Nudge, dropped.Kind);
        Assert.Equal(0, dropped.Rung);
        Assert.Equal(TimeSpan.FromMinutes(2), dropped.Waited);
        Assert.DoesNotContain(_log.Played, entry => entry.Kind == SoundDecisionKind.Nudge);
    }

    [Fact]
    public void A_dropped_group_sound_is_recorded_dropped_for_the_group()
    {
        _player.Outcome = SoundOutcome.NoOutput;
        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start, "s-1"), RosterKey);

        _engine.OnRosterGroupSettled(RosterKey, Start);

        var dropped = Assert.Single(_log.Dropped);
        Assert.Equal(SoundDecisionKind.GroupNotice, dropped.Kind);
        Assert.Equal(RosterKey, dropped.Group);
        Assert.Empty(_log.Played);
    }

    /// <summary>
    /// <strong>The nudge schedule is the same with output and without</strong>: a dropped sound
    /// advances the ladder exactly as a queued one does, and the engine asks for the same sounds.
    /// </summary>
    [Fact]
    public void The_nudge_schedule_is_the_same_with_output_and_without()
    {
        var withOutput = Schedule(SoundOutcome.Queued);
        var without = Schedule(SoundOutcome.NoOutput);

        Assert.Equal(withOutput.DueTimes, without.DueTimes);
        Assert.Equal(withOutput.Asked, without.Asked);

        // Not vacuous: the ladder did move, past several rungs.
        Assert.True(withOutput.DueTimes.Distinct().Count() > 3);
        Assert.True(withOutput.Asked.Count > 3);
    }

    /// <summary>A session in Needs You — Permission for 40 minutes, evaluated every minute.</summary>
    private static (List<DateTimeOffset?> DueTimes, List<SoundId> Asked) Schedule(SoundOutcome outcome)
    {
        var player = new RecordingSoundPlayer { Outcome = outcome };
        var engine = new SoundPolicyEngine(player, new FakeClock(), new SingleWriterGuard(), new SoundPolicyOptions());
        var session = SessionIn(SessionState.NeedsPermission, Start);

        engine.ChangedInWorkspaceGroup(session);

        var due = new List<DateTimeOffset?> { engine.NextNudgeAt(session.Id) };

        for (var minute = 1; minute <= 40; minute++)
        {
            engine.Evaluate(Start.AddMinutes(minute));
            due.Add(engine.NextNudgeAt(session.Id));
        }

        return (due, [.. player.Played.Select(played => played.Sound)]);
    }

    private static Session SessionIn(SessionState state, DateTimeOffset enteredAt, string id = "s-1")
    {
        var sessionId = new SessionId(id);

        return new Session
        {
            Id = sessionId,
            State = state,
            Latest = new Exchange { Prompt = "p", StartedAt = enteredAt },
            Cwd = Cwd,
            WorkspaceGroup = GroupKeys.ForSession(Cwd, sessionId),
            EnteredAt = enteredAt,
            LastActivity = enteredAt,
            LastHeardAt = enteredAt,
        };
    }

    private sealed record Entry(
        SoundDecisionKind Kind, SessionId Session, GroupKey Group, SoundId Sound, int Rung, TimeSpan Waited, SoundOutcome Outcome);

    /// <summary>The engine's sound decisions, kept as they were reported.</summary>
    private sealed class SoundLog : IDecisionSink
    {
        public List<Entry> Played { get; } = [];

        public List<Entry> Dropped { get; } = [];

        public void SoundPlayed(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, int rung, TimeSpan waited) =>
            Played.Add(new Entry(kind, session, group, sound, rung, waited, SoundOutcome.Queued));

        public void SoundDropped(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, int rung, TimeSpan waited, SoundOutcome outcome) =>
            Dropped.Add(new Entry(kind, session, group, sound, rung, waited, outcome));

        public void SoundSuppressed(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, SuppressionReason reason)
        {
        }
    }
}
