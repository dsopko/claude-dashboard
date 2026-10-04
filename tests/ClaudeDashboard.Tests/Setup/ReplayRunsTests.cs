using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Storage;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// <c>--replay</c> forgets every session at each run's start, as the live dashboard does (T1.60,
/// issue #78).
/// </summary>
/// <remarks>
/// Each database is written through the real store in a scratch folder: hook bodies through the
/// real mapper, at times the test chose, and runs rows through the store's own start call.
/// </remarks>
public sealed class ReplayRunsTests : IDisposable
{
    private const string Cwd = @"C:\\projects\\dashboard";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public ReplayRunsTests() => Directory.CreateDirectory(_folder);

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

    private static string Prompt(string session) =>
        $$"""{"hook_event_name":"UserPromptSubmit","session_id":"{{session}}","cwd":"{{Cwd}}","prompt_id":"p-1","prompt":"go"}""";

    private static string Permission(string session) =>
        $$"""{"hook_event_name":"Notification","session_id":"{{session}}","cwd":"{{Cwd}}","notification_type":"permission_prompt"}""";

    /// <summary>Writes the events at their times, and a runs row at each start, into a new file.</summary>
    private string Database(string name, (DateTimeOffset At, string Body)[] events, DateTimeOffset[] runStarts)
    {
        var path = Path.Combine(_folder, name);
        var clock = new FakeClock();
        var mapper = new HookEventMapper(clock);

        using var store = new SqliteEventStore(path, Logger.None);

        foreach (var start in runStarts)
        {
            Assert.NotNull(store.StartRun(new RunStart("test", 5000, _folder), start));
        }

        foreach (var (at, body) in events)
        {
            clock.Now = at;

            var mapping = mapper.Map(JsonSerializer.Deserialize<HookPayload>(body)!, new PayloadJson(body));

            Assert.True(mapping.Mapped);
            Assert.True(store.Append(mapping.Event!));
        }

        return path;
    }

    private static List<string> Replay(string path)
    {
        var reported = new List<string>();

        Assert.Equal(0, ReplaySwitch.Run(path, reported.Add, Logger.None));

        return reported;
    }

    /// <summary>The times of the nudges replay wrote for one session, as instants.</summary>
    private static List<DateTimeOffset> Nudges(string path, string session) =>
        [.. ForeignSqliteReader
            .Column(path, $"SELECT ts FROM decisions WHERE kind = 'NudgePlayed' AND session_id = '{session}' ORDER BY id")
            .Select(ts => DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))];

    /// <summary>
    /// A session waiting on a permission in the first run is forgotten at the second run's start:
    /// its nudges stop there. The same history without runs rows nudges on, as before T1.60.
    /// </summary>
    [Fact]
    public void A_quiet_session_stops_nudging_at_the_next_run_start()
    {
        var first = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var second = first + TimeSpan.FromHours(1);

        (DateTimeOffset, string)[] events =
        [
            (first + TimeSpan.FromMinutes(1), Prompt("s-quiet")),
            (first + TimeSpan.FromMinutes(2), Permission("s-quiet")),

            // The second run's own session, late enough that replay ticks well past the start.
            (second + TimeSpan.FromHours(1), Prompt("s-next")),
        ];

        var withRuns = Database("with-runs.db", events, [first, second]);
        var withoutRuns = Database("without-runs.db", events, []);

        var reported = Replay(withRuns);
        Replay(withoutRuns);

        var forgotten = Nudges(withRuns, "s-quiet");
        var remembered = Nudges(withoutRuns, "s-quiet");

        Assert.NotEmpty(forgotten);
        Assert.All(forgotten, at => Assert.True(at < second, $"A nudge at {at:o} came after the second run started."));
        Assert.Contains(remembered, at => at >= second);

        Assert.Contains(reported, line => line.Contains("Replay saw 2 runs", StringComparison.Ordinal));
        Assert.Contains(reported, line => line.Contains("No history came before the first run.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A start in UTC splits events written in local text with two offsets at the right event,
    /// because the times are compared as instants, not as text.
    /// </summary>
    /// <remarks>
    /// The clocks change on 2026-10-25: before is <c>+02:00</c>, after is <c>+01:00</c>. The first
    /// event is <c>03:20+02:00</c> (01:20 UTC), before the start at 01:30 UTC; the second is
    /// <c>02:40+01:00</c> (01:40 UTC), after it. As text, both sort after <c>01:30Z</c>, so a text
    /// comparison puts the start before the first event, and the second event finds the session it
    /// should have forgotten. Compared as instants, the second event meets an empty Registry and
    /// adds the session again.
    /// </remarks>
    [Fact]
    public void A_start_splits_at_the_right_event_across_an_offset_change()
    {
        var start = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        var before = new DateTimeOffset(2026, 10, 25, 3, 20, 0, TimeSpan.FromHours(2));
        var after = new DateTimeOffset(2026, 10, 25, 2, 40, 0, TimeSpan.FromHours(1));

        var path = Database("offsets.db", [(before, Prompt("s-1")), (after, Prompt("s-1"))], [start]);

        Assert.EndsWith("+02:00", ForeignSqliteReader.Column(path, "SELECT ts FROM events WHERE id = 1")[0], StringComparison.Ordinal);
        Assert.EndsWith("+01:00", ForeignSqliteReader.Column(path, "SELECT ts FROM events WHERE id = 2")[0], StringComparison.Ordinal);

        var reported = Replay(path);

        var added = ForeignSqliteReader.Column(
            path, "SELECT event_id FROM decisions WHERE kind = 'SessionAdded' ORDER BY id");

        Assert.Equal(["1", "2"], added);
        Assert.Contains(reported, line => line.Contains("Replay saw 1 runs", StringComparison.Ordinal));
        Assert.Contains(reported, line => line.Contains("1 events came before the first run", StringComparison.Ordinal));
    }

    /// <summary>Replay reads runs and never writes it.</summary>
    [Fact]
    public void Replay_never_writes_runs()
    {
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var path = Database("untouched.db", [(start + TimeSpan.FromMinutes(1), Prompt("s-1"))], [start]);

        const string Runs = "SELECT id, started_at, IFNULL(stopped_at, 'NULL'), version, port, data_root FROM runs ORDER BY id";
        var runs = ForeignSqliteReader.Query(path, Runs);

        Replay(path);

        Assert.Equal(runs, ForeignSqliteReader.Query(path, Runs));
    }

    /// <summary>A database with no runs rows replays as one uninterrupted run and says so.</summary>
    [Fact]
    public void A_history_with_no_runs_says_it_replayed_as_one_run()
    {
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var path = Database("no-runs.db", [(start, Prompt("s-1"))], []);

        var reported = Replay(path);

        Assert.Contains(reported, line => line.Contains("no runs rows", StringComparison.Ordinal)
            && line.Contains("uninterrupted", StringComparison.Ordinal));
    }
}
