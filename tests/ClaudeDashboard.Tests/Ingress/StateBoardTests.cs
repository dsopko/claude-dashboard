using System.Text.Json;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Ui;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// The report <c>/state</c> serves (T1.46, issue #10): what it says, and that it stays true as the
/// consumer thread moves on.
/// </summary>
/// <remarks>
/// Wired the way <c>AppHost</c> wires it: the sound engine hears the Registry first, then the board
/// is built. Every expected value is asked of the rule that owns it — the band of
/// <see cref="AttentionOrder.BandOf"/>, the colour of <see cref="TrayVisuals.ColourOf"/>, the nudge
/// time of the engine — so a copy of a rule here cannot drift from the rule.
/// </remarks>
public sealed class StateBoardTests : IDisposable
{
    private const string TitleMarker = "TITLE-MARKER-3f1a";
    private const string DescriptionMarker = "DESCRIPTION-MARKER-9b2c";
    private const string PromptMarker = "PROMPT-MARKER-51de";
    private const string AnswerMarker = "ANSWER-MARKER-77a0";

    private static readonly DateTimeOffset T0 = FakeClock.DefaultStart;

    /// <summary>The three sessions <see cref="Orchestrate"/> makes.</summary>
    private static readonly string[] Orchestration = ["s-1", "s-2", "s-3"];

    private readonly FakeClock _clock = new(T0);
    private readonly RegistryHarness _harness = new();
    private readonly SoundPolicyEngine _sound;
    private readonly StateBoard _board;
    private readonly ClaudeDashboard.App.Configuration.RosterStore _rosters = new(new RecordingEventSink());

