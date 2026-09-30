using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// A finished turn's row counts from when Claude finished (T1.47, issue #59).
/// </summary>
/// <remarks>
/// <para>
/// The row read time in state, so a result that finished four hours ago read "0 s ago" the moment
/// the operator clicked Ack, and again when the session ended. The operator ruled that the time
/// that matters is the finish: Unread, Acked and Ended read <see cref="Exchange.AnsweredAt"/>, and
/// <see cref="Session.EnteredAt"/> when there is no answer — a turn acknowledged or closed midway.
/// </para>
/// <para>
/// Every event goes through a real <see cref="SessionRegistry"/>, because what
/// <see cref="Exchange.AnsweredAt"/> holds is the Registry's decision, and a hand-built session
/// would test the row against an assumption about it.
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

    /// <summary>Acceptance 2: an acknowledged row reads the same age after the click as before.</summary>
    [Fact]
    public void Acknowledging_a_finished_row_keeps_its_age()
    {
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
    }

    /// <summary>Acceptance 3: a finished row that ends reads the same age after the end as before.</summary>
    [Fact]
    public void Ending_a_finished_row_keeps_its_age()
    {
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
    }

    /// <summary>Acknowledged, then ended: still the finish, through both moves.</summary>
    [Fact]
    public void Acknowledged_then_ended_still_reads_the_finish()
    {
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
    }

    /// <summary>Acceptance 4: a session that ends in the middle of a turn counts from its end.</summary>
    [Fact]
    public void A_session_ended_mid_turn_counts_from_its_end()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(20);
        var ended = _clock.Now;
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);
        Assert.Null(Current.Latest.AnsweredAt);

        _clock.AdvanceMinutes(7);
        Assert.Equal(_clock.Now - ended, AgeNow());
    }

    /// <summary>
    /// The same fallback for an acknowledgment: a permission prompt acknowledged mid-turn has no
    /// answer, so the row counts from the click.
    /// </summary>
    [Fact]
    public void A_session_acknowledged_mid_turn_counts_from_the_ack()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(5);
        Apply(new Notification { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt" });
        _clock.AdvanceMinutes(5);
        var acked = _clock.Now;
        Apply(Ack());

        Assert.Equal(SessionState.Acked, Current.State);
        Assert.Null(Current.Latest.AnsweredAt);

        _clock.AdvanceMinutes(7);
        Assert.Equal(_clock.Now - acked, AgeNow());
    }

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
        Apply(new Notification { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt" });
        _clock.AdvanceMinutes(4);
        Apply(Stop("p-1"));

        UnreadAgeUnchanged();
    }

    /// <summary>
    /// After a Waiting stretch and a task notification, <see cref="Exchange.AnsweredAt"/> is the
    /// final Stop, not the one that entered Waiting.
    /// </summary>
    /// <remarks>
    /// The Stop that enters Waiting sets it. The task notification continues the ask and clears it
    /// (<c>SessionRegistry.Continued</c>: <c>AnsweredAt = null</c>). The turn's own Stop then sets
    /// it again. So an Unread row after Waiting, and the Acked row after it, count from the last
    /// word, as the operator's ruling asks.
    /// </remarks>
    [Fact]
    public void After_waiting_and_a_task_notification_the_finish_is_the_final_stop()
    {
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
    }

    /// <summary>
    /// A session that ends while Waiting has an answer: the Stop that entered Waiting, which is
    /// what Claude had said so far. By the ruling's table it reads that, not the close.
    /// </summary>
    [Fact]
    public void A_session_ended_while_waiting_reads_the_stop_that_entered_waiting()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(2);
        var stopped = _clock.Now;
        Apply(Stop("p-1", new BackgroundTask("task-1", BackgroundTaskKind.Shell, "build")));

        _clock.AdvanceMinutes(10);
        Apply(End());

        Assert.Equal(SessionState.Ended, Current.State);
        Assert.Equal(_clock.Now - stopped, AgeNow());
    }

    /// <summary>The states outside the ruling keep the clock they had.</summary>
    [Fact]
    public void Blocked_states_still_count_time_in_state()
    {
        Apply(Prompt("p-1"));
        _clock.AdvanceMinutes(5);
        var blocked = _clock.Now;
        Apply(new Notification { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt" });

        _clock.AdvanceMinutes(4);
        Assert.Equal(_clock.Now - blocked, AgeNow());
    }

    private void UnreadAgeUnchanged()
    {
        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(Current.EnteredAt, Current.Latest.AnsweredAt);

        var at = _clock.Now + TimeSpan.FromMinutes(9);
        var row = new SessionViewModel(Current);
        row.RefreshAge(at);

        Assert.Equal(at - Current.EnteredAt, row.Age);
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

    private Ack Ack() => new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual };

    private SessionEnd End() => new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" };
}
