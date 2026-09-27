using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Waiting: a session paused on its own background work is not finished (T1.41, issue #52).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The real Registry and the real sound engine, wired as the product wires them.</strong>
/// The engine hears every change through <c>SessionChanged</c>, exactly as <c>AppHost</c> and
/// <c>ReplaySwitch</c> subscribe it, and plays into a recording player — so "no finished notice"
/// is asserted on what the engine actually played, not inferred from the state.
/// </para>
/// </remarks>
public sealed class WaitingStateTests
{
    private const string Cwd = @"C:\projects\dashboard";
    private static readonly SessionId Id = new("s-1");

    /// <summary>The roster's members, by the session titles the two roster sessions carry.</summary>
    private static readonly string[] RosterMembers = ["director", "coder"];

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry;
    private readonly RecordingSoundPlayer _player = new();

    public WaitingStateTests()
    {
        _registry = new SessionRegistry(_guard);

        var engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions());

        _registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, e.Session.WorkspaceGroup);
    }

    private Session Current => _registry.Sessions[Id];

    private int FinishedNotices => _player.Played.Count(played => played.Sound == SoundId.Finished);

    // ---- Acceptance 1: #52's run, event by event ----------------------------------------------

    /// <summary>
    /// <strong>#52's coder under a director: no "finished" notice at step 4, exactly one at
    /// step 8.</strong>
    /// </summary>
    /// <remarks>
    /// The issue's table, row for row. Step 4 is the Stop that ended the turn with the build
    /// still running — before T1.41, a false "finished" chime. Step 6 is the task-notification
    /// wake-up, which is also not an acknowledgment. Step 8 is the Stop with nothing running,
    /// which is the real finish.
    /// </remarks>
    [Fact]
    public void The_issue_run_chimes_finished_once_and_only_at_the_real_finish()
    {
        // 1. The director's message arrives: a machine prompt that starts a new ask.
        Apply(Prompt("<cross-session-message from=\"director\">Task T9</cross-session-message>", "p-1"));
        Assert.Equal(SessionState.Working, Current.State);

        // 2–3. The coder works, and starts a background build.
        Apply(Batch());
        Apply(Batch());

        // 4. The turn ends with the build still running.
        Apply(Stopped("p-1", Shell("b1", "Run the test suite")));
        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Equal(0, FinishedNotices);

        // 5a. The build runs: idle notices, ignored as today.
        Assert.Equal(ApplyOutcome.Ignored, Apply(Notified("idle_prompt")));

        // 5b. (A subagent's own tool calls arrive under this session, and leave it Waiting.)
        Assert.Equal(ApplyOutcome.Ignored, Apply(Batch()));
        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Equal(0, FinishedNotices);

        // 6. The build finishes and the coder is woken.
        Apply(Prompt("<task-notification>\n<task-id>b1</task-id>", "p-2"));
        Assert.Equal(SessionState.Working, Current.State);

        // 7. The coder reads the result.
        Apply(Batch());

        // 8. The turn ends with nothing running.
        Apply(Stopped("p-2"));
        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(1, FinishedNotices);

        // And nothing at all was played on the way: no nudge, no other notice.
        Assert.Single(_player.Played);
    }

    // ---- Acceptance 2: what a Stop decides ----------------------------------------------------

    /// <summary>A Stop listing a running shell or subagent enters Waiting, with no sound.</summary>
    [Theory]
    [InlineData(BackgroundTaskKind.Shell)]
    [InlineData(BackgroundTaskKind.Subagent)]
    public void A_stop_with_allowed_running_work_enters_waiting(BackgroundTaskKind kind)
    {
        Apply(Prompt("go", "p-1"));
        Apply(Stopped("p-1", new BackgroundTask("t1", kind, "the work")));

        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Equal("all done", Current.Latest.Answer);
        Assert.Empty(_player.Played);
    }

    /// <summary>
    /// A Stop with nothing allowed running — only a monitor, or only an unseen type, which the
    /// mapper does not pass on — means what it meant before: Unread, and the chime.
    /// </summary>
    [Fact]
    public void A_stop_with_no_allowed_work_finishes_as_before()
    {
        Apply(Prompt("go", "p-1"));
        Apply(Stopped("p-1") with { UnrecognisedBackgroundTasks = 1 });

        Assert.Equal(SessionState.Unread, Current.State);
        Assert.Equal(1, FinishedNotices);
    }

    // ---- Acceptance 3: what moves Waiting, and what does not ----------------------------------

    /// <summary>A batch in Waiting stays Waiting, but it is heard: LastHeardAt advances.</summary>
    [Fact]
    public void A_batch_in_waiting_stays_waiting_and_is_heard()
    {
        GivenWaiting();
        var before = Current;

        _clock.AdvanceMinutes(2);
        Assert.Equal(ApplyOutcome.Ignored, Apply(Batch()));

        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Equal(before.EnteredAt, Current.EnteredAt);
        Assert.Equal(_clock.Now, Current.LastHeardAt);
    }

    /// <summary>Any prompt ends Waiting: the operator's, a peer's, or the wake-up.</summary>
    [Theory]
    [InlineData("carry on")]
    [InlineData("<cross-session-message from=\"director\">next</cross-session-message>")]
    [InlineData("<task-notification>\n<task-id>b1</task-id>")]
    public void Any_prompt_in_waiting_moves_it_to_working(string prompt)
    {
        GivenWaiting();

        _clock.AdvanceMinutes(1);
        Apply(Prompt(prompt, "p-2"));

        Assert.Equal(SessionState.Working, Current.State);
    }

    /// <summary>Every Stop decides again: one that still lists a task is Waiting again.</summary>
    [Fact]
    public void A_stop_that_still_lists_a_task_is_waiting_again()
    {
        GivenWaiting();

        _clock.AdvanceMinutes(1);
        Apply(Prompt("<task-notification>\n<task-id>b0</task-id>", "p-2"));
        _clock.AdvanceMinutes(1);
        Apply(Stopped("p-2", Shell("b1", "Run the test suite")));

        Assert.Equal(SessionState.Waiting, Current.State);
        Assert.Empty(_player.Played);
    }

    // ---- Acceptance 4 and 5: what outranks it, and what never touches it ----------------------

    /// <summary>A permission, a question or an error outranks Waiting, and sounds as it does today.</summary>
    [Theory]
    [InlineData("permission_prompt", SessionState.NeedsPermission)]
    [InlineData("agent_needs_input", SessionState.NeedsQuestion)]
    public void A_needs_you_notification_in_waiting_takes_over(string type, SessionState expected)
    {
        GivenWaiting();

        _clock.AdvanceMinutes(1);
        Apply(Notified(type));

        Assert.Equal(expected, Current.State);
        Assert.Single(_player.Played);
    }

    /// <summary>An error outranks Waiting too.</summary>
    [Fact]
    public void An_error_in_waiting_takes_over()
    {
        GivenWaiting();

        _clock.AdvanceMinutes(1);
        Apply(new StopFailure { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, ErrorKind = "rate_limit" });

        Assert.Equal(SessionState.Error, Current.State);
    }

    /// <summary>
    /// <strong>The silence sweep never moves Waiting</strong>, however long it is quiet — the
    /// operator's ruling: it is quiet because it is waiting, not because it was cut off.
    /// </summary>
    [Fact]
    public void The_silence_sweep_never_touches_waiting()
    {
        GivenWaiting();

        _clock.AdvanceMinutes(60 * 24);

        Assert.Empty(_registry.SweepSilent(_clock.Now, SilenceWatch.DefaultThreshold));
        Assert.Equal(SessionState.Waiting, Current.State);
    }

    // ---- Acceptance 6: a roster with a Waiting member ------------------------------------------

    /// <summary>
    /// <strong>A roster with a Waiting member does not settle finished</strong>, however long it
    /// is quiet; it settles once that member really finishes.
    /// </summary>
    [Fact]
    public void A_roster_with_a_waiting_member_does_not_settle()
    {
        // Roster members are session titles, so the two sessions carry theirs from their first prompt.
        var book = RosterBook.From([("orchestration", RosterMembers)]);
        var watch = new RosterGroupWatch();

        Apply(Prompt("go", "p-1", "s-1") with { SessionTitle = "director" });
        Apply(Stopped("p-1", sessionId: "s-1"));
        Apply(Prompt("go", "p-1", "s-2") with { SessionTitle = "coder" });
        Apply(Stopped("p-1", Shell("b1", "Run the build"), sessionId: "s-2"));

        var group = Assert.Single(GroupResolver.Resolve(_registry.Sessions.Values, book));
        Assert.Equal(SessionState.Waiting, group.WorstState);

        var later = _clock.Now + RosterSettle.DefaultWindow + TimeSpan.FromHours(1);
        Assert.NotEqual(SessionState.Unread, RosterSettle.StateOf(group, later));
        Assert.DoesNotContain(watch.Observe(GroupResolver.Resolve(_registry.Sessions.Values, book), later),
            change => change.Event == RosterGroupEvent.Settled);

        // The member really finishes: now the group settles.
        _clock.Now = later;
        Apply(Prompt("<task-notification>\n<task-id>b1</task-id>", "p-2", "s-2"));
        Apply(Stopped("p-2", sessionId: "s-2"));

        var settled = watch.Observe(
            GroupResolver.Resolve(_registry.Sessions.Values, book),
            _clock.Now + RosterSettle.DefaultWindow);

        Assert.Contains(settled, change => change.Event == RosterGroupEvent.Settled);
    }

    // ---- The task list the row shows -----------------------------------------------------------

    /// <summary>
    /// A task keeps the instant it was first listed across the Stops that go on listing it, and
    /// drops out when a Stop no longer does.
    /// </summary>
    [Fact]
    public void A_task_keeps_its_first_seen_instant_and_drops_when_it_leaves_the_list()
    {
        var first = _clock.Now;
        Apply(Prompt("go", "p-1"));
        Apply(Stopped("p-1", Shell("b1", "Run the build"), Shell("b2", "Run the linter")));

        _clock.AdvanceMinutes(5);
        var second = _clock.Now;
        Apply(Prompt("<task-notification>\n<task-id>b2</task-id>", "p-2"));
        Apply(Stopped("p-2", Shell("b1", "Run the build"), new BackgroundTask("a1", BackgroundTaskKind.Subagent, "Review")));

        Assert.Equal(
            [("b1", first), ("a1", second)],
            Current.WaitingOn.Select(task => (task.Id, task.FirstSeenAt)).ToArray());
    }

    /// <summary>
    /// A redelivered Stop in Waiting is a duplicate, like one in Unread: the same turn has one Stop,
    /// and the next one follows the wake-up prompt under a new prompt_id.
    /// </summary>
    [Fact]
    public void A_stop_redelivered_in_waiting_is_a_duplicate()
    {
        Apply(Prompt("go", "p-1"));
        Apply(Stopped("p-1", Shell("b1", "Run the build"), Shell("b2", "Run the linter")));
        var before = Current;

        _clock.AdvanceMinutes(1);
        Assert.Equal(ApplyOutcome.Duplicate, Apply(Stopped("p-1", Shell("b1", "Run the build"), Shell("b2", "Run the linter"))));
        Assert.Equal(ApplyOutcome.Duplicate, Apply(Stopped("p-1", Shell("b1", "Run the build"))));

        Assert.Equal(before.Latest, Current.Latest);
        Assert.Equal(before.WaitingOn, Current.WaitingOn);
        Assert.Equal(before.Transitions.Count, Current.Transitions.Count);
    }

    // ---- Acceptance 7, Core half: the transition log's tier-1 claim ---------------------------

    /// <summary>
    /// A machine prompt on a session with something pending moves it to Working and does not
    /// record an acknowledgment; the operator's own prompt still does.
    /// </summary>
    [Theory]
    [InlineData("<task-notification>\n<task-id>b1</task-id>", false)]
    [InlineData("<cross-session-message from=\"director\">next</cross-session-message>", false)]
    [InlineData("[Cross-session idle notice] the director is idle", false)]
    [InlineData("<agent-message from=\"x\">hi</agent-message>", false)]
    [InlineData("now write the tests", true)]
    [InlineData(" <task-notification> with a leading space is typed text", true)]
    public void Only_the_operators_own_prompt_is_recorded_as_an_acknowledgment(string prompt, bool acknowledges)
    {
        Apply(Prompt("go", "p-1"));
        Apply(Stopped("p-1"));
        Assert.Equal(SessionState.Unread, Current.State);

        _clock.AdvanceMinutes(1);
        Apply(Prompt(prompt, "p-2"));

        Assert.Equal(SessionState.Working, Current.State);

        var cause = Current.Transitions[^1].Cause!;
        Assert.Equal(acknowledges, cause.Contains("auto-ack", StringComparison.Ordinal));
        Assert.Equal(!acknowledges, cause.Contains("machine prompt", StringComparison.Ordinal));
    }

    // ---- Harness -------------------------------------------------------------------------------

    private ApplyOutcome Apply(InboundEvent inboundEvent) => _registry.Apply(inboundEvent);

    private void GivenWaiting()
    {
        Apply(Prompt("go", "p-1"));
        Apply(Stopped("p-1", Shell("b1", "Run the build")));
        Assert.Equal(SessionState.Waiting, Current.State);
    }

    private static BackgroundTask Shell(string id, string description) => new(id, BackgroundTaskKind.Shell, description);

    private UserPromptSubmit Prompt(string text, string promptId, string sessionId = "s-1") => new()
    {
        SessionId = new SessionId(sessionId), Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId, Prompt = text,
    };

    private Stop Stopped(string promptId, params BackgroundTask[] running) => Stopped(promptId, running, "s-1");

    private Stop Stopped(string promptId, BackgroundTask task, string sessionId) => Stopped(promptId, [task], sessionId);

    private Stop Stopped(string promptId, string sessionId) => Stopped(promptId, [], sessionId);

    private Stop Stopped(string promptId, BackgroundTask[] running, string sessionId) => new()
    {
        SessionId = new SessionId(sessionId), Timestamp = _clock.Now, Cwd = Cwd, PromptId = promptId,
        LastAssistantMessage = "all done",
        BackgroundTasks = running,
    };

    private PostToolBatch Batch() => new() { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd };

    private Notification Notified(string type) => new()
    {
        SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = type,
    };
}
