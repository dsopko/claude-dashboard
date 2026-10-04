using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Storage;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The counts in <c>/state</c>, and the hourly summary (T1.65, issue #76).
/// </summary>
/// <remarks>
/// The board is driven tick by tick under a fake clock, with the real recorder and archive channel,
/// so the hour boundaries and the rows are deterministic. One test runs the real consumer to show
/// the summary is written inside its tick.
/// </remarks>
public sealed class HealthBoardTests
{
    /// <summary>13:40 UTC: the first summary, at the first tick after 14:00, is partial.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 13, 40, 0, TimeSpan.Zero);

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    /// <summary>A board, its recorder and its archive channel, as the consumer holds them.</summary>
    private sealed class Rig
    {
        public Rig(HealthSources? sources = null, DateTimeOffset? start = null)
        {
            Clock = new FakeClock(start ?? Start);
            Log = new RecordingLogSink();
            Archive = new EventArchive(Serilog.Core.Logger.None);
            Recorder = TestDecisions.For(new SessionRegistry(new SingleWriterGuard()), Archive);
            Board = new HealthBoard(sources ?? new HealthSources { Version = "1.2.3", Port = 5000, CanReceive = true }, Clock, Logger(Log));
        }

        public FakeClock Clock { get; }

        public RecordingLogSink Log { get; }

        public EventArchive Archive { get; }

        public DecisionRecorder Recorder { get; }

        public HealthBoard Board { get; }

        /// <summary>One tick at <paramref name="at"/>, inside a tick scope, as the consumer runs it.</summary>
        public void Tick(DateTimeOffset at, ConsumerCounts counts)
        {
            Clock.Now = at;
            Recorder.BeginTick(at);

            try
            {
                Board.Tick(at, counts, Recorder);
            }
            finally
            {
                Recorder.Complete();
            }
        }

        /// <summary>The <c>HourlySummary</c> rows the recorder handed to the archive so far.</summary>
        public List<Decision> Summaries()
        {
            var rows = new List<Decision>();

            while (Archive.Reader.TryRead(out var record))
            {
                rows.AddRange(record.Decisions.Where(d => d.Kind == DecisionKind.HourlySummary));
            }

            return rows;
        }

        public List<string> SummaryLines() =>
            [.. Log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture))
                .Where(line => line.StartsWith("Hourly summary", StringComparison.Ordinal))];
    }

    private static ConsumerCounts Applied(long applied, long ticks) => new(applied, 0, 0, ticks, 0, 0);

    /// <summary>
    /// A run that crosses two clock hours writes two lines and two rows, at the first tick after
    /// each hour and at no other tick. Line, row and <c>/state</c> agree; the first is partial.
    /// </summary>
    [Fact]
    public void Two_clock_hours_give_two_summaries_at_the_first_tick_after_each()
    {
        var rig = new Rig();
        var summaries = new List<Decision>();

        rig.Tick(Start + TimeSpan.FromMinutes(10), Applied(1, 1));
        summaries.AddRange(rig.Summaries());
        Assert.Empty(summaries);

        var firstAt = new DateTimeOffset(2026, 10, 4, 14, 0, 5, TimeSpan.Zero);
        rig.Tick(firstAt, Applied(4, 2));
        var first = Assert.Single(rig.Summaries());
        var snapshotAtFirst = rig.Board.Current!;

        rig.Tick(firstAt + TimeSpan.FromSeconds(15), Applied(5, 3));
        rig.Tick(new DateTimeOffset(2026, 10, 4, 14, 59, 59, TimeSpan.Zero), Applied(6, 4));
        Assert.Empty(rig.Summaries());

        var secondAt = new DateTimeOffset(2026, 10, 4, 15, 0, 5, TimeSpan.Zero);
        rig.Tick(secondAt, Applied(9, 5));
        var second = Assert.Single(rig.Summaries());

        rig.Tick(secondAt + TimeSpan.FromSeconds(15), Applied(9, 6));
        Assert.Empty(rig.Summaries());

        // The rows: no event, no session; the first is partial.
        Assert.All([first, second], row => Assert.Null(row.SessionId));
        Assert.Equal("partial", first.Reason);
        Assert.Null(second.Reason);
        Assert.Equal(firstAt, first.Ts);
        Assert.Equal(secondAt, second.Ts);

        Assert.Equal(
            "applied=4 declined=0 uncorrelated=0 shed=0 lost=0 archiveDropped=0 refused=0 notWritten=0 ticks=2 sweeps=0 settles=0",
            first.Detail);
        Assert.Equal(
            "applied=5 declined=0 uncorrelated=0 shed=0 lost=0 archiveDropped=0 refused=0 notWritten=0 ticks=3 sweeps=0 settles=0",
            second.Detail);

        // The lines carry the same counts, and /state's last hour at that moment is the same.
        var lines = rig.SummaryLines();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith(first.Detail, lines[0], StringComparison.Ordinal);
        Assert.Contains("partial since the start", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(second.Detail, lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("partial", lines[1], StringComparison.Ordinal);

        Assert.Equal(first.Detail, snapshotAtFirst.LastHour!.ToDetail());
        Assert.True(snapshotAtFirst.LastHourPartial);
        Assert.Equal(Start, snapshotAtFirst.LastHourFrom);
    }

    /// <summary>The hour's counts start again at each summary; the counts since the start do not.</summary>
    [Fact]
    public void The_hours_counts_reset_at_each_summary_and_the_totals_do_not()
    {
        var rig = new Rig();

        rig.Tick(Start + TimeSpan.FromMinutes(10), Applied(5, 1));
        Assert.Equal(5, rig.Board.Current!.ThisHour.Applied);

        rig.Tick(new DateTimeOffset(2026, 10, 4, 14, 0, 5, TimeSpan.Zero), Applied(8, 2));
        Assert.Equal(8, rig.Board.Current!.LastHour!.Applied);
        Assert.Equal(0, rig.Board.Current!.ThisHour.Applied);
        Assert.Equal(8, rig.Board.Current!.SinceStart.Applied);

        rig.Tick(new DateTimeOffset(2026, 10, 4, 14, 30, 0, TimeSpan.Zero), Applied(10, 3));
        Assert.Equal(2, rig.Board.Current!.ThisHour.Applied);
        Assert.Equal(10, rig.Board.Current!.SinceStart.Applied);

        rig.Tick(new DateTimeOffset(2026, 10, 4, 15, 0, 5, TimeSpan.Zero), Applied(11, 4));
        Assert.Equal(3, rig.Board.Current!.LastHour!.Applied);
        Assert.Equal(11, rig.Board.Current!.SinceStart.Applied);
    }

    /// <summary>
    /// <c>/state</c>'s <c>health</c> is built from the published snapshot only: a live count that moves
    /// after the tick does not reach it until the next tick.
    /// </summary>
    [Fact]
    public void Health_reads_the_snapshot_and_not_the_live_counts()
    {
        var refused = 1L;
        var rig = new Rig(new HealthSources { Version = "1.2.3", Refused = () => refused });

        Assert.Null(new HealthEntry(null, null).With(rig.Board.Current).Counts);

        rig.Tick(Start + TimeSpan.FromMinutes(1), Applied(3, 1));

        refused = 99;

        var entry = new HealthEntry(null, null).With(rig.Board.Current);
        Assert.Equal(1, entry.Counts!.SinceStart.Refused);
        Assert.Equal(3, entry.Counts.SinceStart.Applied);
        Assert.Equal((Start + TimeSpan.FromMinutes(1)).UtcDateTime, entry.CountedAt);
    }

    /// <summary>Every new <c>health</c> field, as <c>/state</c> writes it, with its type.</summary>
    [Fact]
    public void Health_answers_every_field_with_its_type()
    {
        var rig = new Rig(new HealthSources
        {
            Version = "1.2.3",
            Port = 5000,
            CanReceive = true,
            DatabaseAvailable = () => true,
            SoundOutput = () => false,
            Paused = () => true,
            MutedUntil = () => Start + TimeSpan.FromHours(2),
        });

        rig.Tick(new DateTimeOffset(2026, 10, 4, 14, 0, 5, TimeSpan.Zero), Applied(2, 1));

        var entry = new HealthEntry(Start.UtcDateTime, null).With(rig.Board.Current);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(entry, IngressEndpoints.StateOptions));
        var health = json.RootElement;

        Assert.EndsWith("Z", health.GetProperty("lastHeardAt").GetString(), StringComparison.Ordinal);
        Assert.Equal("1.2.3", health.GetProperty("version").GetString());
        Assert.EndsWith("Z", health.GetProperty("startedAt").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", health.GetProperty("countedAt").GetString(), StringComparison.Ordinal);
        Assert.Equal(5000, health.GetProperty("ingress").GetProperty("port").GetInt32());
        Assert.True(health.GetProperty("ingress").GetProperty("receiving").GetBoolean());
        Assert.Equal("Writing", health.GetProperty("database").GetString());
        Assert.False(health.GetProperty("soundOutput").GetBoolean());
        Assert.True(health.GetProperty("modes").GetProperty("paused").GetBoolean());
        Assert.EndsWith("Z", health.GetProperty("modes").GetProperty("mutedUntil").GetString(), StringComparison.Ordinal);

        var counts = health.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("sinceStart").GetProperty("applied").GetInt64());
        Assert.Equal(0, counts.GetProperty("thisHour").GetProperty("applied").GetInt64());

        var lastHour = counts.GetProperty("lastHour");
        Assert.True(lastHour.GetProperty("partial").GetBoolean());
        Assert.EndsWith("Z", lastHour.GetProperty("from").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, lastHour.GetProperty("counts").GetProperty("applied").GetInt64());

        foreach (var name in new[] { "applied", "declined", "uncorrelated", "shed", "lost", "archiveDropped", "refused", "notWritten", "ticks", "sweeps", "settles" })
        {
            Assert.Equal(JsonValueKind.Number, counts.GetProperty("sinceStart").GetProperty(name).ValueKind);
        }
    }

    /// <summary>
    /// The real consumer writes the summary inside its tick, and its stop line gives the counts since
    /// the start in the same form.
    /// </summary>
    [Fact]
    public async Task The_consumer_writes_the_summary_in_its_tick_and_the_counts_at_its_stop()
    {
        var clock = new FakeClock(Start);
        var log = new RecordingLogSink();
        using var logger = Logger(log);
        var guard = new SingleWriterGuard();
        var registry = new SessionRegistry(new SingleWriterGuard());
        var archive = new EventArchive(Serilog.Core.Logger.None);
        var board = new HealthBoard(new HealthSources { Version = "1.2.3" }, clock, logger);

        using var consumer = new EventConsumer(
            new EventPipeline(Serilog.Core.Logger.None),
            registry,
            new SoundPolicyEngine(new RecordingSoundPlayer(), clock, guard, new SoundPolicyOptions()),
            clock,
            guard,
            logger,
            new RecordingUiTick(),
            archive,
            new RosterStore(new RecordingEventSink()),
            recorder: TestDecisions.For(registry, archive),
            tickInterval: TimeSpan.FromMilliseconds(20),
            health: board);

        await consumer.StartAsync(CancellationToken.None);

        Assert.True(SpinWait.SpinUntil(() => board.Current is not null, TimeSpan.FromSeconds(30)));
        clock.Now = new DateTimeOffset(2026, 10, 4, 14, 0, 5, TimeSpan.Zero);

        var found = new List<Decision>();
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                while (archive.Reader.TryRead(out var record))
                {
                    found.AddRange(record.Decisions.Where(d => d.Kind == DecisionKind.HourlySummary));
                }

                return found.Count > 0;
            },
            TimeSpan.FromSeconds(30)));

        await consumer.StopAsync(CancellationToken.None);

        var row = Assert.Single(found);
        Assert.Equal("partial", row.Reason);

        var stop = Assert.Single(
            log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture)),
            line => line.StartsWith("Event consumer stopped", StringComparison.Ordinal));
        Assert.Matches(@"Since the start: applied=\d+ declined=\d+ .* ticks=\d+ sweeps=\d+ settles=\d+$", stop);
    }

    /// <summary>
    /// The summary comes at the UTC hour, not the local one: with a clock at +05:30, a start at 19:10
    /// local (13:40 UTC) gets its summary at the first tick after 14:00 UTC (19:30 local), and none at
    /// the local hour, 20:00 (14:30 UTC).
    /// </summary>
    [Fact]
    public void The_summary_comes_at_the_utc_hour_with_a_half_hour_offset()
    {
        var offset = TimeSpan.FromHours(5.5);
        var rig = new Rig(start: new DateTimeOffset(2026, 10, 4, 19, 10, 0, offset));

        rig.Tick(new DateTimeOffset(2026, 10, 4, 19, 25, 0, offset), Applied(1, 1));
        Assert.Empty(rig.Summaries());

        var utcHour = new DateTimeOffset(2026, 10, 4, 19, 35, 0, offset);
        rig.Tick(utcHour, Applied(2, 2));
        var row = Assert.Single(rig.Summaries());
        Assert.Equal(utcHour, row.Ts);
        Assert.Equal("partial", row.Reason);

        rig.Tick(new DateTimeOffset(2026, 10, 4, 20, 5, 0, offset), Applied(3, 3));
        Assert.Empty(rig.Summaries());
    }

    /// <summary>
    /// <c>notWritten</c> is the writer's count since the start: after a failure, a record lost inside
    /// the retry minute and a recovery, the store's own <c>LostCount</c> is 0 again and
    /// <c>notWritten</c> still says 2. Through AppHost's own wiring of the sources.
    /// </summary>
    [Fact]
    public async Task Not_written_keeps_counting_through_a_recovery()
    {
        var folder = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var clock = new FakeClock(Start);
            using var store = new SqliteEventStore(Path.Combine(folder, "dashboard.db"), Serilog.Core.Logger.None, clock);
            var archive = new EventArchive(Serilog.Core.Logger.None);
            using var writer = new EventArchiveWriter(archive, store, Serilog.Core.Logger.None);

            var sources = ClaudeDashboard.App.Hosting.AppHost.HealthSourcesFor(
                ClaudeDashboard.App.Hosting.IngressStatus.Healthy(5000),
                new EventPipeline(Serilog.Core.Logger.None),
                archive,
                new HookHealth(),
                writer,
                store,
                new FixedOutput(),
                new SettableSoundModes());

            await writer.StartAsync(CancellationToken.None);

            store.InsideTransaction = () => throw new Microsoft.Data.Sqlite.SqliteException("planted: database or disk is full", 13);

            archive.TryArchive(new ArchiveRecord(TestEvents.Hook("{}"), []));
            Assert.True(SpinWait.SpinUntil(() => writer.RefusedCount == 1, TimeSpan.FromSeconds(30)));

            // Inside the retry minute: lost without an attempt.
            archive.TryArchive(new ArchiveRecord(TestEvents.Hook("{}"), []));
            Assert.True(SpinWait.SpinUntil(() => writer.RefusedCount == 2, TimeSpan.FromSeconds(30)));

            Assert.Equal(2, store.LostCount);
            Assert.Equal(2, sources.NotWritten());

            store.InsideTransaction = null;
            clock.Now += SqliteEventStore.RetryAfter;

            archive.TryArchive(new ArchiveRecord(TestEvents.Hook("{}"), []));
            Assert.True(SpinWait.SpinUntil(() => writer.WrittenCount == 1, TimeSpan.FromSeconds(30)));

            Assert.Equal(0, store.LostCount);
            Assert.Equal(2, sources.NotWritten());

            await writer.StopAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Disposable temp folder.
            }
        }
    }

    /// <summary>An output that is always bound.</summary>
    private sealed class FixedOutput : ClaudeDashboard.App.Adapters.ISoundOutput
    {
        public bool HasOutput => true;
    }

    /// <summary>Replay over a history that spans hours writes no hourly summary: it is not a running process.</summary>
    [Fact]
    public void Replay_writes_no_hourly_summary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var path = Path.Combine(folder, "replay.db");
            var mapperClock = new FakeClock(Start);
            var mapper = new HookEventMapper(mapperClock);

            using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
            {
                for (var minutes = 0; minutes <= 180; minutes += 30)
                {
                    mapperClock.Now = Start + TimeSpan.FromMinutes(minutes);
                    var body = $$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-{{minutes}}","cwd":"C:\\w","prompt_id":"p","prompt":"go"}""";
                    var mapping = mapper.Map(JsonSerializer.Deserialize<HookPayload>(body)!, new PayloadJson(body));
                    Assert.True(store.Append(mapping.Event!));
                }
            }

            Assert.Equal(0, ReplaySwitch.Run(path, _ => { }, Serilog.Core.Logger.None));

            Assert.NotEmpty(ForeignSqliteReader.Column(path, "SELECT id FROM decisions"));
            Assert.Empty(ForeignSqliteReader.Column(path, "SELECT id FROM decisions WHERE kind = 'HourlySummary'"));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Disposable temp folder.
            }
        }
    }
}
