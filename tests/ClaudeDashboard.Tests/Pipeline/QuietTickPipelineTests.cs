using System.Globalization;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// A quiet tick through the whole pipeline: raw bodies through the real mapper, consumer, Registry,
/// sound engine, roster watch and decisions recorder (T1.44, issue #56).
/// </summary>
/// <remarks>
/// The operator's case: the watchdog cron runs in the director, a roster member.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class QuietTickPipelineTests : IAsyncLifetime
{
    private const string Cron = "zqx-cron-marker-5v9: check the coder, resurface anything overdue.";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Members = ["director", "coder"];

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RosterStore _rosters = new(new RecordingEventSink(), RosterBook.From([("orchestration", Members)]));
    private readonly RecordingSoundPlayer _player = new();
    private readonly CapturingSink _log = new();
    private readonly List<ArchiveRecord> _records = [];

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();

        var recorder = new DecisionRecorder(_registry, _rosters, _archive, _logger);
        // Unread nudges off: the clock jumps twenty minutes before the tick, and the group's own
        // nudge ladder firing across that jump is correct and not what this test is about.
        // QuietTickTests pins that a quiet tick leaves the ladder where it was.
        var engine = new SoundPolicyEngine(
            _player, _clock, _guard, new SoundPolicyOptions { UnreadNudgeAfter = null }, recorder);

        _registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline, _registry, engine, _clock, _guard, _logger, new RecordingUiTick(), _archive, _rosters,
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
    /// <strong>The director's quiet watchdog tick: no sound at all, the group's finish is not
    /// announced again, and the decisions record says why — with no cron text and no reply in it.</strong>
    /// </summary>
    [Fact]
    public async Task A_quiet_tick_in_the_director_is_silent_and_recorded()
    {
        Publish(Prompt("the real work", "p-1"));
        _clock.AdvanceMinutes(1);
        Publish(Stop("p-1", "the real answer"));

        // The roster settles once and announces once.
        _clock.AdvanceMinutes(1);
        await Until(() => _consumer.SettledCount >= 1);
        var announced = _player.Played.Count;
        Assert.Equal(1, _player.Played.Count(played => played.Sound == SoundId.Finished));

        _clock.AdvanceMinutes(20);
        Publish(Prompt(Cron, "p-2"));
        var tick = await RecordWhere(r => r.Event is UserPromptSubmit { PromptId: "p-2" });

        _clock.AdvanceMinutes(1);
        Publish(Stop("p-2", "\n" + QuietTicks.Sentinel + "\n"));
        var quiet = await RecordWhere(r => r.Event is Stop { PromptId: "p-2" });

        // The group settles again at the same quiet instant: the settle already announced.
        _clock.AdvanceMinutes(1);
        await Until(() => _consumer.SettledCount >= 2);
        await RecordWhere(r => r.Decisions.Any(d => d.Kind == DecisionKind.NoticeSuppressed
            && d.Reason == nameof(SuppressionReason.AlreadyAnnounced) && d.SessionId is null));

        // No sound played by any of it.
        Assert.Equal(announced, _player.Played.Count);

        var session = _registry.Sessions[new SessionId("s-1")];
        Assert.Equal(SessionState.Unread, session.State);
        Assert.Equal("the real answer", session.Latest.Answer);

        // What the decisions record says.
        Assert.Equal(nameof(PromptMeaning.ScheduledPrompt), Moved(tick).Reason);
        Assert.Equal(nameof(TickOutcome.QuietTick), Moved(quiet).Reason);
        Assert.Equal(nameof(SessionState.Unread), Moved(quiet).ToState);
        Assert.Contains(quiet.Decisions, d => d.Kind == DecisionKind.NoticeSuppressed && d.Reason == nameof(SuppressionReason.AlreadyAnnounced));

        static Decision Moved(ArchiveRecord record) => Assert.Single(record.Decisions, d => d.Kind == DecisionKind.StateMoved);
    }

    // ---- Harness -------------------------------------------------------------------------------

    private void Publish(InboundEvent inboundEvent) => Assert.True(_pipeline.Sink.TryPublish(inboundEvent));

    private UserPromptSubmit Prompt(string text, string promptId) =>
        Map<UserPromptSubmit>($$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-1","cwd":"C:\\w","session_title":"director","prompt_id":"{{promptId}}","prompt":{{JsonSerializer.Serialize(text)}}}""");

    private Stop Stop(string promptId, string reply) =>
        Map<Stop>($$"""
            {"hook_event_name":"Stop","session_id":"s-1","cwd":"C:\\w","prompt_id":"{{promptId}}","last_assistant_message":{{JsonSerializer.Serialize(reply)}},"background_tasks":[],"session_crons":[{"id":"c1","schedule":"*/30 * * * *","prompt":{{JsonSerializer.Serialize(Cron)}},"recurring":true}]}
            """);

    private T Map<T>(string body)
        where T : InboundEvent
    {
        var payload = JsonSerializer.Deserialize<HookPayload>(body, Options)!;
        return Assert.IsType<T>(new HookEventMapper(_clock).Map(payload, new PayloadJson(body)).Event);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.True(condition(), "The condition never held.");
    }

    private async Task<ArchiveRecord> RecordWhere(Func<ArchiveRecord, bool> match)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            while (_archive.Reader.TryRead(out var record))
            {
                _records.Add(record);
            }

            if (_records.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException("No matching record arrived.");
    }
}
