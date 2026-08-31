using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Sound for a roster group: the group is the unit for <em>done</em> and for nothing else
/// (T1.25, issue #16).
/// </summary>
public sealed class RosterSoundTests
{
    private const string Orchestration = "orchestration";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly GroupKey RosterKey = GroupKeys.ForRoster(Orchestration);

    private readonly RecordingSoundPlayer _player = new();
    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SoundPolicyEngine _engine;

    public RosterSoundTests() =>
        _engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions());

    /// <summary>
    /// <strong>Members finishing one at a time produce ONE done sound, at the settle.</strong>
    /// </summary>
    /// <remarks>
    /// The count before the settle is asserted as well as the count after. "One sound fired" alone
    /// would pass if each member had sounded and the group had not, which is the noise this rule
    /// exists to remove — three agents handing work round would chime three times a turn.
    /// </remarks>
    [Fact]
    public void Members_finishing_one_at_a_time_produce_one_done_sound()
    {
        _engine.OnSessionChanged(Finished("s-1"), RosterKey);
        _engine.OnSessionChanged(Finished("s-2"), RosterKey);
        _engine.OnSessionChanged(Finished("s-3"), RosterKey);

        Assert.DoesNotContain(_player.Played, p => p.Sound == SoundId.Finished);

        _engine.OnRosterGroupSettled(RosterKey, At);

        Assert.Single(_player.Played, p => p.Sound == SoundId.Finished);
    }

    /// <summary>A session in no roster still sounds its own done notice.</summary>
    /// <remarks>
    /// The control for the test above: without it, a suppression that silenced <em>every</em>
    /// finished notice would look identical.
    /// </remarks>
    [Fact]
    public void An_ungrouped_session_still_sounds_its_own_done_notice()
    {
        var session = Finished("s-1");

        _engine.OnSessionChanged(session, session.WorkspaceGroup);

        Assert.Single(_player.Played, p => p.Sound == SoundId.Finished);
    }

    /// <summary>
    /// <strong>Permission, question and error still sound immediately, grouped or not.</strong>
    /// </summary>
    /// <remarks>
    /// Suppressing these because a sibling happens to be working would hide exactly what this
    /// product exists to surface — and unlike "done", they are about that member and nobody else.
    /// </remarks>
    [Theory]
    // SoundId is a struct, so it cannot be an attribute argument; the state names the sound.
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    public void An_attention_state_sounds_immediately_even_in_a_roster(SessionState state)
    {
        var expected = state switch
        {
            SessionState.NeedsPermission => SoundId.Permission,
            SessionState.NeedsQuestion => SoundId.Question,
            _ => SoundId.Error,
        };

        _engine.OnSessionChanged(In(state, "s-1"), RosterKey);

        Assert.Equal(expected, Assert.Single(_player.Played).Sound);
    }

    /// <summary>A roster member does not nudge on its own unread result either.</summary>
    /// <remarks>
    /// The reminder belongs to the group for the same reason the notice does. Nudging per member
    /// would reinstate the noise the suppression removes, one step later — and the operator would
    /// hear a chime per agent for a turn that produced one result.
    /// </remarks>
    [Fact]
    public void A_roster_member_does_not_nudge_on_its_own_result()
    {
        var options = new SoundPolicyOptions { UnreadNudgeAfter = TimeSpan.FromMinutes(2) };
        var engine = new SoundPolicyEngine(_player, _clock, _guard, options);

        engine.OnSessionChanged(Finished("s-1"), RosterKey);

        Assert.Null(engine.NextNudgeAt(new SessionId("s-1")));

        engine.Evaluate(At.AddHours(1));

        Assert.Empty(_player.Played);
    }

    /// <summary>A settled group nudges once, as one, and then stops.</summary>
    /// <remarks>
    /// TS §IV.5 gives an unread result at most one soft nudge, and a settled group is one unread
    /// result however many members produced it. The second evaluation is what proves it stops.
    /// </remarks>
    [Fact]
    public void A_settled_group_nudges_once_and_then_stops()
    {
        var options = new SoundPolicyOptions { UnreadNudgeAfter = TimeSpan.FromMinutes(2) };
        var engine = new SoundPolicyEngine(_player, _clock, _guard, options);

        engine.OnRosterGroupSettled(RosterKey, At);

        // One for the notice. Counts rather than a cleared recorder, so the notice stays visible
        // in the same assertion as the nudge and neither can be silently lost.
        Assert.Single(_player.Played);

        engine.Evaluate(At.AddMinutes(3));
        Assert.Equal(2, _player.Played.Count);

        engine.Evaluate(At.AddHours(1));
        Assert.Equal(2, _player.Played.Count);
    }

    /// <summary>A group that goes back to work stops nudging, and says nothing about it.</summary>
    [Fact]
    public void An_unsettled_group_stops_nudging_silently()
    {
        var options = new SoundPolicyOptions { UnreadNudgeAfter = TimeSpan.FromMinutes(2) };
        var engine = new SoundPolicyEngine(_player, _clock, _guard, options);

        engine.OnRosterGroupSettled(RosterKey, At);
        Assert.Single(_player.Played);

        engine.OnRosterGroupUnsettled(RosterKey);

        // Unsettling says nothing of its own …
        Assert.Single(_player.Played);

        engine.Evaluate(At.AddHours(1));

        // … and there is no nudge left to fire.
        Assert.Single(_player.Played);
    }

    /// <summary>Settling a group that is already settled sounds nothing a second time.</summary>
    [Fact]
    public void Settling_twice_sounds_once()
    {
        _engine.OnRosterGroupSettled(RosterKey, At);
        _engine.OnRosterGroupSettled(RosterKey, At.AddSeconds(5));

        Assert.Single(_player.Played);
    }

    /// <summary>
    /// <strong>Muting the group silences its done chime; muting one member does not.</strong>
    /// </summary>
    /// <remarks>
    /// The notice belongs to the group, so the group's mute is the one that applies to it. A member
    /// mute silencing the whole group's result would be the operator quieting one agent and losing
    /// the answer for all three.
    /// </remarks>
    [Fact]
    public void The_group_mute_silences_the_group_notice_and_a_member_mute_does_not()
    {
        _engine.SetSessionMuted(new SessionId("s-1"), muted: true);
        _engine.OnRosterGroupSettled(RosterKey, At);

        Assert.Single(_player.Played);

        _engine.OnRosterGroupUnsettled(RosterKey);

        _engine.SetGroupMuted(RosterKey, muted: true);
        _engine.OnRosterGroupSettled(RosterKey, At);

        // Still the one from before: the group mute stopped the second notice.
        Assert.Single(_player.Played);
    }

    /// <summary>A settled group needs a key.</summary>
    [Fact]
    public void A_settled_group_needs_a_key()
    {
        Assert.Throws<ArgumentException>(() => _engine.OnRosterGroupSettled(default, At));
    }

    private static Session Finished(string id) => In(SessionState.Unread, id);

    private static Session In(SessionState state, string id) => new()
    {
        Id = new SessionId(id),
        State = state,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
        Cwd = @"C:\w",
        WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\w"),
        EnteredAt = At,
        LastActivity = At,
        LastHeardAt = At,
        Title = id,
    };
}
