using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The silence sweep runs on the consumer's own tick, and says exactly once what it did
/// (issue #28).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The log is why the threshold has no setting.</strong> Ten minutes is a guess and is
/// treated as one: every transition records how long the session had actually been quiet, so the
/// operator can see from their own machine whether the number is right. A run full of these at
/// eleven minutes is a better argument than any knob. The same instrument as T1.25's mis-mark
/// warning, and the same reason.
/// </para>
/// <para>
/// <strong>Once, not once per tick.</strong> The loop evaluates every fifteen seconds and the
/// session stays quiet, so a sweep that re-reported would fill the log with one event that
/// happened once — and the line the operator needs would be buried under copies of itself.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class SilenceLoggingTests : IAsyncLifetime
{
    /// <summary>A title shaped like one a small model would write from the operator's prompt.</summary>
    private const string Marker = "zqx-secret-title-marker-9k2";

    private const string Cwd = @"C:\projects\dashboard";
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly SessionId Id = new("s-1");

    private readonly FakeClock _clock = new();
    private readonly CapturingSink _sink = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            new RosterStore(new RecordingEventSink()),

            // Short enough that the loop ticks without the test waiting on a real fifteen seconds,
            // and unrelated to the threshold below, which is what the sweep actually measures.
            tickInterval: TimeSpan.FromMilliseconds(20),
            silenceThreshold: TimeSpan.FromMinutes(10));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    private IEnumerable<string> Rendered =>
        _sink.Events.Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));

    private IEnumerable<string> SilenceLines =>
        Rendered.Where(line => line.Contains("no event for", StringComparison.Ordinal));

    /// <summary>
    /// <strong>The loop sweeps on its own tick, and the line carries the silence and no title.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session carries a title throughout, so the absence of one in the log is the rule being
    /// kept rather than an arrangement that had nothing to leak. T1.24: a title can be a
    /// model-written summary of the operator's prompt, and a line saying which session went quiet
    /// needs none of it.
    /// </para>
    /// <para>
    /// The wording is asserted too. It says "no event", not "interrupted" — the badge carries the
    /// operator's word because they asked for it, and the log carries what was observed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_tick_sweeps_and_logs_the_silence_without_the_title()
    {
        _pipeline.Sink.TryPublish(new UserPromptSubmit
        {
            SessionId = Id,
            Timestamp = _clock.Now,
            Cwd = Cwd,
            PromptId = "p-1",
            Prompt = "run the tests",
            SessionTitle = Marker,
        });

        Assert.True(await Until(() => _consumer.AppliedCount >= 1));
        Assert.Equal(Marker, _registry.Sessions[Id].Title);

        _clock.AdvanceMinutes(11);

        Assert.True(await Until(() => _consumer.SilencedCount >= 1));

        Assert.Equal(SessionState.Interrupted, _registry.Sessions[Id].State);

        var line = Assert.Single(SilenceLines);

        Assert.Contains("s-1", line, StringComparison.Ordinal);
        Assert.Contains("11.0 minutes", line, StringComparison.Ordinal);
        Assert.Contains("10 minutes", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, line, StringComparison.Ordinal);
        Assert.DoesNotContain("run the tests", line, StringComparison.Ordinal);

        // And it is not shouting: nothing has happened that the operator must act on.
        Assert.True(_sink.AnyMentioning("no event for", Serilog.Events.LogEventLevel.Information));
    }

    /// <summary>One line for one silence, however many times the loop ticks.</summary>
    [Fact]
    public async Task The_silence_is_reported_once_and_not_once_per_tick()
    {
        _pipeline.Sink.TryPublish(new UserPromptSubmit
        {
            SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1", Prompt = "go",
        });

        Assert.True(await Until(() => _consumer.AppliedCount >= 1));

        _clock.AdvanceMinutes(11);
        Assert.True(await Until(() => _consumer.SilencedCount >= 1));

        // Let the loop go round many more times with the session still quiet.
        var ticks = _consumer.TickCount;
        Assert.True(await Until(() => _consumer.TickCount >= ticks + 5));

        Assert.Single(SilenceLines);
        Assert.Equal(1, _consumer.SilencedCount);
    }

    /// <summary>
    /// <strong>No sound and no nudge on entering it.</strong>
    /// </summary>
    /// <remarks>
    /// Nothing has happened that the operator must hear. A chime for "we stopped hearing from
    /// something" would fire on every long build — and this is the sweep running on the very loop
    /// that evaluates the nudge schedule, so the two are as close together as they will ever be.
    /// </remarks>
    [Fact]
    public async Task Going_silent_makes_no_sound_and_schedules_no_nudge()
    {
        _pipeline.Sink.TryPublish(new UserPromptSubmit
        {
            SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1", Prompt = "go",
        });

        Assert.True(await Until(() => _consumer.AppliedCount >= 1));

        _clock.AdvanceMinutes(11);
        Assert.True(await Until(() => _consumer.SilencedCount >= 1));

        // Several more ticks, so a nudge scheduled on entry would have had every chance to fire.
        var ticks = _consumer.TickCount;
        Assert.True(await Until(() => _consumer.TickCount >= ticks + 5));

        Assert.Empty(_player.Played);
    }

    /// <summary>Polls a condition without sleeping on a fixed duration.</summary>
    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }
}
