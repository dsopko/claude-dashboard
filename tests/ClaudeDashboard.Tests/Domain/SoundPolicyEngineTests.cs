using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// TS §IV.5's sound policy.
/// </summary>
/// <remarks>
/// <para>
/// Two test-blindness patterns govern how these assertions are written, because this engine is
/// more exposed to both than anything else built so far.
/// </para>
/// <para>
/// <strong>A sound firing is not evidence.</strong> Asserting "a nudge played" stays green
/// under a mutation that fires at the wrong instant, the wrong gain, the wrong count, or for
/// the wrong session. Every assertion here pins the exact payload and the exact count, the
/// ladder is pinned as a full sequence of firing instants rather than as three-nudges-happened,
/// and every boundary is checked from <em>both</em> sides — advanced to just before it with
/// nothing expected, then just after it with exactly one thing expected.
/// </para>
/// <para>
/// <strong>A notice and a nudge both end in <see cref="ISoundPlayer.Play"/>.</strong> "A sound
/// played" cannot tell which produced it, and they carry the same <see cref="SoundId"/> by
/// design. They are distinguished here by payload — a notice is full gain with no fade, a
/// nudge is lower gain with a fade-in — via <see cref="Notices"/> and <see cref="Nudges"/>,
/// which every assertion goes through.
/// </para>
/// </remarks>
public sealed class SoundPolicyEngineTests
{
    private static readonly DateTimeOffset Start = FakeClock.DefaultStart;
    private const string Cwd = @"C:\projects\dashboard";

    /// <summary>Minutes after entry at which the default ladder's first three nudges fall due.</summary>
    private static readonly double[] LadderBoundaries = [2.0, 7.0, 17.0];

    private readonly RecordingSoundPlayer _player = new();
    private readonly FakeClock _clock = new();
    private readonly SoundPolicyEngine _engine;

