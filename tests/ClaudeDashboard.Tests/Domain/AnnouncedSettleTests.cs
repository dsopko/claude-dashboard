using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The engine's rule for a roster group's settle (T1.72, issue #107): silent when every Unread member already
/// announced its finish; played once otherwise, and when in doubt. The pipeline cases are in
/// <c>RosterAnnouncedPipelineTests</c>; these hold the rule in Core, member by member.
/// </summary>
public sealed class AnnouncedSettleTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly GroupKey Workspace = GroupKeys.ForWorkspace(@"C:\w");
    private static readonly GroupKey Roster = GroupKeys.ForRoster("orchestration");
    private static readonly SessionId One = new("s-1");
    private static readonly SessionId Two = new("s-2");

    private readonly RecordingSoundPlayer _player = new();
    private readonly FakeClock _clock = new();
    private readonly Decisions _decisions = new();
    private readonly SoundPolicyEngine _engine;

    public AnnouncedSettleTests() =>
        _engine = new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), new SoundPolicyOptions(), _decisions);

    /// <summary>The settle pass hands the engine the group's Unread members, as the group stands.</summary>
    [Fact]
    public void The_unread_members_are_read_from_the_group_as_it_stands()
    {
        var group = new Group(Roster, [In(SessionState.Unread, "s-1"), In(SessionState.Working, "s-2"), In(SessionState.Unread, "s-3")]);

        Assert.Equal(["s-1", "s-3"], RosterSettle.UnreadMembers(group).Select(id => id.Value));
    }

    /// <summary>
    /// <strong>Every Unread member announced its own finish:</strong> the settle plays nothing, is recorded as
    /// already announced, starts no group reminder, and each member keeps its own reminder.
    /// </summary>
    [Fact]
    public void A_settle_of_members_that_announced_is_silent_and_starts_no_reminder()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-2"), Workspace);
        Assert.Equal(2, Finishes());

        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(1), At, One, [One, Two]);

        Assert.Equal(2, Finishes());
        Assert.Equal([(SoundDecisionKind.GroupNotice, SuppressionReason.AlreadyAnnounced)], _decisions.Suppressed);

        _engine.Evaluate(At.AddMinutes(10));

        Assert.Equal([SoundDecisionKind.Nudge, SoundDecisionKind.Nudge], _decisions.Played.Where(kind => kind != SoundDecisionKind.Notice));
    }

    /// <summary>
    /// <strong>When in doubt, the sound plays:</strong> no members given, an empty list, a member the engine holds
    /// no record of, or a member whose finish went to its roster group.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("given to the group")]
    public void A_settle_plays_when_any_member_is_not_known_to_have_announced(string members)
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-2"), members == "given to the group" ? Roster : Workspace);

        IReadOnlyCollection<SessionId>? given = members switch
        {
            "none" => null,
            "empty" => [],
            "unknown" => [One, new SessionId("s-9")],
            _ => [One, Two],
        };

        var before = Finishes();
        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(1), At, One, given);

        Assert.Equal(before + 1, Finishes());
        Assert.DoesNotContain(_decisions.Suppressed, row => row.Reason == SuppressionReason.AlreadyAnnounced);
    }

    /// <summary>
    /// <strong>A group that announced has told its members' finish:</strong> the same members under a new key
    /// (a roster renamed) settle silently.
    /// </summary>
    [Fact]
    public void A_group_that_announced_marks_its_members_announced()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-2"), Roster);
        Assert.Equal(0, Finishes());

        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(1), At, One, [One, Two]);
        Assert.Equal(1, Finishes());

        _engine.OnRosterGroupSettled(GroupKeys.ForRoster("pair"), At.AddMinutes(2), At, One, [One, Two]);

        Assert.Equal(1, Finishes());
        Assert.Contains((SoundDecisionKind.GroupNotice, SuppressionReason.AlreadyAnnounced), _decisions.Suppressed);
    }

    /// <summary>
    /// <strong>A held-back notice counts as announced</strong> (T1.55's rule: the engine goes on as if it played).
    /// </summary>
    [Fact]
    public void A_notice_held_back_by_a_mute_counts_as_announced()
    {
        _engine.SetAllMuted(true);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);
        _engine.SetAllMuted(false);

        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(1), At, One, [One]);

        Assert.Equal(0, Finishes());
        Assert.Contains((SoundDecisionKind.GroupNotice, SuppressionReason.AlreadyAnnounced), _decisions.Suppressed);
    }

    /// <summary>
    /// <strong>A T1.44 restore keeps the fact:</strong> the session's entry comes back unchanged after a quiet
    /// tick, still announced, so a settle over it is silent. A new entry decides the fact again.
    /// </summary>
    [Fact]
    public void A_restored_entry_keeps_the_fact_and_a_new_entry_decides_it_again()
    {
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Workspace);
        _engine.OnSessionChanged(In(SessionState.Working, "s-1", At.AddMinutes(1)), Workspace);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1"), Roster);

        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(2), At, One, [One]);
        Assert.Equal(1, Finishes());

        // A real new finish inside the roster: given to the group, so the group's next settle plays.
        _engine.OnRosterGroupUnsettled(Roster);
        _engine.OnSessionChanged(In(SessionState.Working, "s-1", At.AddMinutes(3)), Roster);
        _engine.OnSessionChanged(In(SessionState.Unread, "s-1", At.AddMinutes(4)), Roster);
        _engine.OnRosterGroupSettled(Roster, At.AddMinutes(5), At.AddMinutes(4), One, [One]);

        Assert.Equal(2, Finishes());
    }

    private int Finishes() => _player.PlayedOf(SoundId.Finished).Count;

    private static Session In(SessionState state, string id, DateTimeOffset? entered = null) => new()
    {
        Id = new SessionId(id),
        State = state,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
        Cwd = @"C:\w",
        WorkspaceGroup = Workspace,
        EnteredAt = entered ?? At,
        LastActivity = entered ?? At,
        LastHeardAt = entered ?? At,
        Title = id,
    };

    /// <summary>What the engine said it decided: the kinds it played, and the suppressions with their reasons.</summary>
    private sealed class Decisions : IDecisionSink
    {
        public List<SoundDecisionKind> Played { get; } = [];

        public List<(SoundDecisionKind Kind, SuppressionReason Reason)> Suppressed { get; } = [];

        public void SoundPlayed(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, int rung, TimeSpan waited) =>
            Played.Add(kind);

        public void SoundDropped(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, int rung, TimeSpan waited, SoundOutcome outcome) =>
            Played.Add(kind);

        public void SoundSuppressed(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, SuppressionReason reason) =>
            Suppressed.Add((kind, reason));
    }
}
