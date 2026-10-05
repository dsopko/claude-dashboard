using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The Activity window's lines come from the consumer's decisions (T1.70, issue #97): the real consumer, the
/// real recorder and the real sound engine, and no store at all. Nothing drains the archive, so whatever a
/// store would do, the window shows what the dashboard did.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class ActivityPipelineTests : IAsyncLifetime
{
    private const string Title = "zqx-activity-name-marker-8e3";
    private const string Cwd = @"C:\zqx-activity-path-marker\payments-api";
    private static readonly SessionId Id = new("s-1");

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly QueueingDispatcher _dispatcher = new();
    private readonly RecordingLogSink _log = new();

    private Logger _logger = null!;
    private ActivityLog _activity = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();
        _activity = new ActivityLog(_dispatcher, _clock);

        var recorder = new DecisionRecorder(_registry, _rosters, _archive, _logger) { Decided = _activity.Decided };
        var engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions(), recorder);

        _registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            engine,
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            _rosters,
            recorder: recorder,
            tickInterval: TimeSpan.FromMilliseconds(20));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A prompt and a permission prompt: the lines arrive from the consumer's decisions, with the session's name
    /// and project, though no store ever reads the archive.
    /// </summary>
    [Fact]
    public async Task The_lines_come_from_the_consumer_with_no_store()
    {
        Assert.True(_pipeline.Sink.TryPublish(new UserPromptSubmit
        {
            SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1", Prompt = "run the tests", SessionTitle = Title,
        }));

        _clock.AdvanceMinutes(1);
        Assert.True(_pipeline.Sink.TryPublish(new Notification
        {
            SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt",
        }));

        Assert.True(await Until(() =>
        {
            _dispatcher.Pump();
            return _activity.Lines.Any(line => line.Line.Played);
        }));

        // The sound on top of its record, the change that caused it directly under it (the T1.70 review).
        Assert.Equal(["permission", "needs permission", "new session"], _activity.Lines.Select(line => line.What));
        Assert.All(_activity.Lines, line => Assert.Equal((Title, "payments-api", Cwd), (line.Name, line.Project, line.ProjectPath)));
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;

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
