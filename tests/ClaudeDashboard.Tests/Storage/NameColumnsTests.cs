using System.Globalization;
using System.IO;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The session's name and path columns (T1.69, issue #98): on a new file, on an old one at its next
/// start, and safe to add again; and the store writes them where they belong.
/// </summary>
/// <remarks>Scratch folders only. Every read goes through <see cref="ForeignSqliteReader"/>.</remarks>
public sealed class NameColumnsTests : IDisposable
{
    private const string Title = "Payments API";
    private const string Folder = @"C:\dev\payments-api";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public NameColumnsTests() => Directory.CreateDirectory(_folder);

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

    /// <summary>A new file has the three columns, and is at version 2.</summary>
    [Fact]
    public void A_new_file_has_the_three_columns()
    {
        using (var store = new SqliteEventStore(Db(), Serilog.Core.Logger.None))
        {
            store.CountDecisions();
        }

        Assert.Contains("session_title", Columns("events"));
        Assert.Equal(["session_title", "cwd"], Columns("decisions").TakeLast(2));
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion());
    }

    /// <summary>
    /// <strong>An old file at version 1, with rows, gains the columns at its next start</strong>: every row
    /// is kept, with NULL in the new columns, and the file is at version 2. A second start changes
    /// nothing and says nothing.
    /// </summary>
    [Fact]
    public void An_old_file_gains_the_columns_once_and_keeps_its_rows()
    {
        OldFile(version: 1);

        var events = ForeignSqliteReader.Query(Db(), "SELECT id, session_id, ts, event_type, payload_json, cwd FROM events ORDER BY id");
        var decisions = ForeignSqliteReader.Query(Db(), "SELECT id, IFNULL(event_id, 'NULL'), ts, session_id, kind, IFNULL(reason, 'NULL') FROM decisions ORDER BY id");

        var log = new RecordingLogSink();

        using (var store = new SqliteEventStore(Db(), Logger(log)))
        {
            store.CountDecisions();
        }

        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion());
        Assert.Equal(events, ForeignSqliteReader.Query(Db(), "SELECT id, session_id, ts, event_type, payload_json, cwd FROM events ORDER BY id"));
        Assert.Equal(decisions, ForeignSqliteReader.Query(Db(), "SELECT id, IFNULL(event_id, 'NULL'), ts, session_id, kind, IFNULL(reason, 'NULL') FROM decisions ORDER BY id"));
        Assert.All(ForeignSqliteReader.Column(Db(), "SELECT IFNULL(session_title, 'NULL') FROM events"), value => Assert.Equal("NULL", value));
        Assert.All(ForeignSqliteReader.Column(Db(), "SELECT IFNULL(session_title, 'NULL') || '|' || IFNULL(cwd, 'NULL') FROM decisions"), value => Assert.Equal("NULL|NULL", value));
        Assert.Single(Lines(log), line => line.Contains("Added the session's name and path columns", StringComparison.Ordinal));

        // The second start: the same columns, the same version, the same rows, and no line.
        var columnsBefore = Columns("decisions");
        var second = new RecordingLogSink();

        using (var store = new SqliteEventStore(Db(), Logger(second)))
        {
            store.CountDecisions();
        }

        Assert.Equal(columnsBefore, Columns("decisions"));
        Assert.Single(Columns("events"), column => column == "session_title");
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion());
        Assert.Equal(events, ForeignSqliteReader.Query(Db(), "SELECT id, session_id, ts, event_type, payload_json, cwd FROM events ORDER BY id"));
        Assert.DoesNotContain(Lines(second), line => line.Contains("Added the session's name", StringComparison.Ordinal));
        Assert.Equal(0, ErrorsOrWarnings(second));
    }

    /// <summary>
    /// The version is not trusted alone: a file that says 2 and lacks a column still gains it.
    /// </summary>
    [Fact]
    public void A_file_whose_version_says_two_still_gains_a_missing_column()
    {
        OldFile(version: 2);

        using (var store = new SqliteEventStore(Db(), Serilog.Core.Logger.None))
        {
            store.CountDecisions();
        }

        Assert.Contains("session_title", Columns("events"));
        Assert.Equal(["session_title", "cwd"], Columns("decisions").TakeLast(2));
        Assert.Equal(SqliteEventStore.NameColumnsVersion, UserVersion());
    }

    /// <summary>
    /// The store writes the event's name to <c>events.session_title</c>, and a decision's name and path
    /// to its own columns; null stores NULL. The name is in no other column.
    /// </summary>
    [Fact]
    public void The_store_writes_the_name_and_the_path_in_their_own_columns()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        using (var store = new SqliteEventStore(Db(), Serilog.Core.Logger.None))
        {
            var named = new Decision(at, "s-1", DecisionKind.StateMoved, "Working", "Unread", "Applied", "silentMinutes=0")
            {
                SessionTitle = Title,
                Cwd = Folder,
            };

            Assert.True(store.Append(new ArchiveRecord(TestEvents.Hook("{}") with { Timestamp = at }, [named]) { EventSessionTitle = Title }));
            Assert.True(store.Append(new ArchiveRecord(TestEvents.Hook("{}") with { Timestamp = at }, [new Decision(at, null, DecisionKind.HourlySummary)])));
        }

        Assert.Equal([Title, "NULL"], ForeignSqliteReader.Column(Db(), "SELECT IFNULL(session_title, 'NULL') FROM events ORDER BY id"));
        Assert.Equal(
            [$"{Title}|{Folder}", "NULL|NULL"],
            ForeignSqliteReader.Column(Db(), "SELECT IFNULL(session_title, 'NULL') || '|' || IFNULL(cwd, 'NULL') FROM decisions ORDER BY id"));

        // Every other column of every decision row: no name.
        foreach (var row in ForeignSqliteReader.Query(Db(), "SELECT IFNULL(event_id, ''), ts, IFNULL(session_id, ''), kind, IFNULL(from_state, ''), IFNULL(to_state, ''), IFNULL(reason, ''), IFNULL(detail, ''), IFNULL(cwd, '') FROM decisions"))
        {
            Assert.All(row, value => Assert.DoesNotContain(Title, value, StringComparison.Ordinal));
        }
    }

    /// <summary>A file as a build before T1.69 left it: the old columns, rows, and the given version.</summary>
    private void OldFile(long version)
    {
        using var connection = new SqliteConnection($"Data Source={Db()};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE events (id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, ts TEXT NOT NULL,
                event_type TEXT NOT NULL, payload_json TEXT NOT NULL, cwd TEXT NOT NULL);
            CREATE TABLE decisions (id INTEGER PRIMARY KEY, event_id INTEGER, ts TEXT NOT NULL, session_id TEXT,
                kind TEXT NOT NULL, from_state TEXT, to_state TEXT, reason TEXT, detail TEXT);
            CREATE TABLE runs (id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, stopped_at TEXT,
                version TEXT NOT NULL, port INTEGER, data_root TEXT NOT NULL);
            INSERT INTO events (session_id, ts, event_type, payload_json, cwd)
                VALUES ('s-1', '2026-10-01T09:00:00.0000000Z', 'UserPromptSubmit', '{"prompt":"old"}', 'C:\dev\old');
            INSERT INTO events (session_id, ts, event_type, payload_json, cwd)
                VALUES ('s-2', '2026-10-01T09:01:00.0000000Z', 'Stop', '{}', 'C:\dev\old');
            INSERT INTO decisions (event_id, ts, session_id, kind, reason)
                VALUES (1, '2026-10-01T09:00:00.0000000Z', 's-1', 'SessionAdded', NULL);
            INSERT INTO decisions (event_id, ts, session_id, kind, reason)
                VALUES (NULL, '2026-10-01T09:05:00.0000000Z', NULL, 'HourlySummary', 'Partial');
            PRAGMA user_version = {{version}};
            """;
        command.ExecuteNonQuery();
    }

    private List<string> Columns(string table) =>
        ForeignSqliteReader.Column(Db(), $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");

    private long UserVersion() =>
        long.Parse(ForeignSqliteReader.Column(Db(), "PRAGMA user_version")[0], CultureInfo.InvariantCulture);

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static List<string> Lines(RecordingLogSink log) =>
        [.. log.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture))];

    private static int ErrorsOrWarnings(RecordingLogSink log) =>
        log.Events.Count(e => e.Level >= Serilog.Events.LogEventLevel.Warning);
}
