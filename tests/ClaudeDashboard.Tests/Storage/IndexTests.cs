using System.IO;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The six indexes, and the documented queries' plans (T1.63, issue #79).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plans are read by a SQLite the product does not use</strong>
/// (<see cref="ForeignSqliteReader"/>). With no <c>ANALYZE</c> statistics, SQLite plans from the
/// indexes, not from the row counts, so the plan here is the plan a large file gets. The file still
/// holds a few thousand rows.
/// </para>
/// <para>
/// <strong>"No SCAN" alone does not hold <c>ix_decisions_kind_ts</c>.</strong> Without it, the
/// planner falls back to <c>ix_decisions_ts</c> and <c>ix_decisions_session_id</c>, which are
/// SEARCHes too (measured). So the test also names the index that serves the inner query, "every
/// sound played between two times".
/// </para>
/// </remarks>
public sealed class IndexTests : IDisposable
{
    private static readonly string[] Names =
    [
        "ix_decisions_event_id",
        "ix_decisions_kind_ts",
        "ix_decisions_session_id",
        "ix_decisions_ts",
        "ix_events_session_id",
        "ix_events_ts",
    ];

    /// <summary>The query in Impl Part 4, word for word: "why did that sound play?".</summary>
    private const string WhyDidThatSoundPlay = """
        SELECT d.ts, d.session_id, d.kind, d.from_state, d.to_state, d.reason, d.detail, e.event_type
        FROM decisions d LEFT JOIN events e ON e.id = d.event_id
        WHERE d.session_id IN (SELECT session_id FROM decisions
                               WHERE kind IN ('NoticePlayed', 'NudgePlayed') AND ts BETWEEN $from AND $to)
          AND d.kind IN ('NoticePlayed', 'NudgePlayed', 'StateMoved', 'SilenceSwept')
          AND d.ts BETWEEN $since AND $to
        ORDER BY d.session_id, d.id;
        """;

    /// <summary>The query in event flow §12, word for word: one session's last events.</summary>
    private const string OneSessionsEvents = """
        SELECT e.id, datetime(e.ts, 'localtime') AS local_time, e.event_type, d.kind, d.from_state, d.to_state, d.reason
        FROM events e LEFT JOIN decisions d ON d.event_id = e.id
        WHERE e.session_id = $session
        ORDER BY e.id DESC LIMIT 40;
        """;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public IndexTests() => Directory.CreateDirectory(_folder);

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

    private static List<string> IndexesOf(string path) =>
        ForeignSqliteReader.Column(path, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_%' ORDER BY name");

    private static List<string> Plan(string path, string query) =>
        [.. ForeignSqliteReader.Query(path, "EXPLAIN QUERY PLAN " + query).Select(row => row[3])];

    /// <summary>A new file has the six indexes.</summary>
    [Fact]
    public void A_new_file_has_the_six_indexes()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.True(store.Append(TestEvents.Hook("{}")));
        }

        Assert.Equal(Names, IndexesOf(path));
    }

    /// <summary>
    /// A file made before this change gains the indexes at its next open, with its rows unchanged
    /// (the times converted by T1.62 first).
    /// </summary>
    [Fact]
    public void An_old_file_gains_the_indexes_with_its_rows_unchanged()
    {
        var path = Db();
        OldDatabase.Create(path);

        var events = ForeignSqliteReader.Query(path, OldDatabase.EventRows);
        var decisions = ForeignSqliteReader.Query(path, OldDatabase.DecisionRows);
        Assert.Empty(IndexesOf(path));

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.Equal(1, store.CountDecisions());
        }

        Assert.Equal(Names, IndexesOf(path));
        Assert.Equal(events, ForeignSqliteReader.Query(path, OldDatabase.EventRows));
        Assert.Equal(decisions, ForeignSqliteReader.Query(path, OldDatabase.DecisionRows));
        Assert.Equal(SqliteEventStore.UtcTimesVersion, long.Parse(ForeignSqliteReader.Column(path, "PRAGMA user_version")[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The two documented queries use the indexes and scan neither table. The inner query of Impl
    /// Part 4 uses <c>ix_decisions_kind_ts</c>.
    /// </summary>
    [Fact]
    public void The_documented_queries_use_the_indexes_and_scan_no_table()
    {
        var path = Db();
        var at = TestEvents.At;

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            for (var i = 0; i < 2_000; i++)
            {
                var session = $"s-{i % 20}";

                Assert.True(store.Append(new ArchiveRecord(
                    TestEvents.Hook("{}", session),
                    [
                        new Decision(at + TimeSpan.FromSeconds(i), session, DecisionKind.StateMoved, "Working", "Unread", "Applied"),
                        new Decision(at + TimeSpan.FromSeconds(i), session, i % 2 == 0 ? DecisionKind.NoticePlayed : DecisionKind.NudgePlayed),
                    ])));
            }
        }

        var why = Plan(path, WhyDidThatSoundPlay);
        var session12 = Plan(path, OneSessionsEvents);

        foreach (var line in why.Concat(session12))
        {
            Assert.False(
                line.StartsWith("SCAN ", StringComparison.Ordinal),
                $"A documented query scans a table: {line}{Environment.NewLine}{string.Join(Environment.NewLine, why.Concat(session12))}");
        }

        Assert.Contains(why, line => line.StartsWith("SEARCH decisions USING INDEX ix_decisions_kind_ts", StringComparison.Ordinal));
        Assert.Contains(session12, line => line.StartsWith("SEARCH e USING INDEX ix_events_session_id", StringComparison.Ordinal));
        Assert.Contains(session12, line => line.StartsWith("SEARCH d USING INDEX ix_decisions_event_id", StringComparison.Ordinal));
    }
}
