using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// <strong>Who sounds a session's next reminder, and when</strong> (T1.84, issue #58): the engine's one answer, which
/// <c>/state</c> gives as <c>nextNudgeAt</c> and <c>nudgeBy</c>.
/// </summary>
/// <remarks>
/// Before T1.84 a finished roster member and a session with no reminder coming both answered a null time. Each case
/// here asserts the owner and the time together, so an answer that is right about one and wrong about the other fails.
/// </remarks>
public sealed class NudgeOwnerTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly TimeSpan UnreadAfter = TimeSpan.FromMinutes(2);
    private static readonly GroupKey Workspace = GroupKeys.ForWorkspace(@"C:\w");
    private static readonly GroupKey Roster = GroupKeys.ForRoster("orchestration");
    private static readonly SessionId One = new("s-1");
    private static readonly SessionId Two = new("s-2");

    private readonly RecordingSoundPlayer _player = new();
    private readonly SoundPolicyEngine _engine;

    public NudgeOwnerTests() =>
        _engine = new SoundPolicyEngine(
            _player, new FakeClock(), new SingleWriterGuard(), new SoundPolicyOptions { UnreadNudgeAfter = UnreadAfter });

    /// <summary>A session outside any roster, needing permission, answers its own first nudge.</summary>
    [Fact]
    public void A_session_outside_a_roster_needing_permission_answers_its_own_time()
    {
        _engine.OnSessionChanged(In(SessionState.NeedsPermission, "s-1"), Workspace);

        Assert.Equal(new NextNudge(At + SoundPolicyOptions.DefaultNudgeLadder[0], NudgeOwner.Session), _engine.NextNudgeOf(One));
    }

    /// <summary>
    /// A finished session outside any roster answers its own one nudge, and after that nudge no time, still its own.
    /// </summary>
    [Fact]
    public void A_finished_session_outside_a_roster_answers_no_time_after_its_one_nudge()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);

        Assert.Equal(new NextNudge(At + UnreadAfter, NudgeOwner.Session), _engine.NextNudgeOf(One));

        _engine.Evaluate(At + UnreadAfter);

        Assert.Equal(new NextNudge(null, NudgeOwner.Session), _engine.NextNudgeOf(One));
    }

    /// <summary>
    /// <strong>A finished roster member's reminder is the group's</strong>: no time before the group settles, the
    /// group's time after the settle, and no time once the group's one nudge has played. The owner is the group
    /// throughout, so each null reads one way: the group has nothing scheduled.
    /// </summary>
    [Fact]
    public void A_finished_roster_member_answers_the_group_time_through_the_settle_and_the_nudge()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-2"), Roster);

        Assert.Equal(new NextNudge(null, NudgeOwner.Group), _engine.NextNudgeOf(One));

        var settledAt = At.AddSeconds(2);
        _engine.OnRosterGroupSettled(Roster, settledAt, At, Two, [One, Two]);

        Assert.Equal(new NextNudge(settledAt + UnreadAfter, NudgeOwner.Group), _engine.NextNudgeOf(One));
        Assert.Equal(new NextNudge(settledAt + UnreadAfter, NudgeOwner.Group), _engine.NextNudgeOf(Two));

        // The member's own schedule has nothing: the group's time is not its own.
        Assert.Null(_engine.NextNudgeAt(One));

        _engine.Evaluate(settledAt + UnreadAfter);

        Assert.Equal(new NextNudge(null, NudgeOwner.Group), _engine.NextNudgeOf(One));
    }

    /// <summary>
    /// A roster member needing permission sounds its own notice and its own ladder (T1.25), so it answers its own time.
    /// </summary>
    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    public void A_roster_member_needing_you_answers_its_own_time(SessionState state)
    {
        _engine.OnSessionChanged(In(state, "s-1"), Roster);

        Assert.Equal(new NextNudge(At + SoundPolicyOptions.DefaultNudgeLadder[0], NudgeOwner.Session), _engine.NextNudgeOf(One));
    }

    /// <summary>
    /// <strong>A roster made over members that each announced their own finish</strong> (T1.72): each keeps its own
    /// reminder, before and after the group's silent settle, so each answers its own time.
    /// </summary>
    [Fact]
    public void Members_that_announced_their_own_finish_answer_their_own_time_in_a_roster()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-2"), Workspace);

        // The roster is made: the same entries, now in the roster group.
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-2"), Roster);

        var own = new NextNudge(At + UnreadAfter, NudgeOwner.Session);

        Assert.Equal(own, _engine.NextNudgeOf(One));
        Assert.Equal(own, _engine.NextNudgeOf(Two));

        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(1), At, One, [One, Two]);

        Assert.Equal(own, _engine.NextNudgeOf(One));
        Assert.Equal(own, _engine.NextNudgeOf(Two));
    }

    /// <summary>
    /// A member whose finish went to its roster group, and that then left the roster, has no group to sound it and
    /// none of its own: no time, its own.
    /// </summary>
    [Fact]
    public void A_member_that_left_its_roster_answers_its_own_schedule()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);

        Assert.Equal(new NextNudge(null, NudgeOwner.Session), _engine.NextNudgeOf(One));
    }

    /// <summary>A session the engine does not hold answers no time, its own.</summary>
    [Fact]
    public void A_session_never_seen_answers_no_time_of_its_own() =>
        Assert.Equal(new NextNudge(null, NudgeOwner.Session), _engine.NextNudgeOf(new SessionId("never-seen")));

    /// <summary>
    /// <strong>Mute stays out of the answer</strong> (the T1.84 ruling): the time is the schedule, which a mute leaves
    /// advancing, not what is heard.
    /// </summary>
    [Fact]
    public void A_mute_does_not_change_the_answer()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);
        _engine.OnRosterGroupSettled(Roster, At, At, One, [One]);
        var before = _engine.NextNudgeOf(One);

        _engine.SetGroupMuted(Roster, muted: true);
        _engine.SetSessionMuted(One, muted: true);
        _engine.SetAllMuted(muted: true);

        Assert.Equal(before, _engine.NextNudgeOf(One));
        Assert.Equal(NudgeOwner.Group, before.By);
        Assert.NotNull(before.At);
    }

    /// <summary>
    /// <strong>A roster group's nudge raises <see cref="SoundPolicyEngine.NudgeScheduleAdvanced"/></strong>: its
    /// members' time moved to null with no session change, so <c>/state</c> must be built again.
    /// </summary>
    [Fact]
    public void A_group_nudge_raises_the_schedule_advanced_event()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);
        _engine.OnRosterGroupSettled(Roster, At, At, One, [One]);

        var raised = 0;
        _engine.NudgeScheduleAdvanced += (_, _) => raised++;

        _engine.Evaluate(At + UnreadAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(0, raised);

        _engine.Evaluate(At + UnreadAfter);
        Assert.Equal(1, raised);
    }

    private static Session In(SessionState state, string id) => new()
    {
        Id = new SessionId(id),
        State = state,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
        Cwd = @"C:\w",
        WorkspaceGroup = Workspace,
        EnteredAt = At,
        LastActivity = At,
        LastHeardAt = At,
        Title = id,
    };
}
