using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Which row a sound marks, as the engine decides it (T1.67, issue #99): only a queued sound, a
/// session's sound on its own row, and a group's sound on the member whose finish settled it.
/// </summary>
public sealed class SoundMarkTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly GroupKey RosterKey = GroupKeys.ForRoster("orchestration");

    private readonly RecordingSoundPlayer _player = new();
    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly List<SoundMarkedEventArgs> _marks = [];

    private SoundPolicyEngine Engine(SoundPolicyOptions? options = null)
    {
        var engine = new SoundPolicyEngine(_player, _clock, _guard, options ?? new SoundPolicyOptions());
        engine.SoundMarked += (_, e) => _marks.Add(e);
        return engine;
    }

    /// <summary>A notice the player queued marks its own session, with the sound and the clock's instant.</summary>
    [Fact]
    public void A_queued_notice_marks_its_session()
    {
        var engine = Engine();
        _clock.Now = At.AddSeconds(7);

        engine.ChangedInWorkspaceGroup(In(SessionState.NeedsPermission, "s-1", At));

        var mark = Assert.Single(_marks);
        Assert.Equal(new SessionId("s-1"), mark.Session);
        Assert.Equal(SoundId.Permission, mark.Sound);
        Assert.Equal(At.AddSeconds(7), mark.At);
    }

    /// <summary>
    /// <strong>A suppressed sound marks nothing:</strong> a muted session, a muted group, everything
    /// muted, and monitoring paused. The decisions record still says why; the row says nothing.
    /// </summary>
    [Theory]
    [InlineData("session")]
    [InlineData("group")]
    [InlineData("all")]
    [InlineData("paused")]
    public void A_suppressed_sound_marks_no_row(string how)
    {
        var engine = Engine();
        var session = In(SessionState.NeedsQuestion, "s-1", At);

        switch (how)
        {
            case "session":
                engine.SetSessionMuted(session.Id, muted: true);
                break;
            case "group":
                engine.SetGroupMuted(session.WorkspaceGroup, muted: true);
                break;
            case "all":
                engine.SetAllMuted(muted: true);
                break;
            default:
                engine.SetMonitoringPaused(paused: true);
                break;
        }

        engine.ChangedInWorkspaceGroup(session);

        Assert.Empty(_player.Played);
        Assert.Empty(_marks);
    }

    /// <summary>
    /// An entry already announced, back unchanged after a quiet tick (T1.44), is suppressed, and
    /// marks nothing the second time.
    /// </summary>
    [Fact]
    public void An_entry_already_announced_marks_no_row_again()
    {
        var engine = Engine();
        var finished = In(SessionState.Unread, "s-1", At);

        engine.ChangedInWorkspaceGroup(finished);
        engine.ChangedInWorkspaceGroup(In(SessionState.Working, "s-1", At.AddSeconds(5)));
        _marks.Clear();

        engine.ChangedInWorkspaceGroup(finished);

        Assert.Empty(_marks);
    }

    /// <summary>A dropped sound (no output, or failed) made no noise, so it marks nothing.</summary>
    [Theory]
    [InlineData(SoundOutcome.NoOutput)]
    [InlineData(SoundOutcome.Failed)]
    public void A_dropped_sound_marks_no_row(SoundOutcome outcome)
    {
        var engine = Engine();
        _player.Outcome = outcome;

        engine.ChangedInWorkspaceGroup(In(SessionState.Error, "s-1", At));

        Assert.Single(_player.Played);
        Assert.Empty(_marks);
    }

    /// <summary>
    /// <strong>A group's notice marks the member that the settle named</strong>, and its reminder
    /// marks the same member. The engine does not look for the member itself (the T1.67 review).
    /// </summary>
    [Fact]
    public void A_group_sound_marks_the_member_the_settle_named()
    {
        var engine = Engine(new SoundPolicyOptions { UnreadNudgeAfter = TimeSpan.FromMinutes(2) });

        engine.OnSessionChanged(In(SessionState.Unread, "s-1", At), RosterKey);
        engine.OnSessionChanged(In(SessionState.Unread, "s-3", At.AddSeconds(9)), RosterKey);

        Assert.Empty(_marks);

        _clock.Now = At.AddSeconds(11);
        engine.OnRosterGroupSettled(RosterKey, _clock.Now, quietSince: At.AddSeconds(9), settledBy: new SessionId("s-3"));

        var notice = Assert.Single(_marks);
        Assert.Equal(new SessionId("s-3"), notice.Session);
        Assert.Equal(SoundId.Finished, notice.Sound);

        _clock.Now = At.AddMinutes(3);
        engine.Evaluate(_clock.Now);

        Assert.Equal(2, _marks.Count);
        Assert.Equal(new SessionId("s-3"), _marks[1].Session);
        Assert.Equal(At.AddMinutes(3), _marks[1].At);
    }

    /// <summary>A settle that names no member plays the group's sound and marks no row.</summary>
    [Fact]
    public void A_settle_that_names_no_member_marks_no_row()
    {
        var engine = Engine();

        engine.OnSessionChanged(In(SessionState.Unread, "s-1", At), RosterKey);
        engine.OnRosterGroupSettled(RosterKey, At.AddSeconds(2), quietSince: At);

        Assert.Single(_player.PlayedOf(SoundId.Finished));
        Assert.Empty(_marks);
    }

    /// <summary>
    /// A quiet tick unsettles the group and settles it again at the same quiet instant (T1.44): no
    /// new sound, no new mark, and the reminder still marks the member the first settle named.
    /// </summary>
    [Fact]
    public void A_quiet_tick_keeps_the_member_the_first_settle_named()
    {
        var engine = Engine(new SoundPolicyOptions { UnreadNudgeAfter = TimeSpan.FromMinutes(2) });

        engine.OnSessionChanged(In(SessionState.Unread, "s-2", At), RosterKey);
        engine.OnRosterGroupSettled(RosterKey, At.AddSeconds(2), quietSince: At, settledBy: new SessionId("s-2"));
        engine.OnRosterGroupUnsettled(RosterKey);

        // The settle comes back with no member named: the restored settle keeps its own.
        engine.OnRosterGroupSettled(RosterKey, At.AddSeconds(30), quietSince: At);

        Assert.Single(_marks);

        _clock.Now = At.AddMinutes(3);
        engine.Evaluate(_clock.Now);

        Assert.Equal(2, _marks.Count);
        Assert.Equal(new SessionId("s-2"), _marks[1].Session);
    }

    /// <summary>
    /// <see cref="RosterSettle.SettledBy"/> names the member whose entry instant is the group's quiet
    /// instant, read from the group as it stands.
    /// </summary>
    [Fact]
    public void SettledBy_names_the_member_that_entered_last()
    {
        var group = Roster(
            In(SessionState.Unread, "s-1", At),
            In(SessionState.Unread, "s-3", At.AddSeconds(9)),
            In(SessionState.Acked, "s-2", At.AddSeconds(4)));

        Assert.Equal(new SessionId("s-3"), RosterSettle.SettledBy(group));
    }

    /// <summary>A member that ended last is the one that set off the sound, so it is named (the ruling on #99).</summary>
    [Fact]
    public void SettledBy_names_a_member_that_ended_last()
    {
        var group = Roster(
            In(SessionState.Unread, "s-1", At.AddSeconds(60)),
            In(SessionState.Ended, "s-2", At.AddSeconds(120)));

        Assert.Equal(new SessionId("s-2"), RosterSettle.SettledBy(group));
    }

    /// <summary>Two members that entered at the same instant: the lower id, ordinal, so the answer is stable.</summary>
    [Fact]
    public void A_tie_goes_to_the_lower_id()
    {
        var group = Roster(In(SessionState.Unread, "s-b", At), In(SessionState.Unread, "s-a", At));

        Assert.Equal(new SessionId("s-a"), RosterSettle.SettledBy(group));
    }

    private static Group Roster(params Session[] members) =>
        GroupResolver.Resolve(members, RosterBook.From([("orchestration", members.Select(member => member.Title!))]))
            .Single(group => group.Key == RosterKey);

    /// <summary>A nudge marks its session again, at the nudge's instant.</summary>
    [Fact]
    public void A_nudge_marks_its_session_again()
    {
        var engine = Engine(new SoundPolicyOptions { NudgeLadder = [TimeSpan.FromSeconds(50)] });

        engine.ChangedInWorkspaceGroup(In(SessionState.NeedsPermission, "s-1", At));
        _clock.Now = At.AddSeconds(50);
        engine.Evaluate(_clock.Now);

        Assert.Equal(2, _marks.Count);
        Assert.All(_marks, mark => Assert.Equal(new SessionId("s-1"), mark.Session));
        Assert.Equal(At.AddSeconds(50), _marks[1].At);
    }

    private static Session In(SessionState state, string id, DateTimeOffset enteredAt) => new()
    {
        Id = new SessionId(id),
        State = state,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
        Cwd = @"C:\w",
        WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\w"),
        EnteredAt = enteredAt,
        LastActivity = enteredAt,
        LastHeardAt = enteredAt,
        Title = id,
    };
}
