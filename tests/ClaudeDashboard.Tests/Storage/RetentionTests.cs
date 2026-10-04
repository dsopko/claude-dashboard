using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The history keeps the retention window, 30 days by default, and settings keep keys a version
/// does not know (T1.64, issues #81 and #93).
/// </summary>
/// <remarks>
/// Scratch folders only: never the operator's <c>settings.json</c> or <c>dashboard.db</c>. Every
/// read of a database file goes through <see cref="ForeignSqliteReader"/>.
/// </remarks>
public sealed class RetentionTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public RetentionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }
    }

    private string Db() => Path.Combine(_folder, "dashboard.db");

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static UserPromptSubmit Hook(DateTimeOffset at, string marker = "{}") =>
        TestEvents.Hook(marker) with { Timestamp = at };

    private static Decision Row(DateTimeOffset at, DecisionKind kind = DecisionKind.StateMoved) =>
        new(at, "session-1", kind);

    /// <summary>One event with a decision, and one decision with no event, at each of two ages.</summary>
    private static void Fill(SqliteEventStore store)
    {
        foreach (var days in new[] { 31, 29 })
        {
            var at = Now - TimeSpan.FromDays(days);

            Assert.True(store.Append(new ArchiveRecord(Hook(at, $$"""{"age":{{days}}}"""), [Row(at)])));
            Assert.True(store.Append(new ArchiveRecord(null, [Row(at, DecisionKind.SilenceSwept)])));
        }
    }

    // ---- The prune ----------------------------------------------------------------------------

    /// <summary>
    /// With the default, the 31-day event, its decision and the 31-day decision with no event are
    /// deleted; the 29-day ones are kept.
    /// </summary>
    [Fact]
    public void The_default_keeps_29_days_and_deletes_31()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Fill(store);

            var counts = store.Prune(HistorySettings.DefaultRetentionDays, Now, keepRunId: null);

            Assert.Equal(new PruneCounts(1, 2, 0), counts);
        }

        Assert.Equal(["""{"age":29}"""], ForeignSqliteReader.Column(path, "SELECT payload_json FROM events"));
        Assert.Equal(
            ["StateMoved|2", "SilenceSwept|NULL"],
            ForeignSqliteReader.Column(path, "SELECT kind || '|' || IFNULL(event_id, 'NULL') FROM decisions ORDER BY id"));
        Assert.All(
            ForeignSqliteReader.Column(path, "SELECT ts FROM decisions"),
            ts => Assert.StartsWith((Now - TimeSpan.FromDays(29)).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ts, StringComparison.Ordinal));
    }

    /// <summary>With 0, nothing is deleted.</summary>
    [Fact]
    public void Zero_keeps_everything()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Fill(store);

            Assert.Equal(PruneCounts.None, store.Prune(0, Now, keepRunId: null));
        }

        Assert.Equal(2, ForeignSqliteReader.Column(path, "SELECT id FROM events").Count);
        Assert.Equal(4, ForeignSqliteReader.Column(path, "SELECT id FROM decisions").Count);
    }

    /// <summary>Runs older than the limit go; this process's run stays, however long ago it started.</summary>
    [Fact]
    public void Old_runs_go_and_the_present_run_stays()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            var present = store.StartRun(new RunStart("1.0.0", 5000, _folder), Now - TimeSpan.FromDays(40));
            store.StartRun(new RunStart("1.0.0", 5000, _folder), Now - TimeSpan.FromDays(35));
            var recent = store.StartRun(new RunStart("1.0.0", 5000, _folder), Now - TimeSpan.FromDays(1));

            Assert.Equal(new PruneCounts(0, 0, 1), store.Prune(30, Now, present));

            Assert.Equal(
                [present!.Value.ToString(CultureInfo.InvariantCulture), recent!.Value.ToString(CultureInfo.InvariantCulture)],
                ForeignSqliteReader.Column(path, "SELECT id FROM runs ORDER BY id"));
        }
    }

    /// <summary>A prune that fails rolls back: nothing is deleted, and the history notice shows.</summary>
    [Fact]
    public void A_failed_prune_deletes_nothing_and_shows_the_notice()
    {
        var path = Db();
        var clock = new FakeClock(Now);

        using var store = new SqliteEventStore(path, Serilog.Core.Logger.None, clock);
        Fill(store);

        var notice = new HistoryNotice(() => store.Available == false);
        store.InsidePrune = () => throw new SqliteException("planted: database or disk is full", 13);

        Assert.Null(store.Prune(30, Now, keepRunId: null));
        notice.Tick(clock.Now);

        Assert.True(notice.IsShown);
        Assert.Equal(2, ForeignSqliteReader.Column(path, "SELECT id FROM events").Count);
        Assert.Equal(4, ForeignSqliteReader.Column(path, "SELECT id FROM decisions").Count);
    }

    /// <summary>
    /// Inside the minute after a failure, a prune is skipped: it deletes nothing, and counts neither a
    /// failure nor a lost record, because a prune is not a record (T1.65, from T1.64's review). After
    /// the minute it prunes.
    /// </summary>
    [Fact]
    public void A_prune_inside_the_retry_minute_is_skipped_and_counts_no_lost_record()
    {
        var path = Db();
        var clock = new FakeClock(Now);

        using var store = new SqliteEventStore(path, Serilog.Core.Logger.None, clock);
        Fill(store);

        store.InsidePrune = () => throw new SqliteException("planted: database or disk is full", 13);
        Assert.Null(store.Prune(30, clock.Now, keepRunId: null));
        store.InsidePrune = null;

        var lost = store.LostCount;
        var failed = store.FailedCount;

        clock.Now = Now + TimeSpan.FromSeconds(30);
        Assert.Null(store.Prune(30, clock.Now, keepRunId: null));

        Assert.Equal(lost, store.LostCount);
        Assert.Equal(failed, store.FailedCount);
        Assert.Equal(2, ForeignSqliteReader.Column(path, "SELECT id FROM events").Count);

        clock.Now = Now + SqliteEventStore.RetryAfter;
        Assert.Equal(new PruneCounts(1, 2, 0), store.Prune(30, clock.Now, keepRunId: null));
    }

    // ---- The writer ---------------------------------------------------------------------------

    /// <summary>
    /// The writer prunes once at start, after the run row, keeping this run, and then 24 hours later
    /// under a fake clock, and not before. It prunes on its own loop: the code path shows it, because
    /// only that loop calls PruneIfDue. A thread id would not show it (a pool thread can run both).
    /// </summary>
    [Fact]
    public async Task The_writer_prunes_at_start_and_then_once_a_day()
    {
        var store = new PruningStore();
        var archive = new EventArchive(Serilog.Core.Logger.None);
        var clock = new FakeClock(Now);
        using var started = new CancellationTokenSource();

        using var writer = new EventArchiveWriter(
            archive, store, Serilog.Core.Logger.None, new RunStart("1.0.0", 5000, _folder), clock, 30, started.Token);

        await writer.StartAsync(CancellationToken.None);

        started.Cancel();

        await store.Pruned.WaitAsync(Generous);
        Assert.Equal([Now], store.PruneTimes);
        Assert.Equal(PruningStore.RunId, store.KeptRun);

        // A record 23 hours later: written, and no prune.
        clock.Now = Now + TimeSpan.FromHours(23);
        archive.TryArchive(new ArchiveRecord(Hook(clock.Now), []));
        await store.Appended.WaitAsync(Generous);
        Assert.Equal([Now], store.PruneTimes);

        // A record at 24 hours: the second prune, before it is written.
        clock.Now = Now + EventArchiveWriter.PruneEvery;
        archive.TryArchive(new ArchiveRecord(Hook(clock.Now), []));
        await store.Appended.WaitAsync(Generous);
        Assert.Equal([Now, Now + EventArchiveWriter.PruneEvery], store.PruneTimes);

        await writer.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// At start, the retention line comes before any prune line, and neither holds a payload or a
    /// row's time. The prune line has the counts.
    /// </summary>
    [Fact]
    public async Task The_retention_line_comes_before_the_prune_line()
    {
        const string Marker = "a-payload-marker";
        var path = Db();
        var log = new RecordingLogSink();
        var logger = Logger(log);
        var clock = new FakeClock(Now);
        var old = Now - TimeSpan.FromDays(31);

        using var store = new SqliteEventStore(path, logger, clock);
        Assert.True(store.Append(new ArchiveRecord(Hook(old, $$"""{"prompt":"{{Marker}}"}"""), [Row(old)])));

        var archive = new EventArchive(logger);
        using var started = new CancellationTokenSource();

        using (var writer = new EventArchiveWriter(archive, store, logger, new RunStart("1.0.0", 5000, _folder), clock, 30, started.Token))
        {
            await writer.StartAsync(CancellationToken.None);
            started.Cancel();

            Assert.True(SpinWait.SpinUntil(() => Lines(log).Any(line => line.Contains("Pruned", StringComparison.Ordinal)), Generous));

            await writer.StopAsync(CancellationToken.None);
        }

        var lines = Lines(log);
        var retention = lines.FindIndex(line => line.Contains("The history keeps 30 days", StringComparison.Ordinal));
        var pruned = lines.FindIndex(line => line.Contains("Pruned", StringComparison.Ordinal));

        Assert.True(retention >= 0 && retention < pruned, string.Join(Environment.NewLine, lines));
        Assert.Contains("deleted 1 events, 1 decisions and 0 runs", lines[pruned], StringComparison.Ordinal);

        var oldTime = old.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        foreach (var line in new[] { lines[retention], lines[pruned] })
        {
            Assert.DoesNotContain(Marker, line, StringComparison.Ordinal);
            Assert.DoesNotContain(oldTime, line, StringComparison.Ordinal);
        }
    }

    private static List<string> Lines(RecordingLogSink log) =>
        [.. log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture))];

    // ---- The setting --------------------------------------------------------------------------

    /// <summary>
    /// A negative value is the default, and the start logs one repaired-value line that does not
    /// repeat the value.
    /// </summary>
    [Fact]
    public void A_negative_value_is_the_default_and_is_logged_once()
    {
        var paths = new DashboardPaths(_folder);
        File.WriteAllText(paths.SettingsFile, """{ "history": { "retentionDays": -7 } }""");

        var loaded = new SettingsStore(paths).Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(HistorySettings.DefaultRetentionDays, loaded.Settings.History.RetentionDays);

        using (var host = AppHost.Build(paths))
        {
            (host.Services.GetService(typeof(Serilog.ILogger)) as IDisposable)?.Dispose();
        }

        var lines = File.ReadAllLines(Directory.EnumerateFiles(paths.LogFolder, "*.log").Single())
            .Where(line => line.Contains("with a value repaired", StringComparison.Ordinal))
            .ToList();

        var line = Assert.Single(lines);
        Assert.Contains("\"history.retentionDays\" setting is negative", line, StringComparison.Ordinal);
        Assert.DoesNotContain("-7", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repaired-port sentence names the setting and the repair, never the value (T1.65, from
    /// T1.64's review: T1.56 set that no setting value is logged).
    /// </summary>
    [Fact]
    public void A_port_that_is_not_a_port_is_repaired_without_its_value()
    {
        var paths = new DashboardPaths(_folder);
        File.WriteAllText(paths.SettingsFile, """{ "port": 99999 }""");

        var problem = new SettingsStore(paths).Load().Problem;

        Assert.NotNull(problem);
        Assert.StartsWith("The \"port\" setting is not a usable port.", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(" , ", problem, StringComparison.Ordinal);
    }

    /// <summary>An absent value, and an absent section, are the default; 36,525 days fits.</summary>
    [Theory]
    [InlineData("{}", 30)]
    [InlineData("""{ "history": {} }""", 30)]
    [InlineData("""{ "history": { "retentionDays": 0 } }""", 0)]
    [InlineData("""{ "history": { "retentionDays": 36525 } }""", 36_525)]
    public void The_setting_reads_as_the_ruling_says(string json, int expected)
    {
        var paths = new DashboardPaths(_folder);
        File.WriteAllText(paths.SettingsFile, json);

        Assert.Equal(expected, new SettingsStore(paths).Load().Settings.History.RetentionDays);
    }

    /// <summary>
    /// A key this version does not know is written back unchanged by a save, as a save at quit does.
    /// </summary>
    [Fact]
    public void A_key_this_version_does_not_know_survives_a_save()
    {
        var paths = new DashboardPaths(_folder);
        File.WriteAllText(paths.SettingsFile, """
            {
              "history": { "retentionDays": 0 },
              "fromANewerVersion": { "level": 3, "names": [ "a", "b" ] },
              "alsoNew": true
            }
            """);

        var store = new SettingsStore(paths);
        var loaded = store.Load().Settings;

        Assert.True(store.Save(loaded with { Window = new WindowSettings { Left = 10, Top = 20 } }));

        using var saved = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
        var root = saved.RootElement;

        Assert.Equal("""{"level":3,"names":["a","b"]}""", root.GetProperty("fromANewerVersion").GetRawText().Replace(" ", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));
        Assert.True(root.GetProperty("alsoNew").GetBoolean());
        Assert.Equal(0, root.GetProperty("history").GetProperty("retentionDays").GetInt32());
        Assert.Equal(10, root.GetProperty("window").GetProperty("left").GetDouble());
    }

    /// <summary>Records each call, and signals each prune and each append.</summary>
    private sealed class PruningStore : IEventStore
    {
        public const long RunId = 11;

        private readonly Lock _gate = new();
        private readonly List<DateTimeOffset> _pruneTimes = [];

        public SemaphoreSlim Pruned { get; } = new(0);

        public SemaphoreSlim Appended { get; } = new(0);

        public long? KeptRun { get; private set; }

        public List<DateTimeOffset> PruneTimes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pruneTimes];
                }
            }
        }

        public bool Append(InboundEvent inboundEvent) => Append(new ArchiveRecord(inboundEvent, []));

        public bool Append(ArchiveRecord record)
        {
            Appended.Release();
            return true;
        }

        public long? StartRun(RunStart run, DateTimeOffset startedAt) => RunId;

        public bool StopRun(long runId, DateTimeOffset stoppedAt) => true;

        public PruneCounts? Prune(int retentionDays, DateTimeOffset now, long? keepRunId)
        {
            lock (_gate)
            {
                _pruneTimes.Add(now);
            }

            KeptRun = keepRunId;
            Pruned.Release();

            return PruneCounts.None;
        }
    }
}
