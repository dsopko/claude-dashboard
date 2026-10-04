using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The timings that would show a stall (T1.66, issue #86): kept in memory, warned once and cleared
/// after a quiet minute, shown in <c>/state</c> and the hourly line.
/// </summary>
public sealed class TimingsTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 13, 40, 0, TimeSpan.Zero);

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static List<string> Lines(RecordingLogSink log) =>
        [.. log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture))];

    private static List<string> Over(RecordingLogSink log, string timing) =>
        [.. Lines(log).Where(line => line.Contains($"The {timing} was", StringComparison.Ordinal) && line.Contains("over its limit", StringComparison.Ordinal))];

    private static List<string> BackUnder(RecordingLogSink log, string timing) =>
        [.. Lines(log).Where(line => line.Contains($"The {timing} is back under", StringComparison.Ordinal))];

    // ---- One timing ---------------------------------------------------------------------------

    /// <summary>
    /// One Warning at the first crossing, nothing for the values after it, and one all-clear only
    /// when a full minute has passed with no value over the limit.
    /// </summary>
    [Fact]
    public void A_timing_warns_once_and_clears_after_a_quiet_minute()
    {
        var log = new RecordingLogSink();
        var clock = new FakeClock(Start);
        var wait = new Timings(Logger(log), clock).QueueWait;

        foreach (var seconds in new[] { 0.01, 2, 3, 1.5, 0.1 })
        {
            wait.Record(TimeSpan.FromSeconds(seconds));
        }

        Assert.Single(Over(log, "queueWait"));
        Assert.Empty(BackUnder(log, "queueWait"));

        clock.Now += TimeSpan.FromSeconds(59);
        wait.CheckClear(clock.Now);
        Assert.Empty(BackUnder(log, "queueWait"));

        clock.Now += TimeSpan.FromSeconds(1);
        wait.CheckClear(clock.Now);
        wait.CheckClear(clock.Now + TimeSpan.FromSeconds(15));
        Assert.Single(BackUnder(log, "queueWait"));

        wait.Record(TimeSpan.FromSeconds(2.5));
        Assert.Equal(2, Over(log, "queueWait").Count);
        Assert.Equal(3, Lines(log).Count);

        var figure = wait.SinceStart;
        Assert.Equal(6, figure.Count);
        Assert.Equal(3000, figure.WorstShown);
        Assert.Equal(1000, figure.LimitShown);
    }

    /// <summary>
    /// Values that flap around the limit for five minutes give exactly one Warning, and one all-clear
    /// a minute after the last value over it: never a pair a value (the reviewer's probe gave 500).
    /// </summary>
    [Fact]
    public void A_figure_flapping_around_its_limit_warns_once_and_clears_once()
    {
        var log = new RecordingLogSink();
        var clock = new FakeClock(Start);
        var backlog = new Timings(Logger(log), clock).ArchiveBacklog;

        for (var second = 0; second < 300; second++)
        {
            clock.Now = Start + TimeSpan.FromSeconds(second);
            backlog.Record(second % 2 == 0 ? 513 : 511);

            if (second % 15 == 0)
            {
                backlog.CheckClear(clock.Now);
            }
        }

        var lastOver = Start + TimeSpan.FromSeconds(298);

        Assert.Single(Over(log, "archiveBacklog"));
        Assert.Empty(BackUnder(log, "archiveBacklog"));

        backlog.CheckClear(lastOver + TimeSpan.FromSeconds(59));
        Assert.Empty(BackUnder(log, "archiveBacklog"));

        backlog.CheckClear(lastOver + Timing.ClearAfter);
        backlog.CheckClear(lastOver + Timing.ClearAfter + TimeSpan.FromSeconds(15));
        Assert.Single(BackUnder(log, "archiveBacklog"));
        Assert.Single(Over(log, "archiveBacklog"));
    }

    // ---- The consumer -------------------------------------------------------------------------

    /// <summary>A consumer with a health board that has the timings, under a fake clock.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Serilog.Core.Logger _logger;

        public Rig()
        {
            Clock = new FakeClock(Start);
            Log = new RecordingLogSink();
            _logger = Logger(Log);
            Pipeline = new EventPipeline(Serilog.Core.Logger.None);
            Archive = new EventArchive(Serilog.Core.Logger.None);
            Timings = new Timings(_logger, Clock);
            Archive.Backlog = Timings.ArchiveBacklog;
            Rosters = new RosterStore(Pipeline.Sink, clock: Clock);

            var guard = new SingleWriterGuard();
            var registry = new SessionRegistry(new SingleWriterGuard());
            Board = new HealthBoard(new HealthSources { Version = "1.2.3" }, Clock, _logger, Timings);

            Consumer = new EventConsumer(
                Pipeline,
                registry,
                new SoundPolicyEngine(new RecordingSoundPlayer(), Clock, guard, new SoundPolicyOptions()),
                Clock,
                guard,
                _logger,
                new RecordingUiTick(),
                Archive,
                Rosters,
                recorder: TestDecisions.For(registry, Archive),
                tickInterval: TimeSpan.FromMilliseconds(20),
                health: Board);
        }

        public FakeClock Clock { get; }

        public RecordingLogSink Log { get; }

        public EventPipeline Pipeline { get; }

        public EventArchive Archive { get; }

        public Timings Timings { get; }

        public RosterStore Rosters { get; }

        public HealthBoard Board { get; }

        public EventConsumer Consumer { get; }

        public Task StartAsync() => Consumer.StartAsync(CancellationToken.None);

        /// <summary>Takes what the consumer handed to the archive, as the writer would.</summary>
        public void Drain()
        {
            while (Archive.Reader.TryRead(out _))
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Consumer.StopAsync(CancellationToken.None);
            Consumer.Dispose();
            _logger.Dispose();
        }
    }

    private static UserPromptSubmit Prompt(string session, DateTimeOffset arrived) =>
        new()
        {
            SessionId = new SessionId(session),
            Timestamp = arrived,
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "go",
        };

    /// <summary>
    /// An event that waited two seconds between its arrival and its apply: the queue wait is in the
    /// snapshot, one Warning is logged, and one all-clear a minute after, on the tick.
    /// </summary>
    [Fact]
    public async Task An_event_that_waits_past_a_second_warns_once_and_clears()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        // Arrived two seconds before the consumer's clock: the wait is the apply less the arrival.
        Assert.True(rig.Pipeline.Sink.TryPublish(Prompt("s-1", rig.Clock.Now - TimeSpan.FromSeconds(2))));
        Assert.True(SpinWait.SpinUntil(() => rig.Consumer.AppliedCount == 1, Generous));
        Assert.True(SpinWait.SpinUntil(() => rig.Board.Current?.Timings?.Figures[0].Count == 1, Generous));

        var wait = rig.Board.Current!.Timings!.Figures[0];
        Assert.Equal("queueWait", wait.Name);
        Assert.True(wait.WorstShown >= 2000, $"queue wait {wait.WorstShown} ms");
        Assert.Single(Over(rig.Log, "queueWait"));

        Assert.True(rig.Pipeline.Sink.TryPublish(Prompt("s-2", rig.Clock.Now)));
        Assert.True(SpinWait.SpinUntil(() => rig.Consumer.AppliedCount == 2, Generous));
        Assert.Empty(BackUnder(rig.Log, "queueWait"));

        rig.Clock.Now += Timing.ClearAfter;

        Assert.True(SpinWait.SpinUntil(() => BackUnder(rig.Log, "queueWait").Count == 1, Generous));
        Assert.Single(Over(rig.Log, "queueWait"));
    }

    /// <summary>A tick that runs ten seconds after it was due is measured as lateness, and warns once.</summary>
    [Fact]
    public async Task A_late_tick_is_measured_and_warns_once()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        Assert.True(SpinWait.SpinUntil(() => rig.Board.Current is not null, Generous));

        rig.Clock.Now += TimeSpan.FromSeconds(10);

        Assert.True(SpinWait.SpinUntil(() => rig.Board.Current!.Timings!.Figures[1].WorstShown >= 9_000, Generous));
        var ticks = rig.Consumer.TickCount;
        Assert.True(SpinWait.SpinUntil(() => rig.Consumer.TickCount >= ticks + 5, Generous));

        Assert.Single(Over(rig.Log, "tickLateness"));
    }

    /// <summary>
    /// A roster edit through the real <c>RosterStore.Replace</c> is stamped where it is published: a
    /// small queue wait and no Warning (the reviewer's probe: a default stamp gave 63.9 trillion ms).
    /// </summary>
    [Fact]
    public async Task A_roster_edit_has_a_small_queue_wait_and_no_warning()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        rig.Rosters.Replace(RosterBook.Empty);

        Assert.True(SpinWait.SpinUntil(() => rig.Timings.QueueWait.SinceStart.Count == 1, Generous));

        Assert.True(rig.Timings.QueueWait.SinceStart.WorstShown < 1000);
        Assert.Equal(0, rig.Timings.QueueWait.Skipped);
        Assert.Empty(Over(rig.Log, "queueWait"));
    }

    /// <summary>An event with no arrival instant is skipped, counted, and not recorded.</summary>
    [Fact]
    public async Task An_event_with_no_arrival_instant_is_skipped()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        Assert.True(rig.Pipeline.Sink.TryPublish(Prompt("s-1", default)));
        Assert.True(SpinWait.SpinUntil(() => rig.Timings.QueueWait.Skipped == 1, Generous));

        Assert.Equal(0, rig.Timings.QueueWait.SinceStart.Count);
        Assert.Empty(Over(rig.Log, "queueWait"));
    }

    /// <summary>A thousand ordinary events write no timing line.</summary>
    [Fact]
    public async Task A_thousand_ordinary_events_write_no_timing_line()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        for (var batch = 0; batch < 10; batch++)
        {
            for (var i = 0; i < 100; i++)
            {
                Assert.True(rig.Pipeline.Sink.TryPublish(Prompt($"s-{batch}-{i}", rig.Clock.Now)));
            }

            var expected = (batch + 1) * 100;
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    rig.Drain();
                    return rig.Consumer.AppliedCount == expected;
                },
                Generous));
        }

        Assert.DoesNotContain(Lines(rig.Log), line => line.Contains("over its limit", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(rig.Log), line => line.Contains("back under its limit", StringComparison.Ordinal));
        Assert.Equal(1000, rig.Timings.QueueWait.SinceStart.Count);
        Assert.Equal(1000, rig.Timings.ApplyTime.SinceStart.Count);
    }

    // ---- The wiring ---------------------------------------------------------------------------

    /// <summary>
    /// AppHost hands the three timings measured off the consumer to their owners: the archive, the
    /// window's dispatcher (the reviewer's plant d left it unwired) and the self-test.
    /// </summary>
    [Fact]
    public void AppHost_wires_the_three_hand_offs()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var host = AppHost.Build(new DashboardPaths(root));
            var timings = host.Services.GetRequiredService<Timings>();

            var dispatcher = Assert.IsType<WpfDispatcher>(host.Services.GetRequiredService<IUiDispatcher>());
            Assert.Same(timings.UiHop, dispatcher.Hop);
            Assert.Same(timings.ArchiveBacklog, host.Services.GetRequiredService<EventArchive>().Backlog);
            Assert.Same(timings.HookRoundTrip, host.Services.GetRequiredService<HookSelfTest>().RoundTrip);
            Assert.Same(timings, host.Services.GetRequiredService<HealthBoard>().Timings);

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

    // ---- /state and the lines -----------------------------------------------------------------

    /// <summary>
    /// <c>/state</c> answers <c>health.timings</c> with the seven figures; it is null before the first
    /// tick. The hourly line has the hour's figures, which then start again.
    /// </summary>
    [Fact]
    public void State_has_the_seven_figures_and_the_hourly_line_the_hours()
    {
        var log = new RecordingLogSink();
        using var logger = Logger(log);
        var clock = new FakeClock(Start);
        var timings = new Timings(logger, clock);
        var phases = new StartupPhases();
        phases.Mark("settings");
        phases.Mark("build");
        phases.Log(logger);

        var archive = new EventArchive(Serilog.Core.Logger.None);
        var recorder = TestDecisions.For(new SessionRegistry(new SingleWriterGuard()), archive);
        var board = new HealthBoard(new HealthSources { Version = "1.2.3" }, clock, logger, timings, phases);

        Assert.Null(new HealthEntry(null, null).With(board.Current).Timings);

        timings.QueueWait.Record(TimeSpan.FromMilliseconds(3));
        timings.ArchiveBacklog.Record(4);

        void Tick(DateTimeOffset at)
        {
            recorder.BeginTick(at);
            board.Tick(at, new ConsumerCounts(1, 0, 0, 1, 0, 0), recorder);
            recorder.Complete();
        }

        Tick(new DateTimeOffset(2026, 10, 4, 14, 0, 5, TimeSpan.Zero));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            new HealthEntry(null, null).With(board.Current), IngressEndpoints.StateOptions));
        var t = json.RootElement.GetProperty("timings");

        foreach (var name in new[] { "queueWait", "tickLateness", "applyTime", "archiveBacklog", "uiHop", "hookRoundTrip" })
        {
            var figure = t.GetProperty(name);
            Assert.Equal(JsonValueKind.Number, figure.GetProperty("count").ValueKind);
            Assert.Equal(JsonValueKind.Number, figure.GetProperty("average").ValueKind);
            Assert.Equal(JsonValueKind.Number, figure.GetProperty("worst").ValueKind);
            Assert.Equal(JsonValueKind.Number, figure.GetProperty("limit").ValueKind);
            Assert.Equal(JsonValueKind.Number, figure.GetProperty("skipped").ValueKind);
        }

        Assert.Equal(1, t.GetProperty("queueWait").GetProperty("count").GetInt64());
        Assert.Equal(1000, t.GetProperty("queueWait").GetProperty("limit").GetDouble());
        Assert.Equal("Milliseconds", t.GetProperty("queueWait").GetProperty("unit").GetString());
        Assert.Equal("Records", t.GetProperty("archiveBacklog").GetProperty("unit").GetString());
        Assert.Equal(512, t.GetProperty("archiveBacklog").GetProperty("limit").GetDouble());
        Assert.Equal(["settings", "build"], t.GetProperty("startup").EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        var hourly = Assert.Single(Lines(log), line => line.StartsWith("Hourly timings", StringComparison.Ordinal));
        Assert.Contains("queueWait n=1 avg=3ms worst=3ms", hourly, StringComparison.Ordinal);
        Assert.Contains("archiveBacklog n=1 avg=4 worst=4", hourly, StringComparison.Ordinal);
        Assert.Contains("hookRoundTrip n=0", hourly, StringComparison.Ordinal);

        // The next hour starts again, while /state keeps the figures since the start.
        Tick(new DateTimeOffset(2026, 10, 4, 15, 0, 5, TimeSpan.Zero));
        var second = Lines(log).Where(line => line.StartsWith("Hourly timings", StringComparison.Ordinal)).Last();
        Assert.Contains("queueWait n=0", second, StringComparison.Ordinal);
        Assert.Equal(1, board.Current!.Timings!.Figures[0].Count);
    }

    /// <summary>The start-up line lists the phases with their times, once.</summary>
    [Fact]
    public void The_start_up_line_lists_the_phases_once()
    {
        var log = new RecordingLogSink();
        using var logger = Logger(log);
        var phases = new StartupPhases();

        Assert.Null(phases.Finished);

        phases.Mark("settings");
        phases.Mark("port");
        phases.Mark("build");

        Assert.True(phases.Log(logger));
        Assert.False(phases.Log(logger));

        var line = Assert.Single(Lines(log));
        Assert.Matches(@"^Started in \d+ ms: settings=\d+ms port=\d+ms build=\d+ms$", line);
        Assert.Equal(["settings", "port", "build"], phases.Finished!.Select(phase => phase.Name));
    }
}
