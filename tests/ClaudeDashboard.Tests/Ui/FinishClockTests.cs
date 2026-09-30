using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// An acknowledgment or a close never restarts a row's clock (T1.47, issue #59, and the operator's
/// rulings of 2026-09-29).
/// </summary>
/// <remarks>
/// <para>
/// The row read time in state, so a result that finished four hours ago read "0 s ago" the moment
/// the operator clicked Ack, and again when the session ended; and an Interrupted row read the time
/// since the sweep, a threshold after the silence began. The rulings: Acked and Ended keep the
/// moment that mattered in the state they came from — the finish, the block, or the silence — and
/// only a session left mid-turn counts from the ack or the close. Interrupted counts from the last
/// event heard. The rule is transitive.
/// </para>
/// <para>
/// Every event goes through a real <see cref="SessionRegistry"/>, because the anchor is the
/// Registry's decision, and a hand-built session would test the row against an assumption about it.
/// </para>
/// <para>
/// <strong>"You asked" reads the ask throughout.</strong> Each test that moves a finished row also
/// checks <see cref="SessionViewModel.AskedAgoText"/>, because the row's clock and the expanded
/// row's ask are separate lines, and moving one must not move the other (the first review, M1).
/// </para>
/// </remarks>
public sealed class FinishClockTests
{
    private const string Cwd = @"C:\projects\dashboard";
    private static readonly SessionId Id = new("s-1");

    private const string TaskNotification =
        "<task-notification>\n<task-id>b7</task-id>\n<status>completed</status>\n</task-notification>";

