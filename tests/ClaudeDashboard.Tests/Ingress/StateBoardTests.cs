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

    private readonly FakeClock _clock = new(T0);
    private readonly RegistryHarness _harness = new();
    private readonly SoundPolicyEngine _sound;
    private readonly StateBoard _board;

    public StateBoardTests()
    {
        _sound = new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, new SingleWriterGuard(), new SoundPolicyOptions());
        _harness.Registry.SessionChanged += (_, e) => _sound.OnSessionChanged(e.Session, e.Session.WorkspaceGroup);
        _board = new StateBoard(_harness.Registry, _sound, _clock, Logger.None);
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

    private SessionStateEntry Entry(string id) => Assert.Single(_board.Current.Sessions, entry => entry.Id == id);
}