    public StateBoardTests()
    {
        _sound = new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, new SingleWriterGuard(), new SoundPolicyOptions());
        _harness.Registry.SessionChanged += (_, e) => _sound.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));
        _board = new StateBoard(_harness.Registry, _sound, _clock, Logger.None, _rosters);
    }

    public void Dispose()
    {
        _board.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void Before_any_event_the_report_is_empty_and_every_band_reads_zero()
    {
        var report = _board.Current;

        Assert.Equal(0, report.SessionCount);
        Assert.Empty(report.Sessions);
        Assert.Equal(Enum.GetValues<AttentionBand>().Length, report.Bands.Count);
        Assert.All(report.Bands.Values, count => Assert.Equal(0, count));
    }

    /// <summary>
    /// One session in each band, and the report says what the Registry holds — state, band,
    /// counts and the tray's light.
    /// </summary>
    [Fact]
    public void Each_session_is_reported_with_its_state_band_and_the_roll_up()
    {
        _harness.Working("working", T0);
        _harness.Working("blocked", T0);
        _harness.Blocked("blocked", T0.AddSeconds(5));
        _harness.Finished("unread", T0.AddSeconds(6), _harness.Working("unread", T0));
        _harness.Quiet("quiet", T0);
        _harness.Working("ended", T0);
        _harness.Ended("ended", T0.AddSeconds(7));

        var report = _board.Current;

        Assert.Equal(5, report.SessionCount);
        Assert.Equal(5, report.Sessions.Count);

        foreach (var session in _harness.Registry.Sessions.Values)
        {
            var entry = Assert.Single(report.Sessions, e => e.Id == session.Id.Value);

            Assert.Equal(session.State, entry.State);
            Assert.Equal(AttentionOrder.BandOf(session.State), entry.Band);
            Assert.Equal(session.WorkspaceGroup.Value, entry.Group);
            Assert.Equal(session.Cwd, entry.Cwd);
            Assert.Equal(session.EnteredAt, entry.EnteredAt);
            Assert.Equal(session.LastActivity, entry.LastActivity);
            Assert.Equal(session.LastHeardAt, entry.LastHeardAt);
        }

        Assert.All(Enum.GetValues<AttentionBand>(), band => Assert.Equal(1, report.Bands[band]));

        Assert.Equal(SessionState.NeedsPermission, report.Tray.Worst);
        Assert.Equal(TrayVisuals.ColourOf(SessionState.NeedsPermission), report.Tray.Light);

        // Most urgent first, as the window orders them.
        Assert.Equal("blocked", report.Sessions[0].Id);
    }

    /// <summary>
    /// The nudge time is the engine's, it is in the future before the nudge fires, and it moves
    /// when the nudge fires — the one change to it that no session change announces.
    /// </summary>
    [Fact]
    public void The_next_nudge_is_reported_before_it_fires_and_follows_it_after()
    {
        _harness.Working("blocked", T0);
        _harness.Blocked("blocked", T0);

        var id = new SessionId("blocked");
        var first = Entry("blocked").NextNudgeAt;

        Assert.NotNull(first);
        Assert.Equal(_sound.NextNudgeAt(id), first);
        Assert.True(first > _clock.Now, "the first nudge must be reported before it is due");

        _clock.Now = first.Value.AddSeconds(1);
        _sound.Evaluate(_clock.Now);

        var second = Entry("blocked").NextNudgeAt;

        Assert.Equal(_sound.NextNudgeAt(id), second);
        Assert.True(second > _clock.Now, "after the nudge fires, the report must show the next one, not the one just spent");
    }

    /// <summary>
    /// An unread session gets one nudge. Once it fires, the report shows none scheduled, not the
    /// time just spent.
    /// </summary>
    /// <remarks>
    /// The engine clears an Unread nudge rather than scheduling the next one, and that is the
    /// other branch of <see cref="SoundPolicyEngine.NudgeScheduleAdvanced"/>. Nothing else changes
    /// when it fires, so without the event the report would go on showing a time in the past.
    /// </remarks>
    [Fact]
    public void An_unread_sessions_single_nudge_fires_and_the_report_then_shows_none()
    {
        _harness.Finished("unread", T0, _harness.Working("unread", T0));

        var due = Entry("unread").NextNudgeAt;

        Assert.NotNull(due);
        Assert.Equal(SessionState.Unread, Entry("unread").State);

        _clock.Now = due.Value.AddSeconds(1);
        _sound.Evaluate(_clock.Now);

        Assert.Null(_sound.NextNudgeAt(new SessionId("unread")));
        Assert.Null(Entry("unread").NextNudgeAt);
    }

    [Fact]
    public void A_session_with_nothing_scheduled_reports_no_nudge()
    {
        _harness.Working("working", T0);

        Assert.Null(Entry("working").NextNudgeAt);
    }

    [Fact]
    public void A_waiting_session_reports_its_tasks_with_their_description_and_never_a_command()
    {
        var promptId = _harness.Working("waiting", T0);
        _harness.Apply(new Stop
        {
            SessionId = new SessionId("waiting"),
            Timestamp = T0.AddSeconds(3),
            Cwd = RegistryHarness.Workspace,
            PromptId = promptId,
            LastAssistantMessage = "started the build",
            BackgroundTasks = [new BackgroundTask("task-1", BackgroundTaskKind.Shell, DescriptionMarker)],
        });

        var entry = Entry("waiting");
        var task = Assert.Single(entry.WaitingOn);

        Assert.Equal(SessionState.Waiting, entry.State);
        Assert.Equal("task-1", task.Id);
        Assert.Equal(BackgroundTaskKind.Shell, task.Kind);
        Assert.Equal(DescriptionMarker, task.Description.Reveal());
        Assert.Equal(T0.AddSeconds(3), task.FirstSeenAt);

        var body = JsonSerializer.Serialize(_board.Current, IngressEndpoints.StateOptions);

        Assert.Contains(DescriptionMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("command", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The body carries the title and the description, and never the prompt or the answer.
    /// </summary>
    [Fact]
    public void The_body_carries_the_title_and_never_the_prompt_or_the_answer()
    {
        var promptId = _harness.Working("s-1", T0, prompt: PromptMarker, title: TitleMarker);
        _harness.Finished("s-1", T0.AddSeconds(4), promptId, answer: AnswerMarker, title: TitleMarker);

        var body = JsonSerializer.Serialize(_board.Current, IngressEndpoints.StateOptions);

        Assert.Contains(TitleMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AnswerMarker, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A report already handed to a request never changes afterwards. The request serializes a
    /// finished object; the consumer builds the next one beside it.
    /// </summary>
    [Fact]
    public void A_report_already_read_is_not_changed_by_later_events()
    {
        _harness.Working("s-1", T0);
        var read = _board.Current;

        _harness.Blocked("s-1", T0.AddSeconds(1));
        _harness.Working("s-2", T0.AddSeconds(2));

        Assert.Equal(SessionState.Working, Assert.Single(read.Sessions).State);
        Assert.Equal(2, _board.Current.SessionCount);
    }

    /// <summary>
    /// <strong>A roster counts once in the bands</strong> (T1.83, issue #130): one member working and two finished is
    /// <c>bands.working</c> 1 and <c>bands.unread</c> 0, and the light is Working. The session count and the entries
    /// are still the three sessions.
    /// </summary>
    [Fact]
    public void A_working_roster_counts_once_in_the_bands()
    {
        _rosters.Replace(ClaudeDashboard.Core.RosterBook.From([("orchestration", ["Director", "Coder", "Reviewer"])]));
        Orchestrate();

        var report = _board.Current;

        Assert.Equal(1, report.Bands[AttentionBand.Working]);
        Assert.Equal(0, report.Bands[AttentionBand.Unread]);
        Assert.Equal(SessionState.Working, report.Tray.Worst);
        Assert.Equal(3, report.SessionCount);
        Assert.Equal(3, report.Sessions.Count);
        Assert.Equal(2, report.Sessions.Count(entry => entry.State == SessionState.Unread));
    }

    /// <summary>
    /// The last member stops: inside the settle window the bands still read Working, and the report built again at
    /// the deadline (<see cref="StateBoard.Restate"/>, which the consumer calls after its settle pass) reads
    /// <c>bands.unread</c> 1.
    /// </summary>
    [Fact]
    public void At_the_settle_deadline_the_report_reads_one_unread()
    {
        _rosters.Replace(ClaudeDashboard.Core.RosterBook.From([("orchestration", ["Director", "Coder", "Reviewer"])]));
        var reviewer = Orchestrate();
        _clock.Now = T0.AddMinutes(4);
        _harness.Finished("s-3", T0.AddMinutes(4), reviewer, cwd: @"C:\reviewer", title: "Reviewer");

        Assert.Equal(1, _board.Current.Bands[AttentionBand.Working]);

        _clock.Now = T0.AddMinutes(4) + RosterSettle.DefaultWindow;
        _board.Restate();

        var report = _board.Current;
        Assert.Equal(1, report.Bands[AttentionBand.Unread]);
        Assert.Equal(0, report.Bands[AttentionBand.Working]);
        Assert.Equal(SessionState.Unread, report.Tray.Worst);
        Assert.Equal(3, report.SessionCount);
    }

    /// <summary>
    /// <strong>Every session says whose reminder it is</strong> (T1.84, issue #58): in the JSON, <c>nudgeBy</c> is the
    /// string <c>Session</c> or <c>Group</c> on every entry, and each entry's time and owner are the engine's answer.
    /// The two finished roster members are <c>Group</c> with no time before the settle; the working member, and a
    /// session outside the roster that needs permission, are <c>Session</c>.
    /// </summary>
    [Fact]
    public void Every_session_says_whose_reminder_it_is_as_a_string()
    {
        _rosters.Replace(ClaudeDashboard.Core.RosterBook.From([("orchestration", ["Director", "Coder", "Reviewer"])]));
        Orchestrate();
        _harness.Working("alone", T0.AddMinutes(3));
        _harness.Blocked("alone", T0.AddMinutes(3));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(_board.Current, IngressEndpoints.StateOptions));
        var sessions = document.RootElement.GetProperty("sessions").EnumerateArray().ToList();

        Assert.Equal(4, sessions.Count);
        Assert.All(sessions, session => Assert.Equal(JsonValueKind.String, session.GetProperty("nudgeBy").ValueKind));

        var owners = sessions.ToDictionary(session => session.GetProperty("id").GetString()!, session => session.GetProperty("nudgeBy").GetString());
        Assert.Equal("Group", owners["s-1"]);
        Assert.Equal("Group", owners["s-2"]);
        Assert.Equal("Session", owners["s-3"]);
        Assert.Equal("Session", owners["alone"]);

        Assert.Equal(JsonValueKind.Null, sessions.Single(session => session.GetProperty("id").GetString() == "s-1").GetProperty("nextNudgeAt").ValueKind);

        foreach (var entry in _board.Current.Sessions)
        {
            Assert.Equal(_sound.NextNudgeOf(new SessionId(entry.Id)), new NextNudge(entry.NextNudgeAt, entry.NudgeBy));
        }
    }

    /// <summary>
    /// <strong>A finished roster member reports the group's reminder</strong> (T1.84): no time before the settle, the
    /// group's time in the report built again after the settle pass, and no time once the group's nudge has played.
    /// The last step has no session change and no settle pass: only the engine's event builds the report again.
    /// </summary>
    [Fact]
    public void A_finished_roster_member_reports_the_group_reminder_until_the_group_nudge_plays()
    {
        _rosters.Replace(ClaudeDashboard.Core.RosterBook.From([("orchestration", ["Director", "Coder", "Reviewer"])]));
        var reviewer = Orchestrate();
        _clock.Now = T0.AddMinutes(4);
        _harness.Finished("s-3", T0.AddMinutes(4), reviewer, cwd: @"C:\reviewer", title: "Reviewer");

        Assert.Equal(((DateTimeOffset?)null, NudgeOwner.Group), (Entry("s-1").NextNudgeAt, Entry("s-1").NudgeBy));

        var settledAt = T0.AddMinutes(4) + RosterSettle.DefaultWindow;
        _clock.Now = settledAt;
        var roster = GroupKeys.ForRoster("orchestration");
        _sound.OnRosterGroupSettled(roster, settledAt, T0.AddMinutes(4), new SessionId("s-3"), [new SessionId("s-1"), new SessionId("s-2"), new SessionId("s-3")]);
        _board.Restate();

        var due = settledAt + SoundPolicyOptions.DefaultUnreadNudgeAfter;
        Assert.All(Orchestration, id => Assert.Equal(((DateTimeOffset?)due, NudgeOwner.Group), (Entry(id).NextNudgeAt, Entry(id).NudgeBy)));

        _clock.Now = due;
        _sound.Evaluate(due);

        Assert.All(Orchestration, id => Assert.Equal(((DateTimeOffset?)null, NudgeOwner.Group), (Entry(id).NextNudgeAt, Entry(id).NudgeBy)));
    }

    /// <summary>The issue's orchestration: Director and Coder finished, Reviewer working. Returns Reviewer's prompt id.</summary>
    private string Orchestrate()
    {
        _clock.Now = T0;
        var director = _harness.Working("s-1", T0, cwd: @"C:\director", title: "Director");
        _clock.Now = T0.AddMinutes(1);
        _harness.Finished("s-1", T0.AddMinutes(1), director, cwd: @"C:\director", title: "Director");
        var coder = _harness.Working("s-2", T0.AddMinutes(1), cwd: @"C:\coder", title: "Coder");
        _clock.Now = T0.AddMinutes(2);
        _harness.Finished("s-2", T0.AddMinutes(2), coder, cwd: @"C:\coder", title: "Coder");
        var reviewer = _harness.Working("s-3", T0.AddMinutes(2), cwd: @"C:\reviewer", title: "Reviewer");
        _clock.Now = T0.AddMinutes(3);
        return reviewer;
    }

    private SessionStateEntry Entry(string id) => Assert.Single(_board.Current.Sessions, entry => entry.Id == id);
}