    public SoundPolicyEngineTests() => _engine = new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), new SoundPolicyOptions());

    // ---- Distinguishing the two kinds of sound ------------------------------------------------

    /// <summary>Full gain, no fade — the first sound for an event.</summary>
    private IReadOnlyList<PlayedSound> Notices =>
        [.. _player.Played.Where(p => p.Gain == SoundPolicyOptions.DefaultNoticeGain && p.Fade == TimeSpan.Zero)];

    /// <summary>Softer, with a fade-in — a reminder.</summary>
    private IReadOnlyList<PlayedSound> Nudges =>
        [.. _player.Played.Where(p => p.Gain == SoundPolicyOptions.DefaultNudgeGain && p.Fade > TimeSpan.Zero)];

    /// <summary>Every recorded call is one or the other, never neither.</summary>
    private void AssertNothingUnclassified() =>
        Assert.Equal(_player.Played.Count, Notices.Count + Nudges.Count);

    private static Session SessionIn(
        SessionState state,
        DateTimeOffset enteredAt,
        string id = "s-1",
        string cwd = Cwd)
    {
        var sessionId = new SessionId(id);
        return new Session
        {
            Id = sessionId,
            State = state,
            Latest = new Exchange { Prompt = "p", StartedAt = enteredAt },
            Cwd = cwd,
            WorkspaceGroup = GroupKeys.ForSession(cwd, sessionId),
            EnteredAt = enteredAt,
            LastActivity = enteredAt,
        };
    }

    /// <summary>Advances to <paramref name="minutes"/> after <see cref="Start"/> and evaluates.</summary>
    private void EvaluateAt(double minutes) => _engine.Evaluate(Start.AddMinutes(minutes));

    // ---- Notices on state entry ----------------------------------------------------------------

    [Theory]
    [InlineData(SessionState.Unread, "finished")]
    [InlineData(SessionState.NeedsPermission, "permission")]
    [InlineData(SessionState.NeedsQuestion, "question")]
    [InlineData(SessionState.Error, "error")]
    public void A_notice_fires_on_entry_with_the_sound_for_that_state(SessionState state, string expected)
    {
        _engine.OnSessionChanged(SessionIn(state, Start));

        var notice = Assert.Single(Notices);
        Assert.Equal(expected, notice.Sound.Name);
        Assert.Equal(SoundPolicyOptions.DefaultNoticeGain, notice.Gain);
        Assert.Equal(TimeSpan.Zero, notice.Fade);
        Assert.Empty(Nudges);
        AssertNothingUnclassified();
    }

    /// <summary>The four sounds are distinct — the existing sound language (TS §IV.5).</summary>
    [Fact]
    public void The_four_notices_are_four_different_sounds()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start, "s-1"));
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start, "s-2"));
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsQuestion, Start, "s-3"));
        _engine.OnSessionChanged(SessionIn(SessionState.Error, Start, "s-4"));

        Assert.Equal(4, Notices.Select(n => n.Sound).Distinct().Count());
    }

    [Theory]
    [InlineData(SessionState.Working)]
    [InlineData(SessionState.Acked)]
    [InlineData(SessionState.Ended)]
    public void States_that_want_nothing_announce_nothing(SessionState state)
    {
        _engine.OnSessionChanged(SessionIn(state, Start));

        Assert.Empty(_player.Played);
    }

    /// <summary>
    /// A change that did not move the session to a new state is not an entry. T1.2 advances
    /// <c>EnteredAt</c> only on a real state change, which is what makes this decidable.
    /// </summary>
    [Fact]
    public void A_change_that_is_not_an_entry_announces_nothing()
    {
        var blocked = SessionIn(SessionState.NeedsPermission, Start);
        _engine.OnSessionChanged(blocked);
        _player.Clear();

        // Same state, same entry instant — a directory move, say.
        _engine.OnSessionChanged(blocked with { Cwd = @"C:\elsewhere", LastActivity = Start.AddMinutes(1) });

        Assert.Empty(_player.Played);
    }

    [Fact]
    public void Re_entering_a_state_later_announces_again()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start));
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start.AddMinutes(30)));

        Assert.Single(Notices);
    }

    // ---- The nudge ladder: 2 -> 5 -> 10 -----------------------------------------------------------

    /// <summary>
    /// The ladder pinned as a full sequence of firing instants, not as a count. TS §IV.5's
    /// widening intervals put the nudges 2, then 5, then 10 minutes apart — so from entry they
    /// land at 2, 7 and 17 minutes.
    /// </summary>
    [Fact]
    public void The_nudge_ladder_fires_at_two_then_seven_then_seventeen_minutes()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        var firedAt = new List<double>();
        for (var minute = 0.0; minute <= 20.0; minute += 0.5)
        {
            var before = Nudges.Count;
            EvaluateAt(minute);
            if (Nudges.Count > before)
            {
                firedAt.Add(minute);
            }
        }

        Assert.Equal([2.0, 7.0, 17.0], firedAt);
    }

    /// <summary>The widest interval repeats, so a session blocked for an hour is still reminded.</summary>
    [Fact]
    public void The_last_interval_repeats_rather_than_stopping()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        var firedAt = new List<double>();
        for (var minute = 0.0; minute <= 50.0; minute += 0.5)
        {
            var before = Nudges.Count;
            EvaluateAt(minute);
            if (Nudges.Count > before)
            {
                firedAt.Add(minute);
            }
        }

        Assert.Equal([2.0, 7.0, 17.0, 27.0, 37.0, 47.0], firedAt);
    }

    /// <summary>
    /// Each boundary from both sides: just before it nothing has fired, just after it exactly
    /// one thing has. Without the "before" half, a mutation firing early would pass.
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(7.0)]
    [InlineData(17.0)]
    public void Nothing_fires_before_a_boundary_and_exactly_one_thing_fires_after(double boundary)
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        // Walk the ladder up to just before the boundary under test.
        foreach (var earlier in LadderBoundaries.Where(b => b < boundary))
        {
            EvaluateAt(earlier);
        }

        var beforeCount = Nudges.Count;
        EvaluateAt(boundary - 0.001);
        Assert.Equal(beforeCount, Nudges.Count);

        EvaluateAt(boundary);
        Assert.Equal(beforeCount + 1, Nudges.Count);
    }

    [Fact]
    public void A_nudge_is_the_same_sound_as_the_notice_but_softer_and_faded()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsQuestion, Start));
        EvaluateAt(2);

        var notice = Assert.Single(Notices);
        var nudge = Assert.Single(Nudges);

        Assert.Equal(notice.Sound, nudge.Sound);
        Assert.True(nudge.Gain < notice.Gain, "A nudge must never be louder than a notice.");
        Assert.True(nudge.Fade > TimeSpan.Zero, "A nudge fades in; that is what makes it feel softer.");
        AssertNothingUnclassified();
    }

    /// <summary>Every nudge in the ladder is equally soft — never louder, and never fading away.</summary>
    [Fact]
    public void Every_nudge_plays_at_the_same_gain()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        foreach (var minute in new[] { 2.0, 7.0, 17.0, 27.0 })
        {
            EvaluateAt(minute);
        }

        Assert.Equal(4, Nudges.Count);
        Assert.Single(Nudges.Select(n => n.Gain).Distinct());
    }

    [Fact]
    public void Evaluating_more_often_than_nudges_are_due_fires_nothing_extra()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        for (var minute = 0.0; minute <= 6.0; minute += 0.1)
        {
            EvaluateAt(minute);
        }

        Assert.Single(Nudges);
    }

    /// <summary>
    /// A host blocked past several due times must not release a burst — that would be
    /// "faster", which TS §IV.5 forbids outright.
    /// </summary>
    [Fact]
    public void Evaluating_late_fires_once_and_reschedules_from_now()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        EvaluateAt(30);

        Assert.Single(Nudges);
        Assert.Equal(Start.AddMinutes(35), _engine.NextNudgeAt(new SessionId("s-1")));
    }

    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    public void Both_needs_you_states_nudge(SessionState state)
    {
        _engine.OnSessionChanged(SessionIn(state, Start));
        EvaluateAt(2);

        Assert.Single(Nudges);
    }

    /// <summary>
    /// TS §IV.5 says "NeedsYou.*" and TS §IV.1 lists Error beside those states, but §IV.2 as
    /// ratified bands Error with Needs-You as "stopped until looked at". Read literally an
    /// errored session would notice once and go silent forever, so errors nudge.
    /// </summary>
    [Fact]
    public void An_errored_session_nudges_by_default()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Error, Start));
        EvaluateAt(2);

        Assert.Single(Nudges);
    }

    [Fact]
    public void An_errored_session_can_be_configured_not_to_nudge()
    {
        var engine = new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), new SoundPolicyOptions { NudgeOnError = false });

        engine.OnSessionChanged(SessionIn(SessionState.Error, Start));
        engine.Evaluate(Start.AddMinutes(60));

        Assert.Single(Notices);
        Assert.Empty(Nudges);
    }

    [Theory]
    [InlineData(SessionState.Working)]
    [InlineData(SessionState.Acked)]
    public void States_that_are_not_waiting_never_nudge(SessionState state)
    {
        _engine.OnSessionChanged(SessionIn(state, Start));

        EvaluateAt(60);

        Assert.Empty(_player.Played);
    }

    // ---- Cancel on Acked ---------------------------------------------------------------------------

    /// <summary>
    /// TS §IV.5: entering Acked cancels the scheduled nudge. Asserted as an <em>empty</em>
    /// recording after clearing, so "no nudge fired" cannot be confused with "a nudge fired and
    /// this assertion did not look at it".
    /// </summary>
    [Fact]
    public void Acking_cancels_the_scheduled_nudge()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.Acked, Start.AddSeconds(30)));
        Assert.Null(_engine.NextNudgeAt(new SessionId("s-1")));

        EvaluateAt(2);
        EvaluateAt(7);
        EvaluateAt(60);

        Assert.Empty(_player.Played);
    }

    /// <summary>Acking mid-ladder stops the rest of it, not just the next one.</summary>
    [Fact]
    public void Acking_mid_ladder_stops_every_later_nudge()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        EvaluateAt(2);
        Assert.Single(Nudges);
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.Acked, Start.AddMinutes(3)));
        EvaluateAt(7);
        EvaluateAt(17);
        EvaluateAt(60);

        Assert.Empty(_player.Played);
    }

    /// <summary>
    /// TS §IV.5 cancels on Acked "from any ack source" — and a new prompt is one of them
    /// (TS §IV.1), arriving as a move to Working rather than to Acked.
    /// </summary>
    [Fact]
    public void A_new_prompt_also_stops_the_nudges()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsQuestion, Start));
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.Working, Start.AddMinutes(1)));

        EvaluateAt(2);
        EvaluateAt(60);
        Assert.Empty(_player.Played);
    }

    [Fact]
    public void Ending_a_session_stops_the_nudges()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Error, Start));
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.Ended, Start.AddMinutes(1)));

        EvaluateAt(60);
        Assert.Empty(_player.Played);
    }

    // ---- Unread: at most one soft nudge ---------------------------------------------------------------

    [Fact]
    public void An_unread_result_gets_one_soft_nudge_at_five_minutes()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start));
        _player.Clear();

        EvaluateAt(4.999);
        Assert.Empty(Nudges);

        EvaluateAt(5);
        Assert.Single(Nudges);
    }

    /// <summary>"At most one" — pinned across the whole ladder the blocked states would have used.</summary>
    [Fact]
    public void An_unread_result_never_gets_a_second_nudge()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start));
        EvaluateAt(5);
        _player.Clear();

        foreach (var minute in new[] { 7.0, 10.0, 17.0, 30.0, 60.0, 600.0 })
        {
            EvaluateAt(minute);
        }

        Assert.Empty(_player.Played);
    }

    [Fact]
    public void The_unread_nudge_can_be_turned_off_entirely()
    {
        var engine = new SoundPolicyEngine(
            _player, _clock, new SingleWriterGuard(), new SoundPolicyOptions { UnreadNudgeAfter = null });

        engine.OnSessionChanged(SessionIn(SessionState.Unread, Start));
        engine.Evaluate(Start.AddMinutes(600));

        Assert.Single(Notices);
        Assert.Empty(Nudges);
    }

    /// <summary>Unread is on its own schedule, not the blocked-session ladder.</summary>
    [Fact]
    public void An_unread_result_does_not_use_the_blocked_ladder()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.Unread, Start));

        EvaluateAt(2);

        Assert.Empty(Nudges);
    }

    // ---- Mute -------------------------------------------------------------------------------------------

    [Fact]
    public void Muting_a_session_suppresses_its_notice_and_its_nudges()
    {
        _engine.SetSessionMuted(new SessionId("s-1"), true);

        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        EvaluateAt(2);
        EvaluateAt(7);

        Assert.Empty(_player.Played);
    }

    [Fact]
    public void Muting_a_group_suppresses_every_session_in_it()
    {
        _engine.SetGroupMuted(GroupKeys.ForWorkspace(Cwd), true);

        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start, "s-1"));
        _engine.OnSessionChanged(SessionIn(SessionState.Error, Start, "s-2"));
        EvaluateAt(2);

        Assert.Empty(_player.Played);
    }

    [Fact]
    public void Muting_one_group_leaves_another_audible()
    {
        _engine.SetGroupMuted(GroupKeys.ForWorkspace(Cwd), true);

        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start, "s-1"));
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start, "s-2", @"C:\elsewhere"));

        Assert.Single(Notices);
    }

    [Fact]
    public void Unmuting_makes_a_session_audible_again()
    {
        var id = new SessionId("s-1");
        _engine.SetSessionMuted(id, true);
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        Assert.Empty(_player.Played);

        _engine.SetSessionMuted(id, false);
        EvaluateAt(2);

        Assert.Single(Nudges);
    }

    /// <summary>
    /// Mute filters the output; it does not freeze the schedule. Unmuting therefore resumes at
    /// the natural cadence rather than releasing the reminders the operator asked not to hear.
    /// </summary>
    [Fact]
    public void A_muted_session_keeps_advancing_its_ladder_silently()
    {
        var id = new SessionId("s-1");
        _engine.SetSessionMuted(id, true);
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        EvaluateAt(2);
        EvaluateAt(7);
        Assert.Empty(_player.Played);

        _engine.SetSessionMuted(id, false);

        // The ladder advanced to the third rung while muted, so the next nudge is at 17 —
        // not immediately, and not a backlog of the two that were suppressed.
        EvaluateAt(16.999);
        Assert.Empty(_player.Played);

        EvaluateAt(17);
        Assert.Single(Nudges);
    }

    /// <summary>
    /// A session's <c>cwd</c> can change mid-turn, which re-derives its group (TS §IV.3) — so
    /// which group mute applies to it changes with it. Without tracking that, a blocked session
    /// would go on answering to the mute of a workspace it has left.
    /// </summary>
    [Fact]
    public void A_session_that_changes_group_answers_to_its_new_groups_mute()
    {
        var moved = GroupKeys.ForWorkspace(@"C:\elsewhere");
        var blocked = SessionIn(SessionState.NeedsPermission, Start);
        _engine.OnSessionChanged(blocked);
        _player.Clear();

        // Same state and entry instant — a directory move, not a new blocker.
        _engine.OnSessionChanged(blocked with
        {
            Cwd = @"C:\elsewhere",
            WorkspaceGroup = moved,
            LastActivity = Start.AddMinutes(1),
        });

        _engine.SetGroupMuted(moved, true);
        EvaluateAt(2);

        Assert.Empty(_player.Played);
    }

    [Fact]
    public void Mute_is_reported_for_both_scopes()
    {
        var id = new SessionId("s-1");
        var group = GroupKeys.ForWorkspace(Cwd);

        Assert.False(_engine.IsMuted(id, group));

        _engine.SetSessionMuted(id, true);
        Assert.True(_engine.IsMuted(id, group));

        _engine.SetSessionMuted(id, false);
        _engine.SetGroupMuted(group, true);
        Assert.True(_engine.IsMuted(id, group));
    }

    // ---- Several sessions at once -----------------------------------------------------------------------

    /// <summary>
    /// A nudge for one session must not be mistaken for a nudge for another. Each session's
    /// ladder runs from its own entry: s-1 entered at 0 so it nudges at 2 and again at 7, while
    /// s-2 entered at 5 so its first nudge is also at 7 — the two coincide without merging.
    /// </summary>
    /// <remarks>
    /// Counted per sound rather than asserted as a sequence: two nudges falling due in the same
    /// evaluation have no defined order between them, and pinning one would be asserting an
    /// implementation detail rather than the policy.
    /// </remarks>
    [Fact]
    public void Sessions_are_nudged_on_their_own_schedules()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start, "s-1"));
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsQuestion, Start.AddMinutes(5), "s-2"));
        _player.Clear();

        EvaluateAt(2);
        Assert.Equal(1, Nudges.Count(n => n.Sound == SoundId.Permission));
        Assert.Equal(0, Nudges.Count(n => n.Sound == SoundId.Question));

        EvaluateAt(7);
        Assert.Equal(2, Nudges.Count(n => n.Sound == SoundId.Permission));
        Assert.Equal(1, Nudges.Count(n => n.Sound == SoundId.Question));
    }

    [Fact]
    public void Acking_one_session_leaves_another_nudging()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start, "s-1"));
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsQuestion, Start, "s-2"));
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.Acked, Start.AddMinutes(1), "s-1"));
        EvaluateAt(2);

        Assert.Equal([SoundId.Question], Nudges.Select(n => n.Sound));
    }

    // ---- Moving between blocked states ---------------------------------------------------------------------

    /// <summary>
    /// A permission answered but replaced by a question is a new blocker: it announces itself
    /// with its own sound, and its ladder measures how long <em>this</em> question has waited.
    /// </summary>
    [Fact]
    public void Permission_becoming_a_question_is_a_fresh_notice_and_a_fresh_ladder()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        _player.Clear();

        _engine.OnSessionChanged(SessionIn(SessionState.NeedsQuestion, Start.AddMinutes(1.5)));

        Assert.Equal([SoundId.Question], Notices.Select(n => n.Sound));

        // The permission's 2-minute mark passes without a nudge; the question's own does not.
        EvaluateAt(2);
        Assert.Empty(Nudges);

        EvaluateAt(3.5);
        Assert.Equal([SoundId.Question], Nudges.Select(n => n.Sound));
    }

    // ---- The evaluate entry point ----------------------------------------------------------------------------

    [Fact]
    public void Evaluate_reads_the_clock_when_given_no_instant()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        _player.Clear();

        _clock.AdvanceMinutes(2);
        _engine.Evaluate();

        Assert.Single(Nudges);
    }

    [Fact]
    public void Evaluating_an_empty_engine_does_nothing()
    {
        _engine.Evaluate(Start.AddMinutes(60));

        Assert.Empty(_player.Played);
    }

    [Fact]
    public void The_next_nudge_time_is_visible_without_waiting_for_it()
    {
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        Assert.Equal(Start.AddMinutes(2), _engine.NextNudgeAt(new SessionId("s-1")));
        Assert.Null(_engine.NextNudgeAt(new SessionId("never-seen")));
    }

    // ---- Construction ------------------------------------------------------------------------------------------

    [Fact]
    public void The_engine_needs_a_player_a_clock_and_the_shared_guard()
    {
        Assert.Throws<ArgumentNullException>(() => new SoundPolicyEngine(null!, _clock, new SingleWriterGuard(), new SoundPolicyOptions()));
        Assert.Throws<ArgumentNullException>(() => new SoundPolicyEngine(_player, null!, new SingleWriterGuard(), new SoundPolicyOptions()));
        Assert.Throws<ArgumentNullException>(
            () => new SoundPolicyEngine(_player, _clock, null!, new SoundPolicyOptions()));
        Assert.Throws<ArgumentNullException>(
            () => new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), null!));
    }

    [Fact]
    public void OnSessionChanged_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.OnSessionChanged(null!));
    }

    /// <summary>TS §IV.5: never louder. A configuration that would breach it fails at construction.</summary>
    [Fact]
    public void A_nudge_louder_than_a_notice_is_rejected()
    {
        var louder = new SoundPolicyOptions { NoticeGain = 0.5, NudgeGain = 0.9 };

        Assert.Throws<ArgumentException>(() => new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), louder));
    }

    [Fact]
    public void A_custom_ladder_is_honoured()
    {
        var engine = new SoundPolicyEngine(
            _player,
            _clock,
            new SingleWriterGuard(),
            new SoundPolicyOptions { NudgeLadder = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)] });

        engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        engine.Evaluate(Start.AddMinutes(1));
        Assert.Single(Nudges);

        engine.Evaluate(Start.AddMinutes(4));
        Assert.Equal(2, Nudges.Count);

        engine.Evaluate(Start.AddMinutes(7));
        Assert.Equal(3, Nudges.Count);
    }

    // ---- Global mute and pause (T1.13; Impl §5.2) -------------------------------------------------

    /// <summary>
    /// <strong>Muted, the port is never called at all.</strong>
    /// </summary>
    /// <remarks>
    /// The proof is here rather than at the speaker. Nothing is audible in Phase 1 —
    /// <c>SilentSoundPlayer</c> until T1.14 — so "mute silences" is not observable, and asserting
    /// on a gain of zero would be asserting on a number nobody hears. What is observable, and what
    /// actually matters, is that no intent is emitted: <see cref="ISoundPlayer.Play"/> is not
    /// reached. <see cref="ISoundPlayer"/>'s own contract puts mute on this side of the port —
    /// it is policy, not playback.
    /// </remarks>
    [Fact]
    public void A_global_mute_stops_the_port_being_called()
    {
        _engine.SetAllMuted(muted: true);

        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        EvaluateAt(2);
        EvaluateAt(7);
        EvaluateAt(60);

        Assert.Empty(_player.Played);
    }

    /// <summary>Paused, likewise — same silence, different reason.</summary>
    [Fact]
    public void Pausing_stops_the_port_being_called()
    {
        _engine.SetMonitoringPaused(paused: true);

        _engine.OnSessionChanged(SessionIn(SessionState.Error, Start));
        EvaluateAt(2);
        EvaluateAt(60);

        Assert.Empty(_player.Played);
    }

    /// <summary>
    /// A timed mute lapses on its own, with nothing scheduled to end it.
    /// </summary>
    /// <remarks>
    /// This is what "a predicate, not a timer" buys: no callback fires at the thirty-minute mark,
    /// and the mute simply stops being true the next time a sound would be emitted. A timer that
    /// unmuted exactly when a nudge fell due would be a beep out of nowhere.
    /// </remarks>
    [Fact]
    public void A_timed_mute_lapses_without_anything_firing()
    {
        _engine.SetAllMuted(muted: true, Start.AddMinutes(30));

        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        _clock.AdvanceMinutes(2);
        EvaluateAt(2);
        Assert.Empty(_player.Played);

        // Past the expiry: the same schedule now sounds, because the predicate stopped holding.
        _clock.AdvanceMinutes(40);
        EvaluateAt(42);

        Assert.NotEmpty(_player.Played);
    }

    /// <summary>
    /// Unmuting does not release a backlog.
    /// </summary>
    /// <remarks>
    /// Mute filters the output and does not freeze the ladder, so a session muted through three
    /// rungs does not fire three nudges when it comes back — it resumes at the natural cadence.
    /// The operator asked not to be disturbed; being disturbed three times at once for having
    /// asked would be worse than never muting.
    /// </remarks>
    [Fact]
    public void Unmuting_does_not_release_a_backlog()
    {
        _engine.SetAllMuted(muted: true);
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        EvaluateAt(2);
        EvaluateAt(7);
        EvaluateAt(17);
        Assert.Empty(_player.Played);

        _engine.SetAllMuted(muted: false);
        EvaluateAt(18);

        // Nothing was owed: the rungs that fell due while muted were spent, not stored.
        Assert.Empty(_player.Played);
    }

    /// <summary>Unmuting lets the next thing be heard.</summary>
    [Fact]
    public void Unmuting_lets_the_next_notice_through()
    {
        _engine.SetAllMuted(muted: true);
        _engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        Assert.Empty(_player.Played);

        _engine.SetAllMuted(muted: false);
        _engine.OnSessionChanged(SessionIn(SessionState.Error, Start.AddMinutes(1), "s-2"));

        Assert.Single(Notices);
    }


    // ---- Master volume (T1.14; Impl Part 7) -------------------------------------------------------

    /// <summary>
    /// Master volume multiplies whatever gain a sound would otherwise play at.
    /// </summary>
    /// <remarks>
    /// Asserted as a <em>multiplier</em> rather than as a pair of numbers: at half volume the
    /// notice and the nudge both halve, so a nudge stays softer than a notice by the same
    /// proportion. A ceiling would make them converge as it came down, which is not what TS §IV.5
    /// describes.
    /// </remarks>
    [Fact]
    public void Master_volume_scales_every_gain()
    {
        var engine = new SoundPolicyEngine(
            _player,
            _clock,
            new SingleWriterGuard(),
            new SoundPolicyOptions { MasterVolume = 0.5 });

        engine.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));
        engine.Evaluate(Start.AddMinutes(2));

        Assert.Equal(2, _player.Played.Count);

        // The notice at half of 1.0, the nudge at half of 0.6 — and the nudge still softer.
        Assert.Equal(0.5, _player.Played[0].Gain, 3);
        Assert.Equal(0.3, _player.Played[1].Gain, 3);
        Assert.True(_player.Played[1].Gain < _player.Played[0].Gain);
    }

    /// <summary>
    /// <strong>Silence by volume is not the same thing as mute.</strong>
    /// </summary>
    /// <remarks>
    /// A master volume of zero still emits — the port is called, at nothing — while a mute stops
    /// the call being made at all. They are different mechanisms with different meanings, and the
    /// distinction is exactly what would be lost if the adapter grew its own mute: "no Play call"
    /// is T1.13's invariant and the only thing that proves mute is policy rather than volume.
    /// </remarks>
    [Fact]
    public void A_zero_master_volume_still_plays_while_a_mute_does_not()
    {
        var silentByVolume = new SoundPolicyEngine(
            _player,
            _clock,
            new SingleWriterGuard(),
            new SoundPolicyOptions { MasterVolume = 0.0 });

        silentByVolume.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        // Emitted, at nothing.
        Assert.Single(_player.Played);
        Assert.Equal(0.0, _player.Played[0].Gain);

        _player.Clear();

        // Muted: not emitted at all. Same session, same state, same player.
        var muted = new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), new SoundPolicyOptions());
        muted.SetAllMuted(muted: true);
        muted.OnSessionChanged(SessionIn(SessionState.NeedsPermission, Start));

        Assert.Empty(_player.Played);
    }
    /// <summary>The modes read back, so the tray can render them.</summary>
    [Fact]
    public void The_modes_are_readable()
    {
        Assert.Null(_engine.AllMutedUntil);
        Assert.False(_engine.IsMonitoringPaused);
        Assert.False(_engine.IsSilenced(Start));

        _engine.SetAllMuted(muted: true, Start.AddMinutes(30));
        Assert.Equal(Start.AddMinutes(30), _engine.AllMutedUntil);
        Assert.True(_engine.IsSilenced(Start));
        Assert.False(_engine.IsSilenced(Start.AddMinutes(31)));

        _engine.SetAllMuted(muted: false);
        Assert.Null(_engine.AllMutedUntil);

        _engine.SetMonitoringPaused(paused: true);
        Assert.True(_engine.IsMonitoringPaused);

        // Pause has no expiry: it is still silent an hour later, unlike a timed mute.
        Assert.True(_engine.IsSilenced(Start.AddHours(1)));
    }

    /// <summary>An indefinite mute is not confused with no mute at all.</summary>
    [Fact]
    public void An_indefinite_mute_never_lapses()
    {
        _engine.SetAllMuted(muted: true);

        Assert.Equal(DateTimeOffset.MaxValue, _engine.AllMutedUntil);
        Assert.True(_engine.IsSilenced(Start.AddYears(1)));
    }
}

