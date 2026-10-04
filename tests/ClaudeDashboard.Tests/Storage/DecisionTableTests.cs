using System.Globalization;
using System.IO;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using Microsoft.Data.Sqlite;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The decisions table: one transaction with the event, the <c>event_id</c> join, and the
/// failure that must leave neither row (T1.37, issue #48).
/// </summary>
/// <remarks>
/// Checked by the foreign reader, like every other assertion about the file: what matters is
/// what a different program finds on disk, not what the store believes it wrote.
/// </remarks>
public sealed class DecisionTableTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public DecisionTableTests() => Directory.CreateDirectory(_folder);

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

    private static Decision Row(DecisionKind kind, string? sessionId = "s-1") => new(
        TestEvents.At,
        sessionId,
        kind,
        FromState: "Working",
        ToState: "Unread",
        Reason: "Applied",
        Detail: "silentMinutes=11");

    /// <summary>The event and its decisions land together, joined by the event's own id.</summary>
    [Fact]
    public void An_event_and_its_decisions_are_one_row_each_and_joined()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Logger.None))
        {
            Assert.True(store.Append(new ArchiveRecord(
                TestEvents.Hook("""{"raw":1}"""),
                [Row(DecisionKind.SessionAdded), Row(DecisionKind.StateMoved)])));
        }

        var eventId = Assert.Single(ForeignSqliteReader.Column(path, "SELECT id FROM events"));

        var rows = ForeignSqliteReader.Query(
            path,
            "SELECT event_id, ts, session_id, kind, from_state, to_state, reason, detail FROM decisions ORDER BY id");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(eventId, row[0]));
        // UTC in the one form since T1.62 (issue #80).
        Assert.Equal(TestEvents.At.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), rows[0][1]);
        Assert.Equal("s-1", rows[0][2]);
        Assert.Equal(nameof(DecisionKind.SessionAdded), rows[0][3]);
        Assert.Equal(nameof(DecisionKind.StateMoved), rows[1][3]);
        Assert.Equal(["Working", "Unread", "Applied", "silentMinutes=11"], rows[0][4..]);
    }

    /// <summary>A tick's decisions have no causing event, and say so with NULL.</summary>
    [Fact]
    public void A_decisions_only_record_writes_no_event_and_a_null_event_id()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Logger.None))
        {
            Assert.True(store.Append(new ArchiveRecord(null, [Row(DecisionKind.SilenceSwept)])));
        }

        Assert.Empty(ForeignSqliteReader.Column(path, "SELECT id FROM events"));

        var row = Assert.Single(ForeignSqliteReader.Query(
            path, "SELECT IFNULL(event_id, 'NULL'), kind FROM decisions"));

        Assert.Equal("NULL", row[0]);
        Assert.Equal(nameof(DecisionKind.SilenceSwept), row[1]);
    }

    /// <summary>
    /// <strong>A failure between the two inserts leaves neither row.</strong>
    /// </summary>
    /// <remarks>
    /// The plant is schema drift: a <c>decisions</c> table already in the file with the wrong
    /// columns, which <c>CREATE TABLE IF NOT EXISTS</c> keeps by design. The events insert
    /// succeeds, the decisions insert throws, and the transaction must take the event back out —
    /// an events row whose decisions vanished would be indistinguishable from a tick that
    /// decided nothing, which is the corruption the one-transaction rule exists to prevent.
    /// </remarks>
    [Fact]
    public void A_failure_between_the_two_inserts_leaves_neither_row()
    {
        var path = Db();

        // Unpooled, so the handle releases at Dispose without touching the process-wide pools —
        // ClearAllPools here would reach into every other test's connections.
        using (var planted = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            planted.Open();

            using var create = planted.CreateCommand();
            create.CommandText = "CREATE TABLE decisions (id INTEGER PRIMARY KEY);";
            create.ExecuteNonQuery();
        }

        using (var store = new SqliteEventStore(path, Logger.None))
        {
            Assert.False(store.Append(new ArchiveRecord(
                TestEvents.Hook("""{"raw":1}"""),
                [Row(DecisionKind.SessionAdded)])));

            // And the store has latched unavailable, as it does for any dead disk.
            Assert.False(store.Append(new ArchiveRecord(TestEvents.Hook("""{"raw":2}"""), [])));
        }

        Assert.Empty(ForeignSqliteReader.Column(path, "SELECT id FROM events"));
    }

    /// <summary>Replay's write path: decisions against an existing id, events untouched.</summary>
    [Fact]
    public void Appending_decisions_alone_targets_the_given_id_and_never_touches_events()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Logger.None))
        {
            Assert.True(store.Append(new ArchiveRecord(TestEvents.Hook("""{"raw":1}"""), [])));

            var id = long.Parse(
                Assert.Single(ForeignSqliteReader.Column(path, "SELECT id FROM events")),
                CultureInfo.InvariantCulture);

            Assert.True(store.AppendDecisions(id, [Row(DecisionKind.NoticePlayed)]));
        }

        Assert.Single(ForeignSqliteReader.Column(path, "SELECT id FROM events"));

        var row = Assert.Single(ForeignSqliteReader.Query(
            path, "SELECT event_id, kind FROM decisions"));

        Assert.Equal(
            Assert.Single(ForeignSqliteReader.Column(path, "SELECT id FROM events")),
            row[0]);
        Assert.Equal(nameof(DecisionKind.NoticePlayed), row[1]);
    }
}
