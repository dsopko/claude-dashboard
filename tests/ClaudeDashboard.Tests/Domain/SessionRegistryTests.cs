using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The transition table of TS §IV.1 and the three guards of Impl §2.2.
/// </summary>
/// <remarks>
/// Event timestamps come from a <see cref="FakeClock"/> the test advances, which is how the
/// ordering and staleness cases are exercised without waiting. The Registry itself never reads
/// a clock — every stamp it writes comes from the event being applied.
/// </remarks>
public sealed class SessionRegistryTests
{
    private const string Cwd = @"C:\projects\dashboard";
    private static readonly SessionId Id = new("s-1");

    private readonly FakeClock _clock = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly List<SessionChangedEventArgs> _changes = [];

    public SessionRegistryTests() => _registry.SessionChanged += (_, e) => _changes.Add(e);

    private Session Current => _registry.Sessions[Id];

    // ---- Reaching a starting state ----------------------------------------------------------

    /// <summary>Puts the session into Working with a correlated in-flight prompt.</summary>
    private Session GivenWorking(string promptId = "p-1")
    {
        Apply(Prompt("do the thing", promptId));
        return Current;
    }

    private ApplyOutcome Apply(InboundEvent inboundEvent) => _registry.Apply(inboundEvent);

    private UserPromptSubmit Prompt(string text = "do the thing", string? promptId = "p-1") => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, Prompt = text,
    };

    private Stop Finished(string? answer = "all done", string? promptId = "p-1") => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId,
        LastAssistantMessage = answer,
    };

    private Notification Notified(string type) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = type,
    };

    private StopFailure Failed(string kind = "rate_limit") => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, ErrorKind = kind,
    };

    private SessionEnd Ended(string reason = "clear") => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = reason,
    };

    private SessionStart Started(string? source = "startup", string cwd = Cwd) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = cwd, Source = source,
    };

    private CwdChanged Moved(string cwd) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = cwd,
    };

    private Ack Acknowledged(AckSource source = AckSource.Manual) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = source,
    };

    // ---- The transition table ----------------------------------------------------------------

    [Fact]
    public void UserPromptSubmit_moves_a_session_to_Working()
    {
        Assert.Equal(ApplyOutcome.Applied, Apply(Prompt("run the tests")));

        Assert.Equal(SessionState.Working, Current.State);
        Assert.Equal("run the tests", Current.Latest.Prompt);
        Assert.Equal("p-1", Current.Latest.PromptId);
        Assert.Equal(_clock.Now, Current.EnteredAt);
    }

    [Fact]
    public void Working_moves_to_Unread_on_Stop()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("29 passed")));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal("29 passed", Current.Latest.Answer);
        Assert.True(Current.Latest.IsAnswered);
        Assert.Equal(_clock.Now, Current.EnteredAt);
    }

    [Theory]
    [InlineData("permission_prompt", SessionState.NeedsPermission)]
    [InlineData("agent_needs_input", SessionState.NeedsQuestion)]
    public void Working_moves_to_the_needs_you_state_on_Notification(string type, SessionState expected)
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Notified(type)));

        Assert.Equal(expected, Current.State);
    }

    /// <summary>
    /// <strong>The regression the operator hit (issue #1).</strong> A finished result that
    /// nobody has read stays Unread when the session goes idle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Stop</c> → Unread, green, correct; ninety seconds later an <c>idle_prompt</c> turned
    /// the row red and blinking at the top of NEEDS YOU, needing nothing. Every session that
    /// finishes eventually sits idle, so this was the steady state rather than an edge — 207
    /// notifications against 13 permission requests in one day of real use.
    /// </para>
    /// <para>
    /// <strong>The controls are not decoration.</strong> "The state did not change" is satisfied
    /// by a <c>TargetOf</c> that returns null for everything, which would take the two real
    /// notifications down with the inert one and would look exactly like this test passing. So
    /// the same starting state is driven with <c>agent_needs_input</c> and with
    /// <c>permission_prompt</c>, both of which must still move.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_idle_prompt_leaves_an_unread_result_where_it_is()
    {
        GivenWorking();
        Apply(Finished());
        Assert.Equal(SessionState.Unread, Current.State);

        _clock.AdvanceMinutes(2);
        var outcome = Apply(Notified("idle_prompt"));

        // Declined, not applied — and the session is exactly where it was.
        Assert.Equal(ApplyOutcome.Ignored, outcome);
        Assert.Equal(SessionState.Unread, Current.State);

        // …and the answer is still there to read, which is the point of the band.
        Assert.True(Current.Latest.IsAnswered);
    }

    /// <summary>The control: a real question still reaches Needs You from the same state.</summary>
    [Fact]
    public void A_needs_input_notification_still_moves_an_unread_result()
    {
        GivenWorking();
        Apply(Finished());

        _clock.AdvanceMinutes(2);

        Assert.Equal(ApplyOutcome.Applied, Apply(Notified("agent_needs_input")));
        Assert.Equal(SessionState.NeedsQuestion, Current.State);
    }

    /// <summary>The other control: a permission still reaches Needs You from the same state.</summary>
    [Fact]
    public void A_permission_notification_still_moves_an_unread_result()
    {
        GivenWorking();
        Apply(Finished());

        _clock.AdvanceMinutes(2);

        Assert.Equal(ApplyOutcome.Applied, Apply(Notified("permission_prompt")));
        Assert.Equal(SessionState.NeedsPermission, Current.State);
    }

    /// <summary>
    /// Every notification kind is classified as one that moves the session or one that does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven from <see cref="NotificationKind"/> rather than listed, so a kind added later has
    /// to be classified rather than falling quietly into the inert half. That is the failure this
    /// issue was: <c>idle_prompt</c> was classified, just wrongly, and nothing said which side of
    /// the line each kind was meant to be on.
    /// </para>
    /// <para>
    /// It earns its keep beyond the three tests above because it is the one that fails on
    /// <em>addition</em> rather than on change — the direction none of the others cover.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_notification_kind_is_classified()
    {
        var moves = new Dictionary<NotificationKind, SessionState?>
        {
            [NotificationKind.PermissionPrompt] = SessionState.NeedsPermission,
            [NotificationKind.AgentNeedsInput] = SessionState.NeedsQuestion,

            // Recognised and deliberately inert: idleness is not a request, and agent_completed
            // is corroboration for a Stop that is authoritative on its own.
            [NotificationKind.IdlePrompt] = null,
            [NotificationKind.AgentCompleted] = null,

            // Not a wire value; nothing can produce it but the mapper's fallback.
            [NotificationKind.Unknown] = null,
        };

        Assert.Equal(Enum.GetValues<NotificationKind>().Length, moves.Count);

        foreach (var (kind, expected) in moves)
        {
            var registry = new SessionRegistry(new SingleWriterGuard());
            var id = new SessionId($"s-{kind}");

            registry.Apply(new UserPromptSubmit
            {
                SessionId = id,
                Timestamp = FakeClock.DefaultStart,
                Cwd = Cwd,
                PromptId = "p-1",
                Prompt = "run the tests",
            });

            registry.Apply(new Notification
            {
                SessionId = id,
                Timestamp = FakeClock.DefaultStart.AddMinutes(1),
                Cwd = Cwd,
                NotificationType = Wire(kind),
            });

            var actual = registry.Sessions[id].State;

            Assert.Equal(expected ?? SessionState.Working, actual);
        }
    }

    /// <summary>The wire spelling Claude Code sends for a kind (Impl §9.1).</summary>
    private static string Wire(NotificationKind kind) => kind switch
    {
        NotificationKind.PermissionPrompt => "permission_prompt",
        NotificationKind.IdlePrompt => "idle_prompt",
        NotificationKind.AgentNeedsInput => "agent_needs_input",
        NotificationKind.AgentCompleted => "agent_completed",
        _ => "something-no-build-recognises",
    };

    /// <summary>
    /// Impl §9.1 marks <c>agent_completed</c> as an optional corroborating signal; <c>Stop</c>
    /// is what authoritatively finishes a turn, so this must not move the session on its own.
    /// </summary>
    [Fact]
    public void Notification_agent_completed_does_not_change_state()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Ignored, Apply(Notified("agent_completed")));

        Assert.Equal(SessionState.Working, Current.State);
    }

    [Fact]
    public void Notification_of_an_unrecognized_type_is_ignored()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Ignored, Apply(Notified("brand_new_signal")));

        Assert.Equal(SessionState.Working, Current.State);
    }

    [Fact]
    public void Working_moves_to_Error_on_StopFailure()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Failed("rate_limit")));

        Assert.Equal(SessionState.Error, Current.State);
        Assert.Equal("rate_limit", Current.ErrorKind);
    }

    [Fact]
    public void Any_state_moves_to_Ended_on_SessionEnd()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Ended("logout")));

        Assert.Equal(SessionState.Ended, Current.State);
    }

    [Theory]
    [InlineData(SessionState.Unread)]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    public void An_ack_moves_an_attention_seeking_session_to_Acked(SessionState from)
    {
        GivenInState(from);
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Acknowledged()));

        Assert.Equal(SessionState.Acked, Current.State);
    }

    [Theory]
    [InlineData(AckSource.Manual)]
    [InlineData(AckSource.InferredFocus)]
    public void Both_ack_sources_acknowledge(AckSource source)
    {
        GivenInState(SessionState.Unread);
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Acknowledged(source)));

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Contains(source.ToString(), Current.Transitions[^1].Cause, StringComparison.Ordinal);
    }

    /// <summary>Nothing has finished, so there is nothing to acknowledge.</summary>
    [Fact]
    public void An_ack_of_a_working_session_does_nothing()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Ignored, Apply(Acknowledged()));

        Assert.Equal(SessionState.Working, Current.State);
    }

    // ---- Auto-ack on a new prompt --------------------------------------------------------------

    /// <summary>
    /// TS §IV.1: a new prompt moves the session to Working from <em>any</em> state and
    /// auto-acknowledges what was pending — the operator cannot have typed it without having
    /// seen the previous result.
    /// </summary>
    [Theory]
    [InlineData(SessionState.Unread)]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    [InlineData(SessionState.Acked)]
    [InlineData(SessionState.Ended)]
    public void A_new_prompt_moves_any_state_to_Working(SessionState from)
    {
        GivenInState(from);
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Prompt("next thing", "p-next")));

        Assert.Equal(SessionState.Working, Current.State);
        Assert.Equal("next thing", Current.Latest.Prompt);
    }

    [Theory]
    [InlineData(SessionState.Unread)]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    public void A_new_prompt_records_the_auto_ack_of_what_was_pending(SessionState from)
    {
        GivenInState(from);
        _clock.AdvanceMinutes(1);

        Apply(Prompt("next thing", "p-next"));

        var transition = Current.Transitions[^1];
        Assert.Equal(from, transition.From);
        Assert.Equal(SessionState.Working, transition.To);
        Assert.Contains("auto-ack", transition.Cause!, StringComparison.Ordinal);
        Assert.Contains(from.ToString(), transition.Cause!, StringComparison.Ordinal);
    }

    /// <summary>A prompt from a quiet session is not acknowledging anything.</summary>
    [Fact]
    public void A_new_prompt_from_a_quiet_session_records_no_auto_ack()
    {
        GivenInState(SessionState.Acked);
        _clock.AdvanceMinutes(1);

        Apply(Prompt("next thing", "p-next"));

        Assert.DoesNotContain("auto-ack", Current.Transitions[^1].Cause!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A permission prompt approved in the terminal is followed by the turn finishing. TS
    /// §IV.1's diagram draws Stop only from Working, but this path is ordinary use, and
    /// refusing it would strand the session in the loudest band there is.
    /// </summary>
    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    public void A_turn_can_finish_from_a_needs_you_state(SessionState from)
    {
        GivenInState(from);
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("done after all")));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal("done after all", Current.Latest.Answer);
    }

    // ---- Idempotency ----------------------------------------------------------------------------

    [Fact]
    public void The_same_stop_applied_twice_has_one_effect()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        var stop = Finished("29 passed");

        Assert.Equal(ApplyOutcome.Applied, Apply(stop));
        _changes.Clear();

        Assert.Equal(ApplyOutcome.Duplicate, Apply(stop));

        Assert.Empty(_changes);
        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Single(Current.Transitions, t => t.To == SessionState.Unread);
    }

    /// <summary>
    /// A redelivery arrives with a fresh, later stamp, so it passes the timestamp guard and is
    /// not record-equal to the original either. Idempotency has to be judged by outcome.
    /// </summary>
    [Fact]
    public void A_redelivered_stop_with_a_later_stamp_still_has_no_second_effect()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Finished("29 passed"));
        _changes.Clear();

        _clock.AdvanceMinutes(1);
        var redelivered = Finished("29 passed");

        Assert.Equal(ApplyOutcome.Duplicate, Apply(redelivered));

        Assert.Empty(_changes);
        Assert.Equal(SessionState.Unread, Current.State);
    }

    [Theory]
    [InlineData("permission_prompt")]
    [InlineData("agent_needs_input")]
    public void The_same_notification_applied_twice_has_one_effect(string type)
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Notified(type)));
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Duplicate, Apply(Notified(type)));
        Assert.Empty(_changes);
    }

    [Fact]
    public void The_same_prompt_id_applied_twice_has_one_effect()
    {
        Assert.Equal(ApplyOutcome.Applied, Apply(Prompt("do the thing", "p-1")));
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Duplicate, Apply(Prompt("do the thing", "p-1")));

        Assert.Empty(_changes);
        Assert.Single(Current.Transitions);
    }

    [Fact]
    public void The_same_failure_applied_twice_has_one_effect()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Applied, Apply(Failed("rate_limit")));
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Duplicate, Apply(Failed("rate_limit")));
        Assert.Empty(_changes);
    }

    [Fact]
    public void A_different_failure_kind_is_a_real_change()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Failed("rate_limit"));
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Failed("overloaded")));

        Assert.Equal("overloaded", Current.ErrorKind);
    }

    /// <summary>
    /// Enriching a session in place must not make it look newer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Session.EnteredAt"/> is the age clock, and TS §IV.2 sorts the Needs-You band
    /// <strong>oldest first</strong> — the longest-blocked session belongs at the top, because
    /// it is the most wasted capacity. A same-state change that restarted that clock would sink
    /// the session to the bottom of the band instead, quietly defeating the ordering asymmetry
    /// TS calls the heart of the attention model. It would also reset the nudge schedule the
    /// same clock feeds (TS §IV.5), so a session blocked for an hour could go on chiming as if
    /// newly blocked.
    /// </para>
    /// <para>
    /// The live path is <see cref="SessionState.Error"/> → <see cref="SessionState.Error"/>
    /// with a different <c>ErrorKind</c>: a real change, accepted and recorded, but not a
    /// change of state — so the clock must survive it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_new_error_kind_must_not_restart_the_age_clock()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Failed("rate_limit"));
        var blockedSince = Current.EnteredAt;

        _clock.AdvanceMinutes(10);
        Assert.Equal(ApplyOutcome.Applied, Apply(Failed("overloaded")));

        Assert.Equal(SessionState.Error, Current.State);
        Assert.Equal("overloaded", Current.ErrorKind);
        Assert.Equal(blockedSince, Current.EnteredAt);

        // The session was still heard from, so recency advances even though age does not.
        Assert.Equal(_clock.Now, Current.LastActivity);
    }

    /// <summary>
    /// The same rule for the other same-state path: a <c>cwd</c> move enriches the session
    /// without changing what it is doing, so it must not restart the age clock either.
    /// </summary>
    [Fact]
    public void A_directory_move_must_not_restart_the_age_clock()
    {
        GivenInState(SessionState.Unread);
        var finishedAt = Current.EnteredAt;

        _clock.AdvanceMinutes(10);
        Assert.Equal(ApplyOutcome.Applied, Apply(Moved(@"C:\projects\elsewhere")));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(finishedAt, Current.EnteredAt);

        // The session was still heard from, so recency advances even though age does not.
        Assert.Equal(_clock.Now, Current.LastActivity);
    }

    [Fact]
    public void The_same_session_end_applied_twice_has_one_effect()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Applied, Apply(Ended()));
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Duplicate, Apply(Ended()));
        Assert.Empty(_changes);
    }

    [Fact]
    public void The_same_ack_applied_twice_has_one_effect()
    {
        GivenInState(SessionState.Unread);
        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Applied, Apply(Acknowledged()));
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Ignored, Apply(Acknowledged()));
        Assert.Empty(_changes);
    }

    /// <summary>
    /// A duplicate must leave no trace at all: if it advanced LastActivity it would silently
    /// float the session up the recency ordering the Working and Quiet bands sort on.
    /// </summary>
    [Fact]
    public void A_duplicate_does_not_advance_last_activity()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Finished("29 passed"));
        var settled = Current.LastActivity;

        _clock.AdvanceMinutes(30);
        Apply(Finished("29 passed"));

        Assert.Equal(settled, Current.LastActivity);
    }

    // ---- Stale-drop ------------------------------------------------------------------------------

    /// <summary>
    /// An event older than the session's last-applied stamp is ignored (TS §IV.1). Hook events
    /// cannot trip this on their own — ingress stamps them on arrival and the channel is FIFO —
    /// so the guard is exercised here with events carrying independent origins, which is exactly
    /// where it earns its place: a manual or inferred-focus ack, and warm-restart replay.
    /// </summary>
    [Fact]
    public void An_event_older_than_the_last_applied_stamp_is_dropped()
    {
        GivenWorking();
        _clock.AdvanceMinutes(10);
        Apply(Finished("29 passed"));

        var stale = new Ack
        {
            SessionId = Id,
            Timestamp = _clock.Now.AddMinutes(-5),
            Cwd = Cwd,
            Source = AckSource.InferredFocus,
        };
        _changes.Clear();

        Assert.Equal(ApplyOutcome.Stale, Apply(stale));

        Assert.Empty(_changes);
        Assert.Equal(SessionState.Unread, Current.State);
    }

    /// <summary>
    /// The warm-restart case the guard exists for: a replayed event with an independent origin
    /// that <em>would</em> cause a real state change if it were not dropped. Here a stale
    /// permission notification would drag an acknowledged session back into the Needs-You band
    /// and start it chiming again.
    /// </summary>
    [Fact]
    public void A_stale_event_cannot_drag_a_session_backwards()
    {
        GivenWorking();
        _clock.AdvanceMinutes(10);
        Apply(Finished("29 passed"));
        _clock.AdvanceMinutes(1);
        Apply(Acknowledged());
        _changes.Clear();

        var replayed = new Notification
        {
            SessionId = Id,
            Timestamp = _clock.Now.AddMinutes(-30),
            Cwd = Cwd,
            NotificationType = "permission_prompt",
        };

        Assert.Equal(ApplyOutcome.Stale, Apply(replayed));

        Assert.Empty(_changes);
        Assert.Equal(SessionState.Acked, Current.State);
    }

    /// <summary>Two events sharing an instant are both real; only strictly older is stale.</summary>
    [Fact]
    public void An_event_with_the_same_stamp_is_not_stale()
    {
        GivenWorking();

        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("29 passed")));
        Assert.Equal(SessionState.Unread, Current.State);
    }

    // ---- prompt_id correlation on Stop ------------------------------------------------------------

    /// <summary>
    /// The failure this guard exists to prevent: a delayed duplicate <c>Stop</c> from the
    /// previous turn arriving after a new prompt would drag a live Working session back to
    /// Unread — a false "finished" chime on a session that is still running.
    /// </summary>
    [Fact]
    public void A_stop_from_a_previous_turn_cannot_finish_the_current_one()
    {
        Apply(Prompt("first", "p-1"));
        _clock.AdvanceMinutes(1);
        Apply(Finished("first answer", "p-1"));
        _clock.AdvanceMinutes(1);
        Apply(Prompt("second", "p-2"));
        _changes.Clear();

        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Uncorrelated, Apply(Finished("first answer", "p-1")));

        Assert.Empty(_changes);
        Assert.Equal(SessionState.Working, Current.State);
        Assert.Equal("second", Current.Latest.Prompt);
        Assert.Null(Current.Latest.Answer);
    }

    [Fact]
    public void A_stop_matching_the_current_prompt_id_is_accepted()
    {
        Apply(Prompt("second", "p-2"));
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("second answer", "p-2")));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal("second answer", Current.Latest.Answer);
    }

    /// <summary>
    /// Correlation needs an id on both sides. Refusing an uncorrelatable Stop would silently
    /// lose real completions, which is worse than the duplicate the guard catches.
    /// </summary>
    [Fact]
    public void A_stop_without_a_prompt_id_is_still_accepted()
    {
        Apply(Prompt("first", promptId: null));
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("answer", promptId: null)));

        Assert.Equal(SessionState.Unread, Current.State);
    }

    [Fact]
    public void A_stop_is_accepted_when_the_session_has_no_prompt_id_to_correlate_against()
    {
        Apply(Prompt("first", promptId: null));
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("answer", "p-99")));

        Assert.Equal(SessionState.Unread, Current.State);
    }

    // ---- Unknown sessions --------------------------------------------------------------------------

    /// <summary>
    /// TS §I.2: "a session exists in the Registry because the system saw an event from it", and
    /// TS §IV.7 promises the product still shows sessions from their next event on. A session
    /// already running when the dashboard starts must surface from whatever event arrives first.
    /// </summary>
    [Theory]
    [InlineData("Stop", SessionState.Unread)]
    [InlineData("StopFailure", SessionState.Error)]
    [InlineData("Notification", SessionState.NeedsPermission)]
    [InlineData("SessionEnd", SessionState.Ended)]
    [InlineData("SessionStart", SessionState.Acked)]
    [InlineData("UserPromptSubmit", SessionState.Working)]
    public void An_event_for_an_unknown_session_creates_it(string variant, SessionState expected)
    {
        InboundEvent inbound = variant switch
        {
            "Stop" => Finished(),
            "StopFailure" => Failed(),
            "Notification" => Notified("permission_prompt"),
            "SessionEnd" => Ended(),
            "SessionStart" => Started(),
            _ => Prompt(),
        };

        Assert.Equal(ApplyOutcome.Applied, Apply(inbound));

        Assert.Equal(expected, Current.State);
        Assert.Equal(SessionChangeKind.Added, Assert.Single(_changes).Kind);
    }

    [Fact]
    public void A_stop_before_any_prompt_records_the_answer_without_inventing_a_question()
    {
        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("answer with no question")));

        Assert.Equal(string.Empty, Current.Latest.Prompt);
        Assert.Equal("answer with no question", Current.Latest.Answer);
        Assert.True(Current.Latest.IsAnswered);
    }

    /// <summary>
    /// A synthetic ack is the dashboard reporting that the operator saw something, not an
    /// observation of a session. With nothing to acknowledge there is nothing to create, and
    /// materializing a blank row from one would put a session on screen that has never been
    /// observed.
    /// </summary>
    [Fact]
    public void An_ack_for_an_unknown_session_creates_nothing()
    {
        Assert.Equal(ApplyOutcome.Ignored, Apply(Acknowledged()));

        Assert.Empty(_registry.Sessions);
        Assert.Empty(_changes);
    }

    [Fact]
    public void An_event_naming_no_session_is_ignored()
    {
        var anonymous = new Stop { SessionId = default, Timestamp = _clock.Now, Cwd = Cwd };

        Assert.Equal(ApplyOutcome.Ignored, Apply(anonymous));

        Assert.Empty(_registry.Sessions);
    }

    // ---- Ended --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Stop")]
    [InlineData("StopFailure")]
    [InlineData("Notification")]
    [InlineData("Ack")]
    public void An_ended_session_ignores_further_activity(string variant)
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Ended());
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        InboundEvent inbound = variant switch
        {
            "Stop" => Finished(),
            "StopFailure" => Failed(),
            "Notification" => Notified("permission_prompt"),
            _ => Acknowledged(),
        };

        Assert.Equal(ApplyOutcome.Ignored, Apply(inbound));

        Assert.Empty(_changes);
        Assert.Equal(SessionState.Ended, Current.State);
    }

    /// <summary>
    /// <c>SessionEnd</c>'s matchers include <c>resume</c> and <c>SessionStart</c>'s include
    /// <c>resume</c> and <c>fork</c> (Impl §9.1), so one session id can legitimately end and
    /// start again. Leaving it dimmed as Ended would simply be wrong.
    /// </summary>
    [Fact]
    public void An_ended_session_revives_on_SessionStart()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Ended("resume"));
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Started("resume")));

        Assert.Equal(SessionState.Acked, Current.State);
    }

    [Fact]
    public void An_ended_session_revives_on_a_new_prompt()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);
        Apply(Ended());
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Prompt("back again", "p-2")));

        Assert.Equal(SessionState.Working, Current.State);
    }

    // ---- SessionStart and cwd -------------------------------------------------------------------------

    /// <summary>Impl §9.1: a resume "surfaces a pre-existing one" — it does not restart its turn.</summary>
    [Fact]
    public void SessionStart_does_not_disturb_a_live_sessions_state()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Duplicate, Apply(Started("resume")));

        Assert.Equal(SessionState.Working, Current.State);
    }

    [Fact]
    public void CwdChanged_moves_the_session_and_re_derives_its_group()
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Moved(@"C:\projects\elsewhere")));

        Assert.Equal(@"C:\projects\elsewhere", Current.Cwd);
        Assert.Equal(GroupKeys.ForWorkspace(@"C:\projects\elsewhere"), Current.Group);
        Assert.Equal(SessionState.Working, Current.State);
    }

    [Fact]
    public void CwdChanged_to_the_same_directory_has_no_effect()
    {
        GivenWorking();
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Duplicate, Apply(Moved(Cwd)));
        Assert.Empty(_changes);
    }

    /// <summary>
    /// A session with no known workspace groups alone rather than pooling with every other
    /// directory-less session.
    /// </summary>
    [Fact]
    public void A_session_without_a_cwd_groups_under_its_own_id()
    {
        Apply(new UserPromptSubmit
        {
            SessionId = Id, Timestamp = _clock.Now, Cwd = string.Empty, Prompt = "p", PromptId = "p-1",
        });

        Assert.Equal(GroupKeys.ForUngrouped(Id), Current.Group);
    }

    // ---- Change notification ----------------------------------------------------------------------------

    [Fact]
    public void The_first_event_reports_an_addition_and_later_ones_report_updates()
    {
        Apply(Prompt());
        _clock.AdvanceMinutes(1);
        Apply(Finished());

        Assert.Equal(
            [SessionChangeKind.Added, SessionChangeKind.Updated],
            _changes.Select(c => c.Kind));
    }

    [Fact]
    public void The_notification_carries_the_session_as_it_now_stands()
    {
        Apply(Prompt("run the tests"));

        var change = Assert.Single(_changes);
        Assert.Equal(Id, change.Session.Id);
        Assert.Equal(SessionState.Working, change.Session.State);
        Assert.Same(Current, change.Session);
    }

    [Fact]
    public void Dropped_events_raise_nothing()
    {
        GivenWorking();
        _changes.Clear();
        _clock.AdvanceMinutes(1);

        Apply(Notified("agent_completed"));
        Apply(Acknowledged());
        Apply(Finished("x", "p-mismatch"));

        Assert.Empty(_changes);
    }

    // ---- Registry mechanics ------------------------------------------------------------------------------

    [Fact]
    public void Sessions_are_kept_apart_by_id()
    {
        Apply(Prompt());
        Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("s-2"), Timestamp = _clock.Now, Cwd = Cwd,
            Prompt = "other", PromptId = "q-1",
        });

        Assert.Equal(2, _registry.Sessions.Count);
        Assert.Equal("do the thing", _registry.Sessions[Id].Latest.Prompt);
        Assert.Equal("other", _registry.Sessions[new SessionId("s-2")].Latest.Prompt);
    }

    /// <summary>
    /// The Registry needs the shared guard; it will not quietly make its own.
    /// </summary>
    /// <remarks>
    /// It used to default to a private one, which meant deleting the single registration in
    /// <c>AppHost</c> left the Registry and the sound engine each holding a guard of their own —
    /// mutual exclusion between them silently gone, and every test that built its own still
    /// green. The lock-free design rests on one shared region (T1.12b).
    /// </remarks>
    [Fact]
    public void A_registry_needs_the_shared_guard()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionRegistry(null!));
    }

    [Fact]
    public void Applying_null_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Apply(null!));
    }

    [Fact]
    public void A_new_registry_holds_nothing()
    {
        Assert.Empty(new SessionRegistry(new SingleWriterGuard()).Sessions);
    }

    /// <summary>The log is what explains how a row got the way it is.</summary>
    [Fact]
    public void Each_applied_event_appends_one_transition()
    {
        Apply(Prompt());
        _clock.AdvanceMinutes(1);
        Apply(Finished());
        _clock.AdvanceMinutes(1);
        Apply(Acknowledged());

        Assert.Equal(
            [SessionState.Working, SessionState.Unread, SessionState.Acked],
            Current.Transitions.Select(t => t.To));
    }


    // ---- PostToolBatch: the turn resumed (TS §IV.1, 2026-08-25; issue #2) ------------------------

    /// <summary>
    /// <strong>The regression the operator hit (issue #2).</strong> A session blocked on a
    /// permission returns to Working when the turn is seen doing work again.
    /// </summary>
    /// <remarks>
    /// The operator answered the permission, Claude carried on, and the row stayed red at the top
    /// of Needs You for the rest of the turn — claiming to be blocked on someone who had already
    /// unblocked it. Nothing fires when a permission is approved, so resumption is inferred from
    /// the session executing again.
    /// </remarks>
    [Fact]
    public void A_resolved_permission_returns_to_Working_when_the_turn_resumes()
    {
        GivenInState(SessionState.NeedsPermission);
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Batched()));
        Assert.Equal(SessionState.Working, Current.State);
    }

    /// <summary>The same defect by a different entry: a resolved question resumes too.</summary>
    [Fact]
    public void A_resolved_question_returns_to_Working_when_the_turn_resumes()
    {
        GivenInState(SessionState.NeedsQuestion);
        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Batched()));
        Assert.Equal(SessionState.Working, Current.State);
    }

    /// <summary>
    /// An errored turn that recovers on retry resumes, and stops reporting the error kind.
    /// </summary>
    /// <remarks>
    /// A <c>StopFailure</c> that the agent retries produces tool activity with no new prompt, so
    /// without this the session would sit in Error until the turn ended. The error kind is
    /// cleared with it: a session that is working again is not still failing for that reason, and
    /// leaving the kind behind would put a stale cause on a live row.
    /// </remarks>
    [Fact]
    public void An_errored_turn_that_retries_returns_to_Working()
    {
        GivenInState(SessionState.Error);
        Assert.NotNull(Current.ErrorKind);

        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Applied, Apply(Batched()));
        Assert.Equal(SessionState.Working, Current.State);
        Assert.Null(Current.ErrorKind);
    }

    /// <summary>
    /// <strong>An unread result is never un-read.</strong> This is the control that makes the
    /// three above mean something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "It became Working" is satisfied by a handler that moves <em>everything</em> to Working,
    /// which would drag finished-but-unseen results back into the working band — issue #1's
    /// failure mirrored, and the worse direction: #1 was loud and wrong, this would be quiet and
    /// wrong. A late batch arriving after a <c>Stop</c> is exactly the shape that would do it.
    /// </para>
    /// <para>
    /// Asserted as a decline as well as an unchanged state, so a handler that "changed it to
    /// Unread again" — leaving the state right and the recency wrong — is caught too.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unread_result_is_never_resumed()
    {
        GivenInState(SessionState.Unread);
        var before = Current;

        _clock.AdvanceMinutes(1);

        Assert.Equal(ApplyOutcome.Ignored, Apply(Batched()));
        Assert.Equal(SessionState.Unread, Current.State);
        Assert.True(Current.Latest.IsAnswered);
        Assert.Equal(before.LastActivity, Current.LastActivity);
    }

    /// <summary>
    /// A session already Working is left exactly as it is — the overwhelmingly common case.
    /// </summary>
    /// <remarks>
    /// One of these arrives per tool batch for the whole life of a turn, so it must not record a
    /// transition or advance recency each time. Asserted on the transition log, because "still
    /// Working" alone is satisfied by a handler that rewrites the session to the same state and
    /// quietly bumps it up the ordering the UI sorts by.
    /// </remarks>
    [Fact]
    public void A_working_session_is_untouched_by_its_own_tool_batches()
    {
        GivenInState(SessionState.Working);
        var before = Current;

        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Ignored, Apply(Batched()));
        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Ignored, Apply(Batched()));

        Assert.Equal(SessionState.Working, Current.State);
        Assert.Equal(before.LastActivity, Current.LastActivity);
        Assert.Equal(before.Transitions.Count, Current.Transitions.Count);
    }

    /// <summary>
    /// Every state is classified as one a resumed turn recovers or one it leaves alone.
    /// </summary>
    /// <remarks>
    /// Driven from <see cref="SessionState"/> so a state added later must be classified rather
    /// than defaulting quietly into the untouched half. This shape has now earned its keep three
    /// times: <c>AttentionOrder</c>, the tray palette, and the notification kinds.
    /// </remarks>
    [Fact]
    public void Every_state_is_classified_for_a_resumed_turn()
    {
        var resumes = new Dictionary<SessionState, bool>
        {
            [SessionState.NeedsPermission] = true,
            [SessionState.NeedsQuestion] = true,
            [SessionState.Error] = true,

            // Working is already right; Unread must never be un-read; Acked and Ended are over.
            [SessionState.Working] = false,
            [SessionState.Unread] = false,
            [SessionState.Acked] = false,
            [SessionState.Ended] = false,
        };

        Assert.Equal(Enum.GetValues<SessionState>().Length, resumes.Count);

        foreach (var (state, expected) in resumes)
        {
            var fixture = new SessionRegistryTests();
            fixture.GivenInState(state);
            fixture._clock.AdvanceMinutes(1);

            var outcome = fixture.Apply(fixture.Batched());
            var resumed = outcome == ApplyOutcome.Applied
                && fixture.Current.State == SessionState.Working;

            Assert.True(
                resumed == expected,
                $"{state}: expected resume={expected} but the batch produced {outcome} "
                + $"leaving {fixture.Current.State}.");
        }
    }

    /// <summary>A tool batch, carrying nothing about the batch.</summary>
    private PostToolBatch Batched() => new()
    {
        SessionId = Id,
        Timestamp = _clock.Now,
        Cwd = Cwd,
    };
    /// <summary>Drives the session into <paramref name="state"/> using ordinary events.</summary>
    private void GivenInState(SessionState state)
    {
        GivenWorking();
        _clock.AdvanceMinutes(1);

        switch (state)
        {
            case SessionState.Working:
                break;
            case SessionState.Unread:
                Apply(Finished());
                break;
            case SessionState.NeedsPermission:
                Apply(Notified("permission_prompt"));
                break;
            case SessionState.NeedsQuestion:
                Apply(Notified("agent_needs_input"));
                break;
            case SessionState.Error:
                Apply(Failed());
                break;
            case SessionState.Acked:
                Apply(Finished());
                _clock.AdvanceMinutes(1);
                Apply(Acknowledged());
                break;
            case SessionState.Ended:
                Apply(Ended());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unhandled state.");
        }

        Assert.Equal(state, Current.State);
    }
}
