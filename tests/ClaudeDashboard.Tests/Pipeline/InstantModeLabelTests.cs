using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Domain;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The mute and pause labels change when the mode lands, not at the next tick (T1.47, the
/// operator's ruling of 2026-09-30).
/// </summary>
/// <remarks>
/// <para>
/// The reviewer measured it live: the header's Mute all relabelled 7 to 11 s after the click,
/// always on the consumer's fifteen-second tick. The consumer applied the command within
/// milliseconds, so the sound was already muted; but the tray recomputed its labels only on the UI
/// tick or a session change, and a mode is neither.
/// </para>
/// <para>
/// Here the consumer's tick is an hour away and <see cref="EventConsumer.TickCount"/> is asserted to
/// stay at zero, so any relabel seen must come from the command itself. The labels are read from
/// the engine's real state through <see cref="ISoundModeReader"/>: the tray is given the engine,
/// not a fake, so nothing optimistic could pass. The header's Mute all binds to this same tray
/// property (<c>The_header_mute_all_is_the_trays_switch_and_reads_its_state</c>).
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class InstantModeLabelTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly QueueingDispatcher _dispatcher = new();

    private SoundPolicyEngine _sound = null!;
    private EventConsumer _consumer = null!;
    private SessionProjection _projection = null!;
    private TrayViewModel _tray = null!;

    public Task InitializeAsync()
    {
        _sound = new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions());
        _registry.SessionChanged += (_, e) => _sound.OnSessionChanged(e.Session, e.Session.WorkspaceGroup);
        _projection = new SessionProjection(_registry, _dispatcher);
        _tray = new TrayViewModel(
            _projection, _sound, _pipeline.Sink, _clock, IngressStatus.Healthy(DashboardSettings.IngressPortBase), Logger.None);

        var uiTick = new UiTick(_dispatcher);
        uiTick.Attach(_tray);

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            _sound,
            _clock,
            _guard,
            Logger.None,
            uiTick,
            _archive,
            new RosterStore(new RecordingEventSink()),
            recorder: TestDecisions.For(_registry, _archive),
            tickInterval: TimeSpan.FromHours(1));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _consumer.StopAsync(CancellationToken.None);
        _consumer.Dispose();
        _tray.Dispose();
        _projection.Dispose();
    }

    [Fact]
    public async Task Mute_and_unmute_relabel_as_soon_as_the_command_lands()
    {
        Assert.Equal("Mute all", _tray.MuteAllLabel);

        _tray.MuteAllCommand.Execute(null);
        Assert.True(await Relabelled(() => _tray.MuteAllLabel == "Unmute all"), "Mute all did not relabel without a tick");

        _tray.MuteAllCommand.Execute(null);
        Assert.True(await Relabelled(() => _tray.MuteAllLabel == "Mute all"), "Unmute all did not relabel without a tick");

        Assert.Equal(0, _consumer.TickCount);
    }

    [Fact]
    public async Task A_thirty_minute_mute_relabels_as_soon_as_the_command_lands()
    {
        _tray.MuteAllForThirtyMinutesCommand.Execute(null);

        Assert.True(await Relabelled(() => _tray.MuteAllLabel == "Unmute all"), "the timed mute did not relabel without a tick");
        Assert.Equal(0, _consumer.TickCount);
    }

    [Fact]
    public async Task Pause_and_resume_relabel_as_soon_as_the_command_lands()
    {
        Assert.Equal("Pause monitoring", _tray.PauseLabel);

        _tray.TogglePauseCommand.Execute(null);
        Assert.True(await Relabelled(() => _tray.PauseLabel == "Resume monitoring"), "Pause did not relabel without a tick");

        _tray.TogglePauseCommand.Execute(null);
        Assert.True(await Relabelled(() => _tray.PauseLabel == "Pause monitoring"), "Resume did not relabel without a tick");

        Assert.Equal(0, _consumer.TickCount);
    }

    /// <summary>
    /// The echo is only a refresh: it fires no nudge and sweeps nothing. A session blocked long
    /// enough to be owed a nudge, and a working session silent long enough to be swept, are both
    /// left for the tick.
    /// </summary>
    [Fact]
    public async Task The_echo_fires_no_nudge_and_sweeps_nothing()
    {
        Assert.True(_pipeline.Sink.TryPublish(Prompt("blocked")));
        Assert.True(_pipeline.Sink.TryPublish(new ClaudeDashboard.Core.Events.Notification
        {
            SessionId = new SessionId("blocked"), Timestamp = _clock.Now, Cwd = @"C:\w", NotificationType = "permission_prompt",
        }));
        Assert.True(_pipeline.Sink.TryPublish(Prompt("silent")));
        Assert.True(await Until(() => _consumer.AppliedCount == 3));

        var due = _sound.NextNudgeAt(new SessionId("blocked"));
        Assert.NotNull(due);

        _clock.Now = due.Value + TimeSpan.FromHours(1);

        _tray.MuteAllCommand.Execute(null);
        Assert.True(await Relabelled(() => _tray.MuteAllLabel == "Unmute all"));

        Assert.Equal(due, _sound.NextNudgeAt(new SessionId("blocked")));
        Assert.Equal(SessionState.Working, _registry.Sessions[new SessionId("silent")].State);
        Assert.Equal(0, _consumer.SilencedCount);
        Assert.Equal(0, _consumer.TickCount);
    }

    private ClaudeDashboard.Core.Events.UserPromptSubmit Prompt(string id) => new()
    {
        SessionId = new SessionId(id), Timestamp = _clock.Now, Cwd = @"C:\w", PromptId = "p-1", Prompt = "go",
    };

    /// <summary>Pumps the UI queue, as the dispatcher would, until the label reads as expected.</summary>
    private Task<bool> Relabelled(Func<bool> condition) => Until(() =>
    {
        _dispatcher.Pump();
        return condition();
    });

    private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }
}
