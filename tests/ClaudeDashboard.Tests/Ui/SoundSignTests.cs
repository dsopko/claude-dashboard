using System.IO;
using System.Reflection;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The speaker sign on the row that made a sound (T1.67, issue #99), from a hook event through the
/// Registry, the sound engine and the feed to the row, under a fake clock.
/// </summary>
/// <remarks>
/// The engine is subscribed to the Registry BEFORE the projection, as <c>AppHost</c> does it, so a
/// sound for a new session reaches the window before the session's row exists. One dispatcher
/// carries both, as in the product.
/// </remarks>
public sealed class SoundSignTests : IDisposable
{
    private const string Workspace = @"C:\dev\PennCustQuote";
    private const string Prompt = "deploy the build to the staging slot";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly QueueingDispatcher _dispatcher = new();
    private readonly FakeClock _clock = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly RosterStore _rosters;
    private readonly SoundPolicyEngine _engine;
    private readonly SessionProjection _projection;
    private readonly SoundSigns _signs;
    private readonly MainViewModel _viewModel;
    private int _prompts;

    public SoundSignTests()
        : this(new SoundPolicyOptions { NudgeLadder = [TimeSpan.FromSeconds(50)], UnreadNudgeAfter = TimeSpan.FromMinutes(2) })
    {
    }

    private SoundSignTests(SoundPolicyOptions options)
    {
        _rosters = new RosterStore(new RecordingEventSink(), RosterBook.From([("orchestration", ["Coder", "Reviewer"])]));
        _engine = new SoundPolicyEngine(_player, _clock, new SingleWriterGuard(), options);
        _registry.SessionChanged += (_, e) => _engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));
        _projection = new SessionProjection(_registry, _dispatcher);
        _signs = new SoundSigns(_engine, _dispatcher);
        _viewModel = new MainViewModel(
            _projection,
            new MotionPolicy(() => false, observeChanges: false),
            new StubAckPublisher(),
            new FakeClipboard(),
            _rosters,
            new RecordingRosterPersistence(), new UsageBoard());
        _signs.Attach(_viewModel);
    }

    public void Dispose()
    {
        _signs.Dispose();
        _viewModel.Dispose();
        _projection.Dispose();
    }

    /// <summary>
    /// <strong>A notice that the player queued puts the sign on its session's row</strong>, and nowhere
    /// else. It is there at 59 s and gone at the first refresh at or after 60 s.
    /// </summary>
    [Fact]
    public void A_played_notice_marks_its_row_for_one_minute()
    {
        Working("blocked", At);
        Working("busy", At);
        Blocked("blocked", At.AddMinutes(1));

        Assert.Equal(["blocked"], Signed());

        Refresh(At.AddMinutes(1).AddSeconds(59));
        Assert.Equal(["blocked"], Signed());

        Refresh(At.AddMinutes(2));
        Assert.Empty(Signed());
    }

    /// <summary>
    /// The sign goes on a refresh, not on a timer: at 59 s it is there, and the next refresh, a tick
    /// later, takes it away. Between the two nothing changes it.
    /// </summary>
    [Fact]
    public void The_sign_goes_at_the_first_refresh_after_its_minute()
    {
        Working("blocked", At);
        Blocked("blocked", At.AddMinutes(1));

        Refresh(At.AddMinutes(1).AddSeconds(59));
        Assert.Equal(["blocked"], Signed());

        // Fifteen seconds later, the ordinary tick: the sign has been up for 74 s, and goes now.
        Refresh(At.AddMinutes(1).AddSeconds(74));
        Assert.Empty(Signed());
    }

    /// <summary>The hover says what played and how long ago, in the row's own words.</summary>
    [Fact]
    public void The_hover_says_what_played_and_when()
    {
        Working("blocked", At, title: "Payments API");
        Blocked("blocked", At.AddMinutes(1));

        Refresh(At.AddMinutes(1).AddSeconds(20));

        var row = Row("blocked");
        Assert.Equal("played: permission, 20s ago", row.SoundSignText);

        // Data from the operator never reaches the sign.
        Assert.DoesNotContain("Payments", row.SoundSignText, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy", row.SoundSignText, StringComparison.Ordinal);
        Assert.DoesNotContain("PennCustQuote", row.SoundSignText, StringComparison.Ordinal);

        Refresh(At.AddMinutes(2));
        Assert.Equal(string.Empty, row.SoundSignText);
    }

    /// <summary>
    /// <strong>A muted, a paused and a dropped sound put no sign on any row.</strong> The row is there,
    /// blocked, and the engine decided the sound; only a sound the player queued marks a row.
    /// </summary>
    [Theory]
    [InlineData("muted")]
    [InlineData("all-muted")]
    [InlineData("paused")]
    [InlineData("no-output")]
    public void A_sound_that_did_not_play_marks_no_row(string how)
    {
        Working("blocked", At);

        switch (how)
        {
            case "muted":
                _engine.SetSessionMuted(new SessionId("blocked"), muted: true);
                break;
            case "all-muted":
                _engine.SetAllMuted(muted: true);
                break;
            case "paused":
                _engine.SetMonitoringPaused(paused: true);
                break;
            default:
                _player.Outcome = SoundOutcome.NoOutput;
                break;
        }

        Blocked("blocked", At.AddMinutes(1));
        Refresh(At.AddMinutes(1).AddSeconds(1));

        Assert.Equal(SessionState.NeedsPermission, Row("blocked").State);
        Assert.Empty(Signed());
        Assert.Equal(0, _signs.PostedCount);
    }

    /// <summary>
    /// An already-announced entry, put back by a quiet tick (T1.44), is suppressed and marks no row:
    /// the sign of the first finish has gone, and the second does not bring it back.
    /// </summary>
    [Fact]
    public void An_entry_already_announced_marks_no_row()
    {
        var promptId = Working("done", At);
        Finished("done", At.AddMinutes(1), promptId);

        Assert.Equal(["done"], Signed());
        Refresh(At.AddMinutes(3));
        Assert.Empty(Signed());

        // The quiet tick: the row goes to Working and comes back exactly as it was.
        var finished = _registry.Sessions[new SessionId("done")];
        _engine.OnSessionChanged(finished with { State = SessionState.Working, EnteredAt = At.AddMinutes(3) }, finished.WorkspaceGroup);
        _engine.OnSessionChanged(finished, finished.WorkspaceGroup);
        _dispatcher.Pump();

        Assert.Empty(Signed());
    }

    /// <summary>
    /// <strong>A roster group settles: the sign is on the row of the member whose finish settled it,
    /// and the heading has none.</strong> The group's reminder marks the same member, and its minute
    /// starts again.
    /// </summary>
    [Fact]
    public void A_group_sound_marks_the_member_that_settled_it_and_never_the_heading()
    {
        var coder = Working("coder", At, title: "Coder");
        var reviewer = Working("reviewer", At, title: "Reviewer");
        Finished("coder", At.AddMinutes(1), coder);
        Finished("reviewer", At.AddMinutes(2), reviewer);

        // A member's finish belongs to the group: nothing played, so nothing is marked yet.
        Assert.Empty(Signed());

        var settledAt = At.AddMinutes(2).AddSeconds(2);
        Settle(settledAt);

        Assert.Equal(["reviewer"], Signed());

        var heading = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());
        Assert.Equal(GroupKeys.ForRoster("orchestration"), heading.Group.Key);
        Assert.DoesNotContain(typeof(GroupViewModel).GetProperties(), property => property.Name.Contains("Sound", StringComparison.Ordinal));

        Refresh(settledAt.AddMinutes(1));
        Assert.Empty(Signed());

        // The group's one reminder, two minutes after the settle: the same member again.
        _clock.Now = settledAt.AddMinutes(2);
        _engine.Evaluate(_clock.Now);
        _dispatcher.Pump();

        Assert.Equal(["reviewer"], Signed());
    }

    /// <summary>
    /// <strong>A reminder that plays again at 50 s keeps the sign, and its minute starts again.</strong>
    /// At 109 s from the first sound the sign is still there, because it is 59 s from the second.
    /// </summary>
    [Fact]
    public void A_reminder_at_fifty_seconds_starts_the_minute_again()
    {
        var blockedAt = At.AddMinutes(1);
        Working("blocked", At);
        Blocked("blocked", blockedAt);

        Refresh(blockedAt.AddSeconds(45));

        _clock.Now = blockedAt.AddSeconds(50);
        _engine.Evaluate(_clock.Now);
        _dispatcher.Pump();

        Assert.Equal(2, _player.PlayedOf(SoundId.Permission).Count);

        Refresh(blockedAt.AddSeconds(109));
        Assert.Equal(["blocked"], Signed());

        Refresh(blockedAt.AddSeconds(110));
        Assert.Empty(Signed());
    }

    /// <summary>
    /// A session whose first event makes a sound: the sound reaches the window before the session's
    /// row exists (the engine hears the Registry first), and the row takes the sign when it is built.
    /// </summary>
    [Fact]
    public void A_sound_that_arrives_before_its_row_is_shown_when_the_row_is_built()
    {
        _clock.Now = At;
        _registry.Apply(new Notification
        {
            SessionId = new SessionId("new"),
            Timestamp = At,
            Cwd = Workspace,
            NotificationType = "permission_prompt",
        });

        _dispatcher.Pump();

        Assert.Equal(["new"], Signed());
    }

    /// <summary>One post for each sound that played, and none for an event that played nothing.</summary>
    [Fact]
    public void One_post_for_each_sound_and_none_for_an_event_without_one()
    {
        Working("busy", At);

        for (var i = 1; i <= 20; i++)
        {
            Apply(new PostToolBatch { SessionId = new SessionId("busy"), Timestamp = At.AddSeconds(i), Cwd = Workspace });
        }

        Assert.Equal(0, _signs.PostedCount);

        Blocked("busy", At.AddMinutes(1));

        Assert.Equal(1, _signs.PostedCount);
    }

    /// <summary>
    /// A sign writes no log line: none of the types on its path holds a logger. A sign is a display,
    /// and the decisions record already has the sound.
    /// </summary>
    [Theory]
    [InlineData(typeof(SoundSigns))]
    [InlineData(typeof(MainViewModel))]
    [InlineData(typeof(SessionViewModel))]
    [InlineData(typeof(MetaLine))]
    public void Nothing_on_the_signs_path_can_log(Type type)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(fields, field => field.FieldType.FullName is "Serilog.ILogger" or "Microsoft.Extensions.Logging.ILogger");
    }

    /// <summary>
    /// AppHost resolves the feed after the build, so it listens to the engine from the first event.
    /// Read from the event's own invocation list, so no real sound plays on the machine running the test.
    /// </summary>
    [Fact]
    public void AppHost_subscribes_the_feed_to_the_engine()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var host = AppHost.Build(new DashboardPaths(root));
            var engine = host.Services.GetRequiredService<SoundPolicyEngine>();
            var feed = host.Services.GetRequiredService<SoundSigns>();

            var handlers = (Delegate?)typeof(SoundPolicyEngine)
                .GetField(nameof(SoundPolicyEngine.SoundMarked), BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine);

            Assert.NotNull(handlers);
            Assert.Contains(handlers.GetInvocationList(), handler => ReferenceEquals(handler.Target, feed));

            (host.Services.GetService(typeof(Serilog.ILogger)) as IDisposable)?.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Disposable temp folder.
            }
        }
    }

    /// <summary>
    /// <strong>The window's attach</strong> (the T1.67 review, nit 1): <c>Program</c> hands the window's
    /// view model to <see cref="AppHost.AttachWindow"/>, which attaches it to the tick and to the sign.
    /// Without it no age moves and no sign shows, and nothing else would fail.
    /// </summary>
    [Fact]
    public void AppHost_attaches_the_window_to_the_tick_and_the_sign()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var host = AppHost.Build(new DashboardPaths(root));
            var window = host.Services.GetRequiredService<MainViewModel>();

            AppHost.AttachWindow(host.Services, window);

            Assert.Contains(window, host.Services.GetRequiredService<UiTick>().Targets);
            Assert.Contains(window, host.Services.GetRequiredService<SoundSigns>().Targets);

            (host.Services.GetService(typeof(Serilog.ILogger)) as IDisposable)?.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Disposable temp folder.
            }
        }
    }

    private void Apply(InboundEvent inboundEvent)
    {
        _clock.Now = inboundEvent.Timestamp;
        _registry.Apply(inboundEvent);
        _dispatcher.Pump();
    }

    private string Working(string id, DateTimeOffset at, string? title = null)
    {
        var promptId = $"p-{++_prompts}";

        Apply(new UserPromptSubmit
        {
            SessionId = new SessionId(id),
            Timestamp = at,
            Cwd = Workspace,
            PromptId = promptId,
            Prompt = Prompt,
            SessionTitle = title,
        });

        return promptId;
    }

    private void Blocked(string id, DateTimeOffset at) => Apply(new Notification
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = Workspace,
        NotificationType = "permission_prompt",
    });

    private void Finished(string id, DateTimeOffset at, string promptId) => Apply(new Stop
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = Workspace,
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
    });

    /// <summary>
    /// The settle, as the consumer's roster pass reports it: the quiet instant and the member. The pass
    /// itself, with a roster edit, is in <c>SoundSignPipelineTests</c>.
    /// </summary>
    private void Settle(DateTimeOffset now)
    {
        _clock.Now = now;

        var group = GroupResolver.Resolve(_registry.Sessions.Values, _rosters.Book)
            .Single(candidate => candidate.Key == GroupKeys.ForRoster("orchestration"));

        _engine.OnRosterGroupSettled(group.Key, now, RosterSettle.QuietSince(group), RosterSettle.SettledBy(group));
        _dispatcher.Pump();
    }

    private void Refresh(DateTimeOffset now) => _viewModel.Tick(now);

    private SessionViewModel Row(string id) =>
        _viewModel.Rows.OfType<SessionViewModel>().Single(row => row.Id.Value == id);

    private List<string> Signed() =>
        [.. _viewModel.Rows.OfType<SessionViewModel>().Where(row => row.HasSoundSign).Select(row => row.Id.Value).Order(StringComparer.Ordinal)];
}
