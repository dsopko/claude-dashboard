using System.ComponentModel;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// One clock for a piece of work: "You asked" and the row's elapsed time both count from the
/// prompt that began it (T1.40, issue #51).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The real Registry, read through the real row.</strong> Each test drives
/// <see cref="SessionRegistry"/> with events under a <see cref="FakeClock"/> and reads the result
/// through <see cref="SessionViewModel"/>, the object the row binds to — so the rule is checked
/// where it is decided (Core, on the event and in the Registry) and where it is shown.
/// </para>
/// <para>
/// No clock literal is compared to a phrase except through <see cref="RowVisuals.Duration"/>, the
/// row's own relative form, so these state which instant is counted from rather than how it is
/// spelled.
/// </para>
/// </remarks>
public sealed class AskAnchorTests
{
    private const string Cwd = @"C:\projects\dashboard";
    private const string Asked = "refactor the pipeline";
    private static readonly SessionId Id = new("s-1");

    /// <summary>What a task notification looks like on the wire: the tag first, then the rest.</summary>
    private const string TaskNotification =
        "<task-notification>\n<task-id>b7</task-id>\n<status>completed</status>\n</task-notification>";

    private readonly FakeClock _clock = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());

    private Session Current => _registry.Sessions[Id];

    // ---- The one clock across flips (acceptance 2) ---------------------------------------------

    /// <summary>
    /// <strong>Working → NeedsPermission → Working → Interrupted → Working keeps one elapsed
    /// time, from the original prompt, on the collapsed and the expanded row.</strong>
    /// </summary>
    /// <remarks>
    /// Before T1.40 the collapsed row's clock was time in state, so every one of these flips
    /// restarted it. Each step is checked at the instant it happens and again a minute later, so
    /// a clock that restarted at the flip would read one minute where the anchor reads many.
    /// </remarks>
    [Fact]
    public void Flipping_between_working_states_keeps_one_elapsed_time()
    {
        var asked = _clock.Now;
        Apply(Prompt(Asked, "p-1"));
        var askedAt = Row().AskedAtText;

        _clock.AdvanceMinutes(2);
        Apply(new Notification { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt" });
        Assert.Equal(SessionState.NeedsPermission, Current.State);
        OneClock(asked, askedAt);

        _clock.AdvanceMinutes(3);
        Apply(Batch());
        Assert.Equal(SessionState.Working, Current.State);
        OneClock(asked, askedAt);

        _clock.AdvanceMinutes(11);
        Assert.Single(_registry.SweepSilent(_clock.Now, SilenceWatch.DefaultThreshold));
        Assert.Equal(SessionState.Interrupted, Current.State);
        OneClock(asked, askedAt);

        _clock.AdvanceMinutes(1);
        Apply(Batch());
        Assert.Equal(SessionState.Working, Current.State);
        OneClock(asked, askedAt);
    }

    // ---- A task notification is a continuation (acceptance 3) ----------------------------------

    /// <summary>
    /// <strong>A task notification moves neither "You asked", its text, nor the elapsed
    /// time</strong> — and the session still goes to Working, as any prompt makes it.
    /// </summary>
    [Fact]
    public void A_task_notification_changes_nothing_the_row_counts_from()
    {
        var asked = _clock.Now;
        Apply(Prompt(Asked, "p-1"));
        var askedAt = Row().AskedAtText;

        _clock.AdvanceMinutes(4);
        Apply(Finished("p-1"));
        Assert.Equal(SessionState.Unread, Current.State);

        _clock.AdvanceMinutes(3);
        Assert.Equal(ApplyOutcome.Applied, Apply(Prompt(TaskNotification, "p-2")));

        Assert.Equal(SessionState.Working, Current.State);
        Assert.Equal(Asked, Current.Latest.Prompt);
        Assert.Equal(asked, Current.Latest.StartedAt);
        Assert.Equal(Asked, Row().Prompt);
        OneClock(asked, askedAt);
    }

    /// <summary>
    /// The continuation's own Stop still lands: it carries the new turn's <c>prompt_id</c>, and the
    /// continued exchange tracks it.
    /// </summary>
    /// <remarks>
    /// The trap in keeping the old exchange whole: <c>Stop</c> correlates on
    /// <see cref="Exchange.PromptId"/>, so an anchor that kept the old id would decline the
    /// continuation's Stop as belonging to a finished turn, and the session would never reach
    /// Unread. The answer is the new turn's, and the ask is still the original.
    /// </remarks>
    [Fact]
    public void The_continuations_own_stop_is_correlated_and_answers_it()
    {
        Apply(Prompt(Asked, "p-1"));
        _clock.AdvanceMinutes(4);
        Apply(Finished("p-1", "first half done"));
        _clock.AdvanceMinutes(3);
        Apply(Prompt(TaskNotification, "p-2"));

        Assert.Null(Current.Latest.Answer);

        _clock.AdvanceMinutes(2);
        Assert.Equal(ApplyOutcome.Applied, Apply(Finished("p-2", "all of it done")));

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal("all of it done", Current.Latest.Answer);
        Assert.Equal(Asked, Current.Latest.Prompt);
    }

    /// <summary>A redelivered continuation is a duplicate, as any redelivered prompt is.</summary>
    [Fact]
    public void A_redelivered_task_notification_is_a_duplicate()
    {
        Apply(Prompt(Asked, "p-1"));
        _clock.AdvanceMinutes(1);
        Apply(Prompt(TaskNotification, "p-2"));
        var before = Current;

        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Duplicate, Apply(Prompt(TaskNotification, "p-2")));

        // Without a prompt_id the rebuilt exchange is identical, and the Registry's no-change
        // check declines it as a duplicate (TS §IV.1: re-applying the current state is a no-op) —
        // idempotent without a guard of its own.
        _clock.AdvanceMinutes(1);
        Apply(Prompt(TaskNotification, promptId: null));
        var settled = Current;

        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Duplicate, Apply(Prompt(TaskNotification, promptId: null)));
        // Nothing the row counts from moves. LastHeardAt does, by the Registry's existing rule:
        // any event, declined or not, proves the session alive to the silence watch.
        Assert.Equal(settled.Latest, Current.Latest);
        Assert.Equal(settled.EnteredAt, Current.EnteredAt);
        Assert.Equal(settled.LastActivity, Current.LastActivity);
        Assert.Equal(before.Latest.StartedAt, settled.Latest.StartedAt);
    }

    /// <summary>
    /// A session first seen through a task notification has no known ask: the notification is
    /// not shown as the operator's question.
    /// </summary>
    [Fact]
    public void A_session_first_seen_through_a_task_notification_has_no_ask_to_show()
    {
        Apply(Prompt(TaskNotification, "p-9"));

        Assert.Equal(SessionState.Working, Current.State);
        Assert.Equal(string.Empty, Current.Latest.Prompt);
        Assert.Equal(_clock.Now, Current.Latest.StartedAt);
    }

    // ---- What starts a new ask (acceptance 4) --------------------------------------------------

    /// <summary>The operator's own prompt starts a new ask: new text, new anchor.</summary>
    [Fact]
    public void An_operators_prompt_starts_a_new_ask()
    {
        Apply(Prompt(Asked, "p-1"));
        _clock.AdvanceMinutes(4);
        Apply(Finished("p-1"));

        _clock.AdvanceMinutes(3);
        var second = _clock.Now;
        Apply(Prompt("now write the tests", "p-2"));

        Assert.Equal("now write the tests", Current.Latest.Prompt);
        NewAnchor(second);
    }

    /// <summary>
    /// A cross-session message starts a new ask — the operator's ruling on the issue: it is new
    /// work for the session receiving it, though nobody typed it there.
    /// </summary>
    [Fact]
    public void A_cross_session_message_starts_a_new_ask()
    {
        const string Message = "<cross-session-message from=\"director\">Task T9: do the next thing</cross-session-message>";

        Apply(Prompt(Asked, "p-1"));
        _clock.AdvanceMinutes(4);
        Apply(Finished("p-1"));

        _clock.AdvanceMinutes(3);
        var second = _clock.Now;
        Apply(Prompt(Message, "p-2"));

        Assert.Equal(Message, Current.Latest.Prompt);
        NewAnchor(second);
    }

    /// <summary>
    /// <strong>By its prefix only.</strong> The tag anywhere but at the very start is ordinary
    /// text, and the prompt is a new ask like any other.
    /// </summary>
    [Theory]
    [InlineData("please explain what a <task-notification> is")]
    [InlineData(" <task-notification>\n<task-id>b7</task-id>")]
    [InlineData("<TASK-NOTIFICATION>\n<task-id>b7</task-id>")]
    [InlineData("<task-notification")]
    public void Only_the_exact_prefix_continues_the_ask(string prompt)
    {
        Apply(Prompt(Asked, "p-1"));
        _clock.AdvanceMinutes(3);
        var second = _clock.Now;
        Apply(Prompt(prompt, "p-2"));

        Assert.Equal(prompt, Current.Latest.Prompt);
        NewAnchor(second);
    }

    // ---- Finished states keep today's clock (acceptance 5) -------------------------------------

    /// <summary>
    /// <strong>Unread, Acked and Ended still read time in state</strong> — "2 min ago" is how long
    /// the result has gone unseen, not how long the work took.
    /// </summary>
    [Fact]
    public void Finished_states_keep_counting_time_in_state()
    {
        Apply(Prompt(Asked, "p-1"));

        _clock.AdvanceMinutes(20);
        Apply(Finished("p-1"));
        Assert.Equal(SessionState.Unread, Current.State);
        TimeInState();

        _clock.AdvanceMinutes(5);
        Apply(new Ack { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual });
        Assert.Equal(SessionState.Acked, Current.State);
        TimeInState();

        _clock.AdvanceMinutes(5);
        Apply(new SessionEnd { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" });
        Assert.Equal(SessionState.Ended, Current.State);
        TimeInState();
    }

    // ---- "You asked · 23 min ago" ticks (acceptance 1) -----------------------------------------

    /// <summary>
    /// The time ago follows the refresh: the same <see cref="SessionViewModel.RefreshAge"/> the
    /// row's age already ticks on, with a property change for the binding.
    /// </summary>
    [Fact]
    public void The_time_ago_ticks_on_the_refresh()
    {
        var asked = _clock.Now;
        Apply(Prompt(Asked, "p-1"));

        var row = new SessionViewModel(Current);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.RefreshAge(asked + TimeSpan.FromMinutes(2));
        var first = row.AskedAgoText;

        row.RefreshAge(asked + TimeSpan.FromMinutes(23));

        Assert.Equal($"{RowVisuals.Duration(TimeSpan.FromMinutes(2))} ago", first);
        Assert.Equal($"{RowVisuals.Duration(TimeSpan.FromMinutes(23))} ago", row.AskedAgoText);
        Assert.Contains(nameof(SessionViewModel.AskedAgoText), raised);
    }

    // ---- Harness -------------------------------------------------------------------------------

    private ApplyOutcome Apply(InboundEvent inboundEvent) => _registry.Apply(inboundEvent);

    private UserPromptSubmit Prompt(string text, string? promptId) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, Prompt = text,
    };

    private Stop Finished(string promptId, string answer = "done") => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, LastAssistantMessage = answer,
    };

    private PostToolBatch Batch() => new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd };

    /// <summary>The row over the current session, refreshed to the clock.</summary>
    private SessionViewModel Row()
    {
        var row = new SessionViewModel(Current);
        row.RefreshAge(_clock.Now);
        return row;
    }

    /// <summary>
    /// Both rows count from <paramref name="asked"/>, now and a minute from now, and "You asked"
    /// still names the same clock time.
    /// </summary>
    private void OneClock(DateTimeOffset asked, string askedAt)
    {
        foreach (var at in new[] { _clock.Now, _clock.Now + TimeSpan.FromMinutes(1) })
        {
            var row = new SessionViewModel(Current);
            row.RefreshAge(at);

            Assert.Equal(at - asked, row.Age);
            Assert.Equal($"{RowVisuals.Duration(at - asked)} ago", row.AskedAgoText);
            Assert.Equal(askedAt, row.AskedAtText);
        }
    }

    /// <summary>A new ask anchors both rows at <paramref name="startedAt"/>.</summary>
    private void NewAnchor(DateTimeOffset startedAt)
    {
        Assert.Equal(startedAt, Current.Latest.StartedAt);

        _clock.AdvanceMinutes(1);
        var row = Row();

        Assert.Equal(_clock.Now - startedAt, row.Age);
        Assert.Equal($"{RowVisuals.Duration(_clock.Now - startedAt)} ago", row.AskedAgoText);
    }

    /// <summary>The collapsed row counts from entering the state; "You asked" still from the ask.</summary>
    private void TimeInState()
    {
        var entered = Current.EnteredAt;
        var asked = Current.Latest.StartedAt;

        Assert.NotEqual(entered, asked);

        var at = _clock.Now + TimeSpan.FromMinutes(2);
        var row = new SessionViewModel(Current);
        row.RefreshAge(at);

        Assert.Equal(at - entered, row.Age);
        Assert.Equal(RowVisuals.Age(Current.State, at - entered), row.AgeText);
        Assert.Equal($"{RowVisuals.Duration(at - asked)} ago", row.AskedAgoText);
    }
}
