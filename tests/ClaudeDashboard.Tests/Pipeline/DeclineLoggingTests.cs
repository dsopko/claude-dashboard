using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Storage;
using System.Collections.Concurrent;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>Captures what was logged, and at which level.</summary>
internal sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IEnumerable<LogEvent> AtLevel(LogEventLevel level) =>
        _events.Where(e => e.Level == level);

    public bool AnyMentioning(string fragment, LogEventLevel level) =>
        AtLevel(level).Any(e =>
            e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// That a Registry decline reaches the operator when — and only when — it should.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the point of the outcome type, and the thing that would silently regress.</strong>
/// While <c>Apply</c> returned a <c>bool</c>, one value carried both the routine decline and the
/// alarming one, so there was no level that worked: logged loudly, the file drowned in stale
/// duplicates; logged quietly — which is what was happening — the alarm never reached the file
/// at all, because the host's minimum level is <c>Information</c>.
/// </para>
/// <para>
/// So the assertions are two-sided by necessity. "The warning appears" alone would pass with
/// every decline promoted to <c>Warning</c>, which is the failure mode that made the original
/// choice a bad one.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class DeclineLoggingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly CapturingSink _sink = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        // Debug and above reaches this sink, so a routine decline being logged at Debug is
        // visible to the test even though the host's file sink would discard it. That is what
        // lets the test distinguish "logged quietly" from "logged loudly".
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(_sink)
            .CreateLogger();

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            new RosterStore(new RecordingEventSink()),
            tickInterval: TimeSpan.FromMinutes(5));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

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

    /// <summary>
    /// The alarm: a <c>Stop</c> for a turn the session is no longer tracking. It must reach the
    /// file, which means <c>Warning</c> or above, and it must carry both ids — the operator's
    /// first question will be "which prompt did it think it was answering?".
    /// </summary>
    [Fact]
    public async Task An_uncorrelated_completion_is_logged_at_warning_with_both_prompt_ids()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));
        _pipeline.Sink.TryPublish(Finished("s-1", At.AddMinutes(1), "p-1"));
        _pipeline.Sink.TryPublish(Prompt("s-1", At.AddMinutes(2), "p-2"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 3));

        // A delayed duplicate of the first turn's completion, arriving after the second started.
        _pipeline.Sink.TryPublish(Finished("s-1", At.AddMinutes(3), "p-1"));

        // Waits for the warning itself, not for the counter that precedes it.
        //
        // This was intermittent, and rarely: EventConsumer increments UncorrelatedCount at :307
        // and writes the warning at :313, so a test that waited on the count could read the sink
        // in between and find it empty — "Assert.Single() Failure: The collection was empty",
        // roughly once in eight full runs on this machine. Waiting on the count was waiting on a
        // proxy for the thing being asserted, and the proxy arrives first.
        //
        // The count is still asserted, below, because the two are separate claims: that exactly
        // one completion was classified uncorrelated, and that it reached the log.
        Assert.True(
            await Until(() => _sink.AtLevel(LogEventLevel.Warning).Any()),
            "The uncorrelated completion produced no warning.");

        var warnings = _sink.AtLevel(LogEventLevel.Warning).ToList();
        Assert.Equal(1, _consumer.UncorrelatedCount);
        var warning = Assert.Single(warnings);
        var rendered = warning.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("s-1", rendered, StringComparison.Ordinal);
        Assert.Contains("p-1", rendered, StringComparison.Ordinal);
        Assert.Contains("p-2", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side, without which the first assertion proves nothing: the routine declines
    /// must stay below the level the file keeps. If they were promoted alongside the alarm, the
    /// file would fill with normal traffic and the alarm would be just as lost.
    /// </summary>
    [Fact]
    public async Task Routine_declines_are_logged_below_the_level_the_file_keeps()
    {
        // A duplicate, a stale event, and an inapplicable one — three of the four routine kinds.
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));
        Assert.True(await Until(() => _consumer.AppliedCount == 1));

        _pipeline.Sink.TryPublish(Prompt("s-1", At.AddMinutes(1), "p-1"));
        _pipeline.Sink.TryPublish(new Ack
        {
            SessionId = new SessionId("s-1"), Timestamp = At.AddMinutes(2), Cwd = @"C:\w",
            Source = AckSource.Manual,
        });
        _pipeline.Sink.TryPublish(new Notification
        {
            SessionId = new SessionId("s-1"), Timestamp = At.AddSeconds(-30), Cwd = @"C:\w",
            NotificationType = "permission_prompt",
        });

        Assert.True(
            await Until(() => _consumer.DeclinedCount >= 3),
            $"Expected three declines, saw {_consumer.DeclinedCount}.");

        Assert.Empty(_sink.AtLevel(LogEventLevel.Warning));
        Assert.Empty(_sink.AtLevel(LogEventLevel.Error));
        Assert.Equal(0, _consumer.UncorrelatedCount);

        // They are logged — just quietly, where a developer can turn them up and an operator's
        // file is not filled with them.
        Assert.True(
            _sink.AtLevel(LogEventLevel.Debug).Count() >= 3,
            "The routine declines were not logged at all; they should be visible at Debug.");
    }

    /// <summary>An applied event is not a decline and says nothing.</summary>
    [Fact]
    public async Task An_applied_event_logs_no_decline()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));

        Assert.True(await Until(() => _consumer.AppliedCount == 1));

        Assert.Empty(_sink.AtLevel(LogEventLevel.Warning));
        Assert.Empty(_sink.AtLevel(LogEventLevel.Debug));
        Assert.Equal(0, _consumer.DeclinedCount);
    }

    /// <summary>
    /// The scenario this whole task exists for: if Claude Code does not echo the prompt's id,
    /// every completion is rejected. The operator must be able to see that from the log rather
    /// than from a dashboard full of sessions stuck in Working.
    /// </summary>
    [Fact]
    public async Task Systematic_rejection_is_visible_rather_than_silent()
    {
        for (var i = 0; i < 5; i++)
        {
            _pipeline.Sink.TryPublish(Prompt($"s-{i}", At, $"p-{i}"));

            // Every Stop carries an id the session is not tracking — what a non-echoing
            // Claude Code would produce.
            _pipeline.Sink.TryPublish(Finished($"s-{i}", At.AddMinutes(1), $"stop-{i}"));
        }

        Assert.True(
            await Until(() => _consumer.UncorrelatedCount == 5),
            $"Expected five rejections, saw {_consumer.UncorrelatedCount}.");

        Assert.Equal(5, _sink.AtLevel(LogEventLevel.Warning).Count());
        Assert.True(
            _sink.AnyMentioning("does not match", LogEventLevel.Warning),
            "The warning must say what is wrong, not merely that something was rejected.");

        // And the sessions really are stuck in Working, which is the symptom the log explains.
        Assert.All(_registry.Sessions.Values, s => Assert.Equal(SessionState.Working, s.State));
    }

    private static UserPromptSubmit Prompt(string sessionId, DateTimeOffset stamp, string promptId) => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = stamp,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "p",
    };

    private static Stop Finished(string sessionId, DateTimeOffset stamp, string promptId) => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = stamp,
        Cwd = @"C:\w",
        PromptId = promptId,
        LastAssistantMessage = "done",
    };
}
