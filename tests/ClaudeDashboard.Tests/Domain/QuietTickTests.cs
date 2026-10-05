using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// A watchdog tick that finds nothing makes no sound (T1.44, issue #56).
/// </summary>
/// <remarks>
/// The real Registry and the real sound engine, wired as the product wires them, with a recording
/// player and a recording decision sink — so "no finished sound" is what the engine played, and
/// "the ladder was not reset" is when it nudged.
/// </remarks>
public sealed class QuietTickTests
{
    private const string Cwd = @"C:\projects\dashboard";
    private const string Cron = "zqx-cron-prompt-marker-2k7: check the coder and resurface anything overdue.";
    private static readonly SessionId Id = new("s-1");

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry;
    private readonly RecordingSoundPlayer _player = new();
    private readonly SuppressionLog _sink = new();
    private readonly SoundPolicyEngine _engine;

    public QuietTickTests()
    {
        _registry = new SessionRegistry(_guard);
        _engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions(), _sink);
        // As the product subscribes it: the effective group, so a titled roster member is heard as
        // one. Untitled sessions fall back to their workspace group.
        _registry.SessionChanged += (_, e) => _engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, Book));
    }

    private Session Current => _registry.Sessions[Id];

    private int Finished => _player.Played.Count(played => played.Sound == SoundId.Finished);

    // ---- A quiet tick ---------------------------------------------------------------------------

    /// <summary>
    /// <strong>A tick replying exactly the sentinel makes no sound, and the row is back as it was</strong>:
    /// the state, the real answer, and the instant it was entered.
    /// </summary>
    [Fact]
    public void A_quiet_tick_makes_no_sound_and_puts_the_row_back()
    {
        var entered = GivenFinishedWithCron();
        Assert.Equal(1, Finished);

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron, "p-2"));
        Assert.Equal(SessionState.Working, Current.State);

        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Applied, Apply(Stopped("p-2", QuietTicks.Sentinel)));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(entered, Current.EnteredAt);
        Assert.Equal("the real answer", Current.Latest.Answer);
        Assert.Equal("the real work", Current.Latest.Prompt);
        Assert.Null(Current.PreTick);

        Assert.Equal(1, Finished);
        Assert.Contains((SoundId.Finished, SuppressionReason.AlreadyAnnounced), _sink.Suppressed);
    }

    /// <summary>
    /// <strong>A quiet tick does not reset the nudge ladder</strong>: the Unread nudge comes when it
    /// was due from the real finish, not five minutes after the tick.
    /// </summary>
    [Fact]
    public void A_quiet_tick_leaves_the_nudge_ladder_where_it_was()
    {
        var entered = GivenFinishedWithCron();
        var after = new SoundPolicyOptions().UnreadNudgeAfter!.Value;

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron, "p-2"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", QuietTicks.Sentinel));

        // Just past the nudge the real finish scheduled. A ladder restarted by the tick would not
        // be due until after + 3 minutes.
        _engine.Evaluate(entered + after + TimeSpan.FromSeconds(1));

        Assert.Equal(2, Finished);
    }

    /// <summary>A tick that arrives while the session is Waiting puts it back to Waiting, with what it waited on.</summary>
    [Fact]
    public void A_quiet_tick_while_waiting_reverts_to_waiting()
    {
        Apply(Prompt("the real work", "p-1"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-1", "started the build", crons: [Cron], running: [new BackgroundTask("b1", BackgroundTaskKind.Shell, "Run the build")]));
        Assert.Equal(SessionState.Waiting, Current.State);
        var waitingOn = Current.WaitingOn;

        _clock.AdvanceMinutes(5);
        Apply(Prompt(Cron, "p-2"));
        Assert.Empty(Current.WaitingOn);

        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", QuietTicks.Sentinel, crons: [Cron]));

        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Equal(waitingOn, Current.WaitingOn);
        Assert.Equal("started the build", Current.Latest.Answer);
        Assert.Empty(_player.Played);
    }

    /// <summary>Surrounding whitespace does not matter; it is trimmed before the comparison.</summary>
    [Theory]
    [InlineData(" WATCHDOG-QUIET")]
    [InlineData("WATCHDOG-QUIET\n")]
    [InlineData("\r\n\tWATCHDOG-QUIET \r\n")]
    public void Surrounding_whitespace_is_still_quiet(string reply)
    {
        GivenFinishedWithCron();

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron, "p-2"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", reply));

        Assert.Equal("the real answer", Current.Latest.Answer);
        Assert.Equal(1, Finished);
    }

    // ---- Anything else beeps, as today ---------------------------------------------------------

    /// <summary>
    /// <strong>Any other reply beeps and displays as today</strong>: other text, the sentinel with
    /// anything around it, a trailing period, markdown, another case, a Resurface, nothing.
    /// </summary>
    [Theory]
    [InlineData("WATCHDOG-QUIET.")]
    [InlineData("`WATCHDOG-QUIET`")]
    [InlineData("**WATCHDOG-QUIET**")]
    [InlineData("WATCHDOG-QUIET and nothing else")]
    [InlineData("Checked: WATCHDOG-QUIET")]
    [InlineData("watchdog-quiet")]
    [InlineData("NEEDS YOU: the coder has been silent for 40 minutes.")]
    [InlineData("")]
    public void Any_other_reply_beeps_and_displays_as_today(string reply)
    {
        var entered = GivenFinishedWithCron();

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron, "p-2"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", reply));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.NotEqual(entered, Current.EnteredAt);
        Assert.Equal(reply, Current.Latest.Answer);
        Assert.Equal(2, Finished);
    }

    /// <summary>
    /// A typed prompt that equals a cron's text is not a tick when the previous Stop listed no such
    /// cron — even if an earlier Stop did.
    /// </summary>
    [Fact]
    public void A_prompt_matching_a_cron_the_previous_stop_did_not_list_is_not_a_tick()
    {
        GivenFinishedWithCron();

        // The next turn's Stop lists no crons any more.
        _clock.AdvanceMinutes(1);
        Apply(Prompt("something else", "p-2"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", "done", crons: []));
        Assert.Equal(2, Finished);

        _clock.AdvanceMinutes(1);
        Apply(Prompt(Cron, "p-3"));
        Assert.Null(Current.PreTick);

        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-3", QuietTicks.Sentinel, crons: []));

        Assert.Equal(QuietTicks.Sentinel, Current.Latest.Answer);
        Assert.Equal(3, Finished);
    }

    /// <summary>A prompt that matches no listed cron is not a tick, and beeps as today.</summary>
    [Fact]
    public void A_prompt_matching_no_listed_cron_beeps()
    {
        GivenFinishedWithCron();

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron + " ", "p-2"));
        Assert.Null(Current.PreTick);

        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", QuietTicks.Sentinel));

        Assert.Equal(QuietTicks.Sentinel, Current.Latest.Answer);
        Assert.Equal(2, Finished);
    }

    /// <summary>A cron's prompt is a machine prompt: it is not recorded as an acknowledgment.</summary>
    [Fact]
    public void A_cron_prompt_is_not_an_acknowledgment()
    {
        GivenFinishedWithCron();

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron, "p-2"));

        var cause = Current.Transitions[^1].Cause!;
        Assert.Contains("scheduled prompt", cause, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-ack", cause, StringComparison.Ordinal);
    }

    // ---- A roster group ------------------------------------------------------------------------

    /// <summary>
    /// <strong>A quiet tick in a roster member does not announce the group's finish again</strong> —
    /// the operator's own case: the watchdog runs in the director, a roster member.
    /// </summary>
    /// <remarks>
    /// The tick unsettles the group and the revert settles it again at the same quiet instant: the
    /// settle already announced. Driven as the consumer drives it, with the group's quiet instant.
    /// </remarks>
    [Fact]
    public void A_quiet_tick_in_a_roster_member_does_not_announce_the_group_again()
    {
        var key = GroupKeys.ForRoster("orchestration");
        var watch = new RosterGroupWatch();
        var group = 0;

        void Observe()
        {
            var groups = GroupResolver.Resolve(_registry.Sessions.Values, Book);

            foreach (var change in watch.Observe(groups, _clock.Now + RosterSettle.DefaultWindow))
            {
                if (change.Event == RosterGroupEvent.Settled)
                {
                    _engine.OnRosterGroupSettled(change.Group, _clock.Now, RosterSettle.QuietSince(groups.Single(g => g.Key == change.Group)));
                    group++;
                }
                else if (change.Event == RosterGroupEvent.Unsettled)
                {
                    _engine.OnRosterGroupUnsettled(change.Group);
                }
            }
        }

        // The session carries the director's title, so its group is the roster.
        Apply(Prompt("the real work", "p-1") with { SessionTitle = "director" });
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-1", "the real answer", crons: [Cron]));
        Observe();

        var announced = _player.Played.Count;

        _clock.AdvanceMinutes(2);
        Apply(Prompt(Cron, "p-2"));
        Observe();

        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", QuietTicks.Sentinel, crons: [Cron]));
        Observe();

        Assert.Equal(2, group);
        Assert.Equal(announced, _player.Played.Count);
        Assert.Contains((SoundId.Finished, SuppressionReason.AlreadyAnnounced), _sink.Suppressed);
        Assert.Equal(key, GroupKeys.Effective(Current, Book));
    }

    private static readonly string[] Members = ["director", "coder"];

    /// <summary>The roster the director belongs to.</summary>
    private static readonly RosterBook Book = RosterBook.From([("orchestration", Members)]);

    // ---- Harness -------------------------------------------------------------------------------

    /// <summary>Real work, finished, with the watchdog cron listed on its Stop. Returns when it entered Unread.</summary>
    private DateTimeOffset GivenFinishedWithCron()
    {
        Apply(Prompt("the real work", "p-1"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-1", "the real answer", crons: [Cron]));
        Assert.Equal(SessionState.Unread, Current.State);

        return Current.EnteredAt;
    }

    private ApplyOutcome Apply(InboundEvent inboundEvent) => _registry.Apply(inboundEvent);

    private UserPromptSubmit Prompt(string text, string promptId) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, Prompt = text,
    };

    private Stop Stopped(string promptId, string? reply, string[]? crons = null, BackgroundTask[]? running = null) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId,
        LastAssistantMessage = reply,
        ScheduledPrompts = ScheduledPrompts.Of(crons ?? [Cron]),
        BackgroundTasks = running ?? [],
    };

    /// <summary>Records what the engine suppressed, and why.</summary>
    private sealed class SuppressionLog : IDecisionSink
    {
        public List<(SoundId Sound, SuppressionReason Reason)> Suppressed { get; } = [];

        public void SoundPlayed(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, int rung, TimeSpan waited)
        {
        }

        public void SoundDropped(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, int rung, TimeSpan waited, SoundOutcome outcome)
        {
        }

        public void SoundSuppressed(SoundDecisionKind kind, SessionId session, GroupKey group, SoundId sound, SuppressionReason reason) =>
            Suppressed.Add((sound, reason));
    }
}
