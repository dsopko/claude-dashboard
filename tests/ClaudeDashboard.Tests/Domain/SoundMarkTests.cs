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
    /// <strong>A group's notice marks the member whose finish settled the group</strong>: the one whose
    /// entry instant is the group's quiet instant. Its reminder marks the same member.
    /// </summary>
    [Fact]
    public void A_group_sound_marks_the_member_whose_finish_settled_it()
    {
        var engine = Engine(new SoundPolicyOptions { UnreadNudgeAfter = TimeSpan.FromMinutes(2) });

        engine.OnSessionChanged(In(SessionState.Unread, "s-1", At), RosterKey);
        engine.OnSessionChanged(In(SessionState.Unread, "s-3", At.AddSeconds(9)), RosterKey);
        engine.OnSessionChanged(In(SessionState.Unread, "s-2", At.AddSeconds(4)), RosterKey);

        Assert.Empty(_marks);

        _clock.Now = At.AddSeconds(11);
        engine.OnRosterGroupSettled(RosterKey, _clock.Now, quietSince: At.AddSeconds(9));

        var notice = Assert.Single(_marks);
        Assert.Equal(new SessionId("s-3"), notice.Session);
        Assert.Equal(SoundId.Finished, notice.Sound);

        _clock.Now = At.AddMinutes(3);
        engine.Evaluate(_clock.Now);

        Assert.Equal(2, _marks.Count);
        Assert.Equal(new SessionId("s-3"), _marks[1].Session);
        Assert.Equal(At.AddMinutes(3), _marks[1].At);
    }

    /// <summary>
    /// A group whose settling member is no longer in it, or a settle with no quiet instant, plays its
    /// sound and marks no row.
    /// </summary>
    [Fact]
    public void A_group_sound_with_no_matching_member_marks_no_row()
    {
        var engine = Engine();

        engine.OnSessionChanged(In(SessionState.Unread, "s-1", At), RosterKey);

        // Nobody entered at this instant.
        engine.OnRosterGroupSettled(RosterKey, At.AddSeconds(2), quietSince: At.AddSeconds(1));

        // And a settle that says nothing about when the group went quiet.
        var other = GroupKeys.ForRoster("other");
        engine.OnSessionChanged(In(SessionState.Unread, "s-9", At), other);
        engine.OnRosterGroupSettled(other, At.AddSeconds(2));

        Assert.Equal(2, _player.PlayedOf(SoundId.Finished).Count);
        Assert.Empty(_marks);
    }

    /// <summary>Two members that entered at the same instant: the lower id, ordinal, so the answer is stable.</summary>
    [Fact]
    public void A_tie_goes_to_the_lower_id()
    {
        var engine = Engine();

        engine.OnSessionChanged(In(SessionState.Unread, "s-b", At), RosterKey);
        engine.OnSessionChanged(In(SessionState.Unread, "s-a", At), RosterKey);
        engine.OnRosterGroupSettled(RosterKey, At.AddSeconds(2), quietSince: At);

        Assert.Equal(new SessionId("s-a"), Assert.Single(_marks).Session);
    }

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
