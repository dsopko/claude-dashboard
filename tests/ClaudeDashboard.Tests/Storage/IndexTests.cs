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
/// <strong>The plans are read through the product's own SQLite</strong> (Microsoft.Data.Sqlite's
/// <c>e_sqlite3</c>), because the readers they protect run in the product, and Windows' own SQLite
/// is updated separately (T1.63 review). The foreign reader is kept for "another SQLite reads the
/// file": the index names in <c>sqlite_master</c>. With no <c>ANALYZE</c> statistics, SQLite plans
/// from the indexes, not from the row counts, so the plan here is the plan a large file gets. The
/// file still holds a few thousand rows.
/// </para>
/// <para>
/// <strong>The queries are read out of the documents</strong>, not copied into this file: the first
/// <c>sql</c> block after "To answer "why did that sound play?"" in Impl Part 4, and the one in event
/// flow §12. A change to either query in the documents is planned here, so a query that brings back
/// a scan fails (T1.63 review: a copy here drifted unseen).
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

    /// <summary>
    /// The plan of <paramref name="query"/>, through the product's SQLite. Its parameters are bound,
    /// because Microsoft.Data.Sqlite refuses an unbound one; their values do not change the plan.
    /// </summary>
    private static List<string> Plan(string path, string query)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query;

        foreach (var (name, value) in new (string, object)[]
        {
            ("$from", "2026-08-26T14:30:00.0000000Z"),
            ("$to", "2026-08-26T15:30:00.0000000Z"),
            ("$since", "2026-08-25T15:30:00.0000000Z"),
            ("$session", "s-1"),
        })
        {
            if (query.Contains(name, StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(name, value);
            }
        }

        var plan = new List<string>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            plan.Add(reader.GetString(3));
        }

        return plan;
    }

    /// <summary>
    /// The first <c>sql</c> block after <paramref name="anchor"/> in <paramref name="document"/>,
    /// line endings normalised.
    /// </summary>
    private static string QueryIn(string document, string anchor)
    {
        var text = File.ReadAllText(Path.Combine(ClaudeDashboard.Tests.Architecture.RepoLayout.Root.FullName, "docs", document))
            .ReplaceLineEndings("\n");

        var at = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{document} no longer has \"{anchor}\".");

        var start = text.IndexOf("```sql\n", at, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{document} has no sql block after \"{anchor}\".");
        start += "```sql\n".Length;

        var end = text.IndexOf("\n```", start, StringComparison.Ordinal);

        return text[start..end];
    }

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
        Assert.Equal(SqliteEventStore.NameColumnsVersion, long.Parse(ForeignSqliteReader.Column(path, "PRAGMA user_version")[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The two documented queries, as the documents give them today, use the indexes and scan neither
    /// table, in the product's SQLite. The inner query of Impl Part 4 uses <c>ix_decisions_kind_ts</c>.
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

        var why = Plan(path, QueryIn("claude-dashboard-impl-spec.md", "To answer \"why did that sound play?\":"));
        var session12 = Plan(path, QueryIn("claude-dashboard-event-flow.md", "## 12. How to see the path work"));

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
