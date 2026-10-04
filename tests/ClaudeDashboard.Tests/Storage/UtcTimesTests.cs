using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// Every time in <c>dashboard.db</c> is UTC text in one form, and old rows are converted once
/// (T1.62, issue #80).
/// </summary>
/// <remarks>
/// Every read of the file goes through <see cref="ForeignSqliteReader"/>. The old-form files are
/// built here with SQL, as a build before T1.62 left them: times with their local offset, and
/// <c>user_version</c> 0.
/// </remarks>
public sealed partial class UtcTimesTests : IDisposable
{
    private const string Cwd = @"C:\\projects\\dashboard";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public UtcTimesTests() => Directory.CreateDirectory(_folder);

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

    private string Db(string name = "dashboard.db") => Path.Combine(_folder, name);

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    /// <summary>The one form: seven fractional digits and Z.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$")]
    private static partial Regex OneForm();

    private static List<string> ConversionLines(RecordingLogSink log) =>
        [.. log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture))
            .Where(line => line.Contains("in UTC, once", StringComparison.Ordinal))];

    /// <summary>
    /// The file's version. After a full open it is <see cref="SqliteEventStore.NameColumnsVersion"/>:
    /// the open converts the times (1) and then adds the name columns (2, T1.69).
    /// </summary>
    private static long UserVersion(string path) =>
        long.Parse(ForeignSqliteReader.Column(path, "PRAGMA user_version")[0], CultureInfo.InvariantCulture);

    private static string Prompt(string session) =>
        $$"""{"hook_event_name":"UserPromptSubmit","session_id":"{{session}}","cwd":"{{Cwd}}","prompt_id":"p-1","prompt":"go"}""";

    private static string Permission(string session) =>
        $$"""{"hook_event_name":"Notification","session_id":"{{session}}","cwd":"{{Cwd}}","notification_type":"permission_prompt"}""";

    /// <summary>Writes a file as a build before T1.62 left it: local times, user_version 0.</summary>
    private static void OldFile(string path, (string Ts, string EventType, string Body)[] events, (long? EventId, string Ts, string Kind)[] decisions)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE events (
                    id           INTEGER PRIMARY KEY,
                    session_id   TEXT    NOT NULL,
                    ts           TEXT    NOT NULL,
                    event_type   TEXT    NOT NULL,
                    payload_json TEXT    NOT NULL,
                    cwd          TEXT    NOT NULL
                );
                CREATE TABLE decisions (
                    id          INTEGER PRIMARY KEY,
                    event_id    INTEGER,
                    ts          TEXT NOT NULL,
                    session_id  TEXT,
                    kind        TEXT NOT NULL,
                    from_state  TEXT,
                    to_state    TEXT,
                    reason      TEXT,
                    detail      TEXT
                );
                """;
            schema.ExecuteNonQuery();
        }

        foreach (var (ts, eventType, body) in events)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO events (session_id, ts, event_type, payload_json, cwd) VALUES ('s-1', $ts, $type, $body, 'C:\\projects\\dashboard');";
            insert.Parameters.AddWithValue("$ts", ts);
            insert.Parameters.AddWithValue("$type", eventType);
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
        }

        foreach (var (eventId, ts, kind) in decisions)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO decisions (event_id, ts, session_id, kind) VALUES ($event, $ts, 's-1', $kind);";
            insert.Parameters.AddWithValue("$event", (object?)eventId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ts", ts);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.ExecuteNonQuery();
        }
    }

    private static readonly (string Ts, string EventType, string Body)[] OldEvents =
    [
        ("2026-10-25T02:59:59.5000000+02:00", "UserPromptSubmit", Prompt("s-1")),
        ("2026-10-25T02:00:00.5000000+01:00", "Notification", Permission("s-1")),
        ("2026-10-25T02:10:00.0000000+01:00", "UserPromptSubmit", Prompt("s-1")),
    ];

    private static readonly (long? EventId, string Ts, string Kind)[] OldDecisions =
    [
        (1, "2026-10-25T02:59:59.5000000+02:00", "SessionAdded"),
        (2, "2026-10-25T02:00:00.5000000+01:00", "StateMoved"),
        (null, "2026-10-25T02:05:00.0000000+01:00", "SilenceSwept"),
    ];

    // ---- New rows -----------------------------------------------------------------------------

    /// <summary>
    /// Two rows one second apart across a clock change come back by <c>ORDER BY ts</c> in the order
    /// they were written. In local text the second would sort first.
    /// </summary>
    [Fact]
    public void Two_rows_across_an_offset_change_come_back_in_order()
    {
        var path = Db();
        var before = new DateTimeOffset(2026, 10, 25, 2, 59, 59, 500, TimeSpan.FromHours(2));
        var after = new DateTimeOffset(2026, 10, 25, 2, 0, 0, 500, TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(1), after - before);

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.True(store.Append(Hook(before)));
            Assert.True(store.Append(Hook(after)));
        }

        Assert.Equal(["1", "2"], ForeignSqliteReader.Column(path, "SELECT id FROM events ORDER BY ts"));
    }

    /// <summary>New rows in <c>events</c>, <c>decisions</c> and <c>runs</c> all have the one form.</summary>
    [Fact]
    public void New_rows_in_all_three_tables_have_the_one_form()
    {
        var path = Db();
        var at = new DateTimeOffset(2026, 10, 2, 14, 3, 11, 123, TimeSpan.FromHours(2));

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.True(store.Append(new ArchiveRecord(Hook(at), [new Decision(at, "s-1", DecisionKind.SessionAdded)])));
            var run = store.StartRun(new RunStart("1.2.3", 5000, _folder), at);
            Assert.True(store.StopRun(run!.Value, at + TimeSpan.FromHours(1)));
        }

        string[] times =
        [
            .. ForeignSqliteReader.Column(path, "SELECT ts FROM events"),
            .. ForeignSqliteReader.Column(path, "SELECT ts FROM decisions"),
            .. ForeignSqliteReader.Column(path, "SELECT started_at FROM runs"),
            .. ForeignSqliteReader.Column(path, "SELECT stopped_at FROM runs"),
        ];

        Assert.Equal(4, times.Length);
        Assert.All(times, ts => Assert.Matches(OneForm(), ts));
        Assert.Equal("2026-10-02T12:03:11.1230000Z", times[0]);
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion(path));
    }

    // ---- The conversion -----------------------------------------------------------------------

    /// <summary>
    /// A file with old-form rows in both tables has every <c>ts</c> in the one form afterwards, at
    /// the same instant, with the same rows, ids and payloads, and <c>user_version</c> 1. One line
    /// says what was done, with counts and no time from a row.
    /// </summary>
    [Fact]
    public void An_old_file_is_converted_in_both_tables_once()
    {
        var path = Db();
        OldFile(path, OldEvents, OldDecisions);

        const string Events = "SELECT id, session_id, ts, event_type, payload_json, cwd FROM events ORDER BY id";
        const string Decisions = "SELECT id, IFNULL(event_id, 'NULL'), ts, kind FROM decisions ORDER BY id";
        var eventsBefore = ForeignSqliteReader.Query(path, Events);
        var decisionsBefore = ForeignSqliteReader.Query(path, Decisions);
        Assert.Equal(0, UserVersion(path));

        var log = new RecordingLogSink();

        using (var store = new SqliteEventStore(path, Logger(log)))
        {
            Assert.Equal(3, store.CountDecisions());
        }

        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion(path));

        var eventsAfter = ForeignSqliteReader.Query(path, Events);
        var decisionsAfter = ForeignSqliteReader.Query(path, Decisions);

        Assert.Equal(eventsBefore.Count, eventsAfter.Count);
        Assert.Equal(decisionsBefore.Count, decisionsAfter.Count);

        for (var i = 0; i < eventsBefore.Count; i++)
        {
            Assert.Equal(eventsBefore[i][0], eventsAfter[i][0]);
            Assert.Equal(eventsBefore[i][4], eventsAfter[i][4]);
            SameInstantInOneForm(eventsBefore[i][2], eventsAfter[i][2]);
        }

        for (var i = 0; i < decisionsBefore.Count; i++)
        {
            Assert.Equal(decisionsBefore[i][0], decisionsAfter[i][0]);
            Assert.Equal(decisionsBefore[i][1], decisionsAfter[i][1]);
            SameInstantInOneForm(decisionsBefore[i][2], decisionsAfter[i][2]);
        }

        var line = Assert.Single(ConversionLines(log));
        Assert.Contains("6 rows converted", line, StringComparison.Ordinal);
        Assert.Contains("0 left as they were", line, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-25", line, StringComparison.Ordinal);

        // A second open does not convert again.
        var again = new RecordingLogSink();

        using (var store = new SqliteEventStore(path, Logger(again)))
        {
            Assert.Equal(3, store.CountDecisions());
        }

        Assert.Empty(ConversionLines(again));
        Assert.Equal(eventsAfter, ForeignSqliteReader.Query(path, Events));
    }

    /// <summary>A time that will not parse is left as it is and counted; the rest are converted.</summary>
    [Fact]
    public void A_time_that_will_not_parse_is_left_and_counted()
    {
        var path = Db();
        OldFile(path, [("not a time", "UserPromptSubmit", Prompt("s-1")), OldEvents[1]], []);

        var log = new RecordingLogSink();

        using (var store = new SqliteEventStore(path, Logger(log)))
        {
            store.CountDecisions();
        }

        Assert.Equal(
            ["not a time", "2026-10-25T01:00:00.5000000Z"],
            ForeignSqliteReader.Column(path, "SELECT ts FROM events ORDER BY id"));
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion(path));

        var line = Assert.Single(ConversionLines(log));
        Assert.Contains("1 rows converted", line, StringComparison.Ordinal);
        Assert.Contains("1 left as they were", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A conversion that fails rolls back: the old rows are unchanged, <c>user_version</c> stays 0,
    /// and the history notice shows. A minute later the next write converts and records.
    /// </summary>
    [Fact]
    public void A_conversion_that_fails_rolls_back_and_tries_again_a_minute_later()
    {
        var path = Db();
        OldFile(path, OldEvents, OldDecisions);
        var before = ForeignSqliteReader.Query(path, "SELECT id, ts FROM events UNION ALL SELECT id, ts FROM decisions");

        var clock = new FakeClock();
        using var store = new SqliteEventStore(path, Serilog.Core.Logger.None, clock);
        var notice = new HistoryNotice(() => store.Available == false);

        store.InsideConversion = () => throw new SqliteException("planted: database or disk is full", 13);

        Assert.False(store.Append(Hook(clock.Now)));
        notice.Tick(clock.Now);

        Assert.True(notice.IsShown);
        Assert.Equal(0, UserVersion(path));
        Assert.Equal(before, ForeignSqliteReader.Query(path, "SELECT id, ts FROM events UNION ALL SELECT id, ts FROM decisions"));

        store.InsideConversion = null;
        clock.Now += SqliteEventStore.RetryAfter;

        Assert.True(store.Append(Hook(clock.Now)));
        notice.Tick(clock.Now);

        Assert.False(notice.IsShown);
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion(path));
        Assert.All(ForeignSqliteReader.Column(path, "SELECT ts FROM events"), ts => Assert.Matches(OneForm(), ts));
    }

    // ---- Replay -------------------------------------------------------------------------------

    /// <summary>
    /// Replay over a converted file gives the same decisions as over the same history in the old
    /// form. Replay opens its file through the store, so the old file is converted first: the
    /// instants and the rows are unchanged, and only the form of the time.
    /// </summary>
    [Fact]
    public void Replay_over_a_converted_file_matches_the_old_form()
    {
        var old = Db("old.db");
        var converted = Db("converted.db");
        OldFile(old, OldEvents, []);
        File.Copy(old, converted);

        using (var store = new SqliteEventStore(converted, Serilog.Core.Logger.None))
        {
            store.CountDecisions();
        }

        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion(converted));
        Assert.Equal(0, UserVersion(old));

        Assert.Equal(0, ReplaySwitch.Run(old, _ => { }, Serilog.Core.Logger.None));
        Assert.Equal(0, ReplaySwitch.Run(converted, _ => { }, Serilog.Core.Logger.None));

        const string Rows =
            "SELECT IFNULL(event_id, 'NULL'), ts, IFNULL(session_id, 'NULL'), kind, IFNULL(from_state, 'NULL'), " +
            "IFNULL(to_state, 'NULL'), IFNULL(reason, 'NULL'), IFNULL(detail, 'NULL') FROM decisions ORDER BY id";

        var fromOld = ForeignSqliteReader.Query(old, Rows);
        Assert.NotEmpty(fromOld);
        Assert.Equal(fromOld, ForeignSqliteReader.Query(converted, Rows));

        // The old file was converted by replay's own open.
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion(old));
    }

    private static void SameInstantInOneForm(string before, string after)
    {
        Assert.Matches(OneForm(), after);
        Assert.Equal(
            DateTimeOffset.Parse(before, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(after, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static UserPromptSubmit Hook(DateTimeOffset at) =>
        new()
        {
            SessionId = new SessionId("s-1"),
            Timestamp = at,
            Cwd = @"C:\projects\dashboard",
            Prompt = "the prompt as the domain sees it",
            Payload = new PayloadJson(Prompt("s-1")),
        };
}