    private readonly FakeClock _clock = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());

    private Session Current => _registry.Sessions[Id];

    // ---- From Unread: the finish -----------------------------------------------------------------

    /// <summary>Acceptance 2: an acknowledged row reads the same age after the click as before.</summary>
    [Fact]
    public void Acknowledging_a_finished_row_keeps_its_age()
    {
        var asked = _clock.Now;
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        var finished = _clock.Now;
        Apply(Stop("p-1"));

        _clock.AdvanceMinutes(240);
        var before = AgeNow();

        Apply(Ack());

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Equal(before, AgeNow());
        Assert.Equal(_clock.Now - finished, AgeNow());
        AskStill(asked);
    }

    /// <summary>Acceptance 3: a finished row that ends reads the same age after the end as before.</summary>
    [Fact]
    public void Ending_a_finished_row_keeps_its_age()
    {
        var asked = _clock.Now;
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        var finished = _clock.Now;
        Apply(Stop("p-1"));

        _clock.AdvanceMinutes(240);
        var before = AgeNow();

        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);
        Assert.Equal(before, AgeNow());
        Assert.Equal(_clock.Now - finished, AgeNow());
        AskStill(asked);
    }

    /// <summary>Transitive: Unread, then Acked, then Ended still reads the finish.</summary>
    [Fact]
    public void Acknowledged_then_ended_still_reads_the_finish()
    {
        var asked = _clock.Now;
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        var finished = _clock.Now;
        Apply(Stop("p-1"));

        _clock.AdvanceMinutes(30);
        Apply(Ack());
        _clock.AdvanceMinutes(30);
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);
        Assert.Equal(_clock.Now - finished, AgeNow());
        AskStill(asked);
    }

    // ---- From a block: when it became blocked ----------------------------------------------------

    /// <summary>A permission prompt acknowledged, then closed, reads the block both times.</summary>
    [Fact]
    public void An_acknowledged_permission_prompt_reads_when_it_became_blocked_through_the_end()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(5);
        var blocked = _clock.Now;
        Apply(Blocked("permission_prompt"));

        _clock.AdvanceMinutes(15);
        Apply(Ack());

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Equal(_clock.Now - blocked, AgeNow());

        _clock.AdvanceMinutes(15);
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);
        Assert.Equal(_clock.Now - blocked, AgeNow());
    }

    /// <summary>An error acknowledged reads when the turn died.</summary>
    [Fact]
    public void An_acknowledged_error_reads_when_the_turn_died()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(5);
        var died = _clock.Now;
        Apply(new StopFailure { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1", ErrorKind = "rate_limit" });

        _clock.AdvanceMinutes(15);
        Apply(Ack());

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Equal(_clock.Now - died, AgeNow());
    }

    /// <summary>
    /// The ruling's own example: Waiting, then a permission prompt, then Ack reads when the prompt
    /// came — not the Stop that entered Waiting, and not the click.
    /// </summary>
    [Fact]
    public void A_permission_prompt_raised_while_waiting_and_acknowledged_reads_the_prompt()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(2);
        Apply(Stop("p-1", new BackgroundTask("task-1", BackgroundTaskKind.Subagent, "review")));
        Assert.Equal(SessionState.Waiting, Current.State);

        _clock.AdvanceMinutes(10);
        var blocked = _clock.Now;
        Apply(Blocked("permission_prompt"));

        _clock.AdvanceMinutes(10);
        Apply(Ack());

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Equal(_clock.Now - blocked, AgeNow());
    }

    // ---- From Interrupted: the silence -----------------------------------------------------------

    /// <summary>
    /// R2: an Interrupted row counts from the last event heard. The operator's case: silent at
    /// 21:43:54, swept at 21:54:07, and it read "10 min ago" when the truth was 20.
    /// </summary>
    [Fact]
    public void An_interrupted_row_counts_from_the_last_event_heard_not_the_sweep()
    {
        _clock.Now = new DateTimeOffset(2026, 9, 29, 21, 30, 0, TimeSpan.Zero);
        Apply(Prompt("p-1"));
        _clock.Now = new DateTimeOffset(2026, 9, 29, 21, 43, 54, TimeSpan.Zero);
        var heard = _clock.Now;
        Heard();

        _clock.Now = new DateTimeOffset(2026, 9, 29, 21, 54, 7, TimeSpan.Zero);
        Assert.Single(_registry.SweepSilent(_clock.Now, SilenceWatch.DefaultThreshold));
        Assert.Equal(SessionState.Interrupted, Current.State);
        Assert.Equal(_clock.Now, Current.EnteredAt);

        _clock.Now = heard.AddMinutes(20);

        Assert.Equal(TimeSpan.FromMinutes(20), AgeNow());
    }

    /// <summary>
    /// The trap the ruling names: SessionEnd advances LastHeardAt, so Interrupted, then Ended must
    /// read the silence captured before the end moved it.
    /// </summary>
    [Fact]
    public void Interrupted_then_ended_still_reads_the_silence()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(3);
        var heard = _clock.Now;
        Heard();

        _clock.AdvanceMinutes(12);
        Assert.Single(_registry.SweepSilent(_clock.Now, SilenceWatch.DefaultThreshold));

        _clock.AdvanceMinutes(5);
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);
        Assert.Equal(_clock.Now, Current.LastHeardAt);
        Assert.Equal(_clock.Now - heard, AgeNow());
    }

    // ---- Mid-turn: the ack or the close ----------------------------------------------------------

    /// <summary>Acceptance 4: a session that ends in the middle of a turn counts from its end.</summary>
    [Fact]
    public void A_session_ended_mid_turn_counts_from_its_end()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        var ended = _clock.Now;
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);

        _clock.AdvanceMinutes(7);
        Assert.Equal(_clock.Now - ended, AgeNow());
    }

    /// <summary>
    /// A session closed while Waiting counts from the close: Waiting is mid-turn, and the Stop that
    /// entered it is not a finish (the ruling of 2026-09-29, replacing the literal reading).
    /// </summary>
    [Fact]
    public void A_session_closed_while_waiting_counts_from_the_close()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(2);
        Apply(Stop("p-1", new BackgroundTask("task-1", BackgroundTaskKind.Shell, "build")));

        _clock.AdvanceMinutes(10);
        var closed = _clock.Now;
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);

        _clock.AdvanceMinutes(5);
        Assert.Equal(_clock.Now - closed, AgeNow());
    }

    // ---- Unread itself ---------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 5: an Unread row reads the age it read before — the Stop that answers is the
    /// event that enters Unread, so the finish and the time in state are one instant.
    /// </summary>
    [Fact]
    public void An_unread_row_reads_the_same_age_as_before()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        Apply(Stop("p-1"));

        UnreadAgeUnchanged();
    }

    /// <summary>The same from a permission prompt that the turn's Stop resolves.</summary>
    [Fact]
    public void An_unread_row_reached_from_a_permission_prompt_reads_the_same_age_as_before()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(3);
        Apply(Blocked("permission_prompt"));
        _clock.AdvanceMinutes(4);
        Apply(Stop("p-1"));

        UnreadAgeUnchanged();
    }

    /// <summary>
    /// After a Waiting stretch and a task notification, the finish is the final Stop, not the one
    /// that entered Waiting.
    /// </summary>
    /// <remarks>
    /// The Stop that enters Waiting sets <see cref="Exchange.AnsweredAt"/>. The task notification
    /// continues the ask and clears it (<c>SessionRegistry.Continued</c>). The turn's own Stop then
    /// sets it again, and the Unread anchor is taken from it.
    /// </remarks>
    [Fact]
    public void After_waiting_and_a_task_notification_the_finish_is_the_final_stop()
    {
        var asked = _clock.Now;
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(2);
        var first = _clock.Now;
        Apply(Stop("p-1", new BackgroundTask("task-1", BackgroundTaskKind.Shell, "build")));

        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Equal(first, Current.Latest.AnsweredAt);

        _clock.AdvanceMinutes(10);
        Apply(Prompt("p-2", TaskNotification));

        Assert.Equal(SessionState.Working, Current.State);
        Assert.Null(Current.Latest.AnsweredAt);

        _clock.AdvanceMinutes(3);
        var last = _clock.Now;
        Apply(Stop("p-2"));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(last, Current.Latest.AnsweredAt);
        UnreadAgeUnchanged();

        _clock.AdvanceMinutes(60);
        Apply(Ack());

        Assert.Equal(_clock.Now - last, AgeNow());
        AskStill(asked);
    }

    /// <summary>
    /// A quiet tick puts the row back as it was (T1.44), and that includes its clock. An
    /// acknowledged row keeps reading the finish across a tick of its own scheduled job, not the
    /// tick's prompt.
    /// </summary>
    [Fact]
    public void A_quiet_tick_puts_back_the_rows_clock()
    {
        const string Cron = "check the queue";

        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        var finished = _clock.Now;
        Apply(Stop("p-1") with { ScheduledPrompts = ScheduledPrompts.Of([Cron]) });
        _clock.AdvanceMinutes(5);
        Apply(Ack());

        _clock.AdvanceMinutes(30);
        Apply(Prompt("p-2", Cron));
        Assert.Equal(SessionState.Working, Current.State);

        _clock.AdvanceMinutes(1);
        Apply(Stop("p-2") with { LastAssistantMessage = QuietTicks.Sentinel, ScheduledPrompts = ScheduledPrompts.Of([Cron]) });

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Equal(_clock.Now - finished, AgeNow());
    }

    /// <summary>The states outside the ruling keep the clock they had.</summary>
    [Fact]
    public void Blocked_states_still_count_time_in_state()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(5);
        var blocked = _clock.Now;
        Apply(Blocked("permission_prompt"));

        _clock.AdvanceMinutes(4);
        Assert.Equal(_clock.Now - blocked, AgeNow());
    }

    private void UnreadAgeUnchanged()
    {
        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(Current.EnteredAt, Current.Latest.AnsweredAt);
        Assert.Equal(Current.EnteredAt, Current.ClockAnchor);

        var at = _clock.Now + TimeSpan.FromMinutes(9);
        var row = new SessionViewModel(Current);
        row.RefreshAge(at);

        Assert.Equal(at - Current.EnteredAt, row.Age);
    }

    /// <summary>The expanded row's "You asked" still reads the ask, whatever the row's clock reads.</summary>
    private void AskStill(DateTimeOffset asked)
    {
        var row = new SessionViewModel(Current);
        row.RefreshAge(_clock.Now);

        Assert.Equal(asked, Current.Latest.StartedAt);
        Assert.Equal($"{RowVisuals.Duration(_clock.Now - asked)} ago", row.AskedAgoText);
        Assert.NotEqual(row.Age, _clock.Now - asked);
    }

    private TimeSpan AgeNow()
    {
        var row = new SessionViewModel(Current);
        row.RefreshAge(_clock.Now);
        return row.Age;
    }

    private void Apply(InboundEvent inboundEvent) =>
        Assert.Equal(ApplyOutcome.Applied, _registry.Apply(inboundEvent));

    private UserPromptSubmit Prompt(string promptId, string text = "refactor the pipeline") => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, Prompt = text,
    };

    private Stop Stop(string promptId, params BackgroundTask[] running) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, LastAssistantMessage = "done",
        BackgroundTasks = running,
    };

    /// <summary>
    /// A tool batch while Working: ignored by the state machine, but it is an event heard, so it
    /// advances <see cref="Session.LastHeardAt"/>.
    /// </summary>
    private void Heard()
    {
        Assert.Equal(ApplyOutcome.Ignored, _registry.Apply(new PostToolBatch { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd }));
        Assert.Equal(_clock.Now, Current.LastHeardAt);
    }

    private Notification Blocked(string type) =>
        new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = type };

    private Ack Ack() => new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual };

    private SessionEnd End() => new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" };
}
