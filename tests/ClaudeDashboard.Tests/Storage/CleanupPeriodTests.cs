using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The history follows Claude Code's <c>cleanupPeriodDays</c> (T1.68, issue #102): the real reader over
/// a scratch Claude Code folder, the real store, and the archive writer under a fake clock.
/// </summary>
/// <remarks>
/// Scratch folders only. Claude Code's settings file here is a fake one in a temp folder, never the
/// operator's <c>~/.claude/settings.json</c>, and no database is the operator's.
/// </remarks>
public sealed class CleanupPeriodTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private const string DefaultLine = "History follows Claude Code's cleanupPeriodDays: keeps 30 days (Claude Code's default).";
    private const string NotReadLine = "Claude Code's settings could not be read: history is kept in full.";
    private const string NotValidLine = "Claude Code's cleanupPeriodDays is not valid: history is kept in full.";
    private const string TooLargeLine = "Claude Code's cleanupPeriodDays is too large to count back from today: history is kept in full.";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public CleanupPeriodTests() => Directory.CreateDirectory(ClaudeFolder);

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

    private string ClaudeFolder => Path.Combine(_folder, "claude-config");

    private ClaudeCodePaths Claude => new(ClaudeFolder);

    private string ClaudeSettings => Claude.UserSettingsFile;

    private string Db() => Path.Combine(_folder, "dashboard.db");

    // ---- The reader ----------------------------------------------------------------------------

    /// <summary>What the reader finds, file by file. It judges nothing: the rule does.</summary>
    [Theory]
    [InlineData(null, CleanupPeriodKind.NotRead)]
    [InlineData("", CleanupPeriodKind.NotRead)]
    [InlineData("not json", CleanupPeriodKind.NotRead)]
    [InlineData("[30]", CleanupPeriodKind.NotRead)]
    [InlineData("{}", CleanupPeriodKind.Absent)]
    [InlineData("""{ "CleanupPeriodDays": 10 }""", CleanupPeriodKind.Absent)]
    [InlineData("""{ "cleanupPeriodDays": "30" }""", CleanupPeriodKind.NotANumber)]
    [InlineData("""{ "cleanupPeriodDays": true }""", CleanupPeriodKind.NotANumber)]
    [InlineData("""{ "cleanupPeriodDays": null }""", CleanupPeriodKind.NotANumber)]
    [InlineData("""{ "cleanupPeriodDays": 1e400 }""", CleanupPeriodKind.HugeNumber)]
    [InlineData("""{ "cleanupPeriodDays": -1e400 }""", CleanupPeriodKind.HugeNegativeNumber)]
    [InlineData("{ \"cleanupPeriodDays\": 10, // a comment and a trailing comma, as the hook check allows\n}", CleanupPeriodKind.Number)]
    public void The_reader_says_what_it_found(string? json, CleanupPeriodKind expected)
    {
        if (json is not null)
        {
            File.WriteAllText(ClaudeSettings, json);
        }

        Assert.Equal(expected, new ClaudeCleanupPeriod(Claude).Read().Kind);
    }

    /// <summary>A file another process holds locked cannot be read.</summary>
    [Fact]
    public void A_locked_file_is_not_read()
    {
        File.WriteAllText(ClaudeSettings, """{ "cleanupPeriodDays": 10 }""");

        using var locked = new FileStream(ClaudeSettings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(CleanupPeriodRead.NotRead, new ClaudeCleanupPeriod(Claude).Read());
    }

    // ---- The prune, through the writer ---------------------------------------------------------

    /// <summary>
    /// <strong>No key in a readable file:</strong> the rows at 31 days go, in <c>events</c>,
    /// <c>decisions</c> and <c>runs</c>, and the rows at 29 days stay. The line gives the default.
    /// </summary>
    [Fact]
    public async Task No_key_keeps_thirty_days()
    {
        File.WriteAllText(ClaudeSettings, """{ "model": "opus" }""");

        var run = await Prune([31, 29], expectDeletes: true);

        Assert.Equal([29], run.EventAges);
        Assert.Equal([29, 29], run.DecisionAges);
        Assert.Equal([29], run.RunAges);
        Assert.Contains(DefaultLine, run.Lines);
    }

    /// <summary><strong>With <c>cleanupPeriodDays: 10</c></strong>, the rows at 11 days go and the rows at 9 days stay.</summary>
    [Fact]
    public async Task Ten_days_keeps_nine_and_deletes_eleven()
    {
        File.WriteAllText(ClaudeSettings, """{ "cleanupPeriodDays": 10 }""");

        var run = await Prune([11, 9], expectDeletes: true);

        Assert.Equal([9], run.EventAges);
        Assert.Equal([9, 9], run.DecisionAges);
        Assert.Equal([9], run.RunAges);
        Assert.Contains("History follows Claude Code's cleanupPeriodDays: keeps 10 days.", run.Lines);
    }

    /// <summary>
    /// <strong>No file, a file that is not JSON, and the values 0, -1, 1.5, "30", true and 99999999:</strong>
    /// nothing is deleted, the line says why without the value, and no notice shows: a read that fails
    /// is not a history failure.
    /// </summary>
    [Theory]
    [InlineData(null, NotReadLine, "")]
    [InlineData("{ this is not json", NotReadLine, "")]
    [InlineData("""{ "cleanupPeriodDays": 0 }""", NotValidLine, "")]
    [InlineData("""{ "cleanupPeriodDays": -1 }""", NotValidLine, "-1")]
    [InlineData("""{ "cleanupPeriodDays": 1.5 }""", NotValidLine, "1.5")]
    [InlineData("""{ "cleanupPeriodDays": "30" }""", NotValidLine, "\"30\"")]
    [InlineData("""{ "cleanupPeriodDays": true }""", NotValidLine, "true")]
    [InlineData("""{ "cleanupPeriodDays": 99999999 }""", TooLargeLine, "99999999")]
    public async Task A_value_Claude_Code_would_not_use_deletes_nothing(string? json, string line, string raw)
    {
        if (json is not null)
        {
            File.WriteAllText(ClaudeSettings, json);
        }

        var run = await Prune([31, 29], expectDeletes: false);

        Assert.Equal([29, 31], run.EventAges);
        Assert.Equal(4, run.DecisionAges.Count);
        Assert.Equal([29, 31], run.RunAges);
        Assert.Contains(line, run.Lines);
        Assert.False(run.NoticeShown);
        Assert.Equal(0, run.StoreFailures);

        if (raw.Length > 0)
        {
            Assert.DoesNotContain(run.Lines, logged => logged.Contains(raw, StringComparison.Ordinal));
        }
    }

    /// <summary>A file another process holds locked through the prune: nothing is deleted, and no notice shows.</summary>
    [Fact]
    public async Task A_locked_file_deletes_nothing()
    {
        File.WriteAllText(ClaudeSettings, """{ "cleanupPeriodDays": 10 }""");

        PruneRun run;

        using (new FileStream(ClaudeSettings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            run = await Prune([31, 29], expectDeletes: false);
        }

        Assert.Equal([29, 31], run.EventAges);
        Assert.Contains(NotReadLine, run.Lines);
        Assert.False(run.NoticeShown);
    }

    /// <summary>
    /// <strong>The operator's own value, 99999</strong>: valid, so the line says "keeps 99999 days", and
    /// nothing in the test data is old enough to go.
    /// </summary>
    [Fact]
    public async Task The_operators_value_keeps_everything_in_the_data()
    {
        File.WriteAllText(ClaudeSettings, """{ "cleanupPeriodDays": 99999 }""");

        var run = await Prune([3650, 31, 29], expectDeletes: false);

        Assert.Equal([29, 31, 3650], run.EventAges);
        Assert.Contains("History follows Claude Code's cleanupPeriodDays: keeps 99999 days.", run.Lines);
    }

    /// <summary>
    /// <strong>The value changes from 30 to 10 between two prunes</strong>, under a fake clock: the second
    /// prune uses 10, with no restart, and says so. A third prune with the same value writes no rule line.
    /// Claude Code's file is never written: its bytes and its last-write time are unchanged.
    /// </summary>
    [Fact]
    public async Task A_change_takes_effect_at_the_next_prune_and_the_file_is_never_written()
    {
        File.WriteAllText(ClaudeSettings, """{ "cleanupPeriodDays": 30 }""");

        var log = new RecordingLogSink();
        var logger = Logger(log);
        var clock = new FakeClock(Now);

        using var store = new SqliteEventStore(Db(), logger, clock);
        Fill(store, [31, 20, 5]);

        var archive = new EventArchive(logger);
        using var started = new CancellationTokenSource();
        using var writer = new EventArchiveWriter(archive, store, logger, new RunStart("1.0.0", 5000, _folder), clock, new ClaudeCleanupPeriod(Claude), started.Token);

        await writer.StartAsync(CancellationToken.None);
        started.Cancel();

        Assert.True(SpinWait.SpinUntil(() => Count(log, "Pruned") == 1, Generous), string.Join(Environment.NewLine, Lines(log)));
        Assert.Equal([5, 20], EventAges(Now));

        // Claude Code's settings change. Then a day passes, and a record wakes the loop.
        File.WriteAllText(ClaudeSettings, """{ "cleanupPeriodDays": 10 }""");
        File.SetLastWriteTimeUtc(ClaudeSettings, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var bytes = File.ReadAllBytes(ClaudeSettings);
        var written = File.GetLastWriteTimeUtc(ClaudeSettings);

        clock.Now = Now + EventArchiveWriter.PruneEvery;
        archive.TryArchive(new ArchiveRecord(Hook(clock.Now), []));

        Assert.True(SpinWait.SpinUntil(() => Count(log, "Pruned") == 2, Generous), string.Join(Environment.NewLine, Lines(log)));
        Assert.Contains("History follows Claude Code's cleanupPeriodDays: keeps 10 days.", Lines(log));

        // The 20-day row is 21 days old now, past 10: gone. The 5-day row, now 6, stays.
        Assert.True(SpinWait.SpinUntil(() => writer.WrittenCount == 1, Generous));
        Assert.Equal([0, 6], EventAges(clock.Now));

        // A third prune, a day later, with the same value: no rule line.
        clock.Now = Now + EventArchiveWriter.PruneEvery + EventArchiveWriter.PruneEvery;
        archive.TryArchive(new ArchiveRecord(Hook(clock.Now), []));
        Assert.True(SpinWait.SpinUntil(() => writer.WrittenCount == 2, Generous));

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, Lines(log).Count(line => line.StartsWith("History follows Claude Code's cleanupPeriodDays", StringComparison.Ordinal)));
        Assert.Equal(bytes, File.ReadAllBytes(ClaudeSettings));
        Assert.Equal(written, File.GetLastWriteTimeUtc(ClaudeSettings));
    }

    // ---- The setting T1.68 retired, in a real start ---------------------------------------------

    /// <summary>
    /// <strong><c>history.retentionDays: 0</c> in the dashboard's settings is ignored:</strong> with no key in
    /// Claude Code's file, a real start deletes the 31-day rows and keeps the 29-day ones. One line says the
    /// key is no longer used, and the key is still in the file after a save.
    /// </summary>
    [Fact]
    public async Task A_dashboard_set_to_keep_everything_follows_Claude_Code_now()
    {
        var paths = new DashboardPaths(Path.Combine(_folder, "home"));
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(ClaudeSettings, "{}");

        var port = ClaudeDashboard.Tests.Hosting.AppHostTests.FreePort();
        File.WriteAllText(paths.SettingsFile, $$"""{ "port": {{port}}, "history": { "retentionDays": 0 } }""");

        var now = DateTimeOffset.UtcNow;

        using (var store = new SqliteEventStore(paths.DatabaseFile, Serilog.Core.Logger.None))
        {
            foreach (var days in new[] { 31, 29 })
            {
                var at = now - TimeSpan.FromDays(days);
                Assert.True(store.Append(new ArchiveRecord(Hook(at, $$"""{"age":{{days}}}"""), [new Decision(at, "session-1", DecisionKind.StateMoved)])));
            }
        }

        await using (var app = AppHost.Build(paths, claude: Claude))
        {
            await app.StartAsync();

            Assert.True(SpinWait.SpinUntil(() => AgedRows(paths) == 1, Generous));

            await app.StopAsync();
            (app.Services.GetService(typeof(Serilog.ILogger)) as IDisposable)?.Dispose();
        }

        Assert.Equal(["""{"age":29}"""], ForeignSqliteReader.Column(paths.DatabaseFile, "SELECT payload_json FROM events WHERE payload_json LIKE '{\"age\"%'"));

        var lines = File.ReadAllLines(Directory.EnumerateFiles(paths.LogFolder, "*.log").Single());
        Assert.Single(lines, line => line.Contains("history.retentionDays", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(DefaultLine, StringComparison.Ordinal));

        var settings = new SettingsStore(paths);
        Assert.True(settings.Save(settings.Load().Settings with { Window = new WindowSettings { Left = 10, Top = 20 } }));

        using var saved = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
        Assert.Equal(0, saved.RootElement.GetProperty("history").GetProperty("retentionDays").GetInt32());
    }

    // ---- The harness ---------------------------------------------------------------------------

    /// <summary>The test's aged events in a running host's file, or -1 while the file is busy.</summary>
    private static int AgedRows(DashboardPaths paths)
    {
        try
        {
            return ForeignSqliteReader.Column(paths.DatabaseFile, "SELECT payload_json FROM events WHERE payload_json LIKE '{\"age\"%'").Count;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return -1;
        }
    }

    /// <summary>What one prune left, and what it logged.</summary>
    private sealed record PruneRun(
        List<int> EventAges,
        List<int> DecisionAges,
        List<int> RunAges,
        List<string> Lines,
        bool NoticeShown,
        long StoreFailures);

    /// <summary>
    /// Rows at each age, then the writer's first prune, with the real reader over the scratch Claude
    /// Code folder. Waits for the prune line when rows go, or for the rule line when nothing goes.
    /// </summary>
    private async Task<PruneRun> Prune(int[] ages, bool expectDeletes)
    {
        var log = new RecordingLogSink();
        var logger = Logger(log);
        var clock = new FakeClock(Now);

        using var store = new SqliteEventStore(Db(), logger, clock);
        Fill(store, ages);

        var archive = new EventArchive(logger);
        using var started = new CancellationTokenSource();

        using (var writer = new EventArchiveWriter(archive, store, logger, new RunStart("1.0.0", 5000, _folder), clock, new ClaudeCleanupPeriod(Claude), started.Token))
        {
            await writer.StartAsync(CancellationToken.None);
            started.Cancel();

            Func<bool> done = expectDeletes
                ? () => Count(log, "Pruned") > 0
                : () => Lines(log).Any(line => line.Contains("history is kept in full", StringComparison.Ordinal) || line.StartsWith("History follows", StringComparison.Ordinal));

            Assert.True(SpinWait.SpinUntil(done, Generous), string.Join(Environment.NewLine, Lines(log)));

            await writer.StopAsync(CancellationToken.None);
        }

        var notice = new HistoryNotice(() => store.Available == false);
        notice.Tick(clock.Now);

        return new PruneRun(
            EventAges(Now),
            [.. ForeignSqliteReader.Column(Db(), "SELECT ts FROM decisions").Select(ts => AgeOf(ts, Now)).Order()],
            [.. ForeignSqliteReader.Column(Db(), "SELECT started_at FROM runs").Select(ts => AgeOf(ts, Now)).Where(age => age > 0).Order()],
            Lines(log),
            notice.IsShown,
            store.FailedCount);
    }

    /// <summary>At each age: an event with its decision, a decision with no event, and a run.</summary>
    private void Fill(SqliteEventStore store, int[] ages)
    {
        foreach (var days in ages)
        {
            var at = Now - TimeSpan.FromDays(days);

            Assert.True(store.Append(new ArchiveRecord(Hook(at, $$"""{"age":{{days}}}"""), [new Decision(at, "session-1", DecisionKind.StateMoved)])));
            Assert.True(store.Append(new ArchiveRecord(null, [new Decision(at, "session-1", DecisionKind.SilenceSwept)])));
            Assert.NotNull(store.StartRun(new RunStart("0.9.0", 5000, _folder), at));
        }
    }

    private List<int> EventAges(DateTimeOffset now) =>
        [.. ForeignSqliteReader.Column(Db(), "SELECT ts FROM events").Select(ts => AgeOf(ts, now)).Order()];

    private static int AgeOf(string ts, DateTimeOffset now) =>
        (int)Math.Round((now - DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).TotalDays);

    private static UserPromptSubmit Hook(DateTimeOffset at, string marker = "{}") =>
        TestEvents.Hook(marker) with { Timestamp = at };

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static List<string> Lines(RecordingLogSink log) =>
        [.. log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture))];

    private static int Count(RecordingLogSink log, string start) =>
        Lines(log).Count(line => line.StartsWith(start, StringComparison.Ordinal));
}