public sealed class SoundPolicyOptionsTests
{
    [Fact]
    public void Defaults_are_TS_IV_5_s_values()
    {
        var options = new SoundPolicyOptions();

        Assert.Equal(
            [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)],
            options.NudgeLadder);
        Assert.Equal(TimeSpan.FromMinutes(5), options.UnreadNudgeAfter);
        Assert.Equal(1.0, options.NoticeGain);
        Assert.True(options.NudgeGain < options.NoticeGain);
        Assert.True(options.NudgeFadeIn > TimeSpan.Zero);
    }

    [Fact]
    public void An_empty_ladder_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SoundPolicyOptions { NudgeLadder = [] });
    }

    [Fact]
    public void A_non_positive_interval_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new SoundPolicyOptions { NudgeLadder = [TimeSpan.Zero] });
        Assert.Throws<ArgumentException>(() =>
            new SoundPolicyOptions { NudgeLadder = [TimeSpan.FromMinutes(-1)] });
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void A_gain_outside_zero_to_one_is_rejected(double gain)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundPolicyOptions { NoticeGain = gain });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundPolicyOptions { NudgeGain = gain });
    }

    /// <summary>The ladder is copied, so a caller cannot mutate the policy after handing it over.</summary>
    [Fact]
    public void The_ladder_is_copied()
    {
        var ladder = new List<TimeSpan> { TimeSpan.FromMinutes(1) };
        var options = new SoundPolicyOptions { NudgeLadder = ladder };

        ladder.Add(TimeSpan.FromMinutes(99));

        Assert.Single(options.NudgeLadder);
    }
}
