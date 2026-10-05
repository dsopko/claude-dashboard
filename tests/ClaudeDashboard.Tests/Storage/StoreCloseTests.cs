using System.IO;
using Microsoft.Data.Sqlite;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The history store closed while it writes (T1.59, issue #84).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each order is held open by a seam, not found by a timing loop.</strong> The store runs
/// <c>InsideTransaction</c> between <c>BeginTransaction</c> and <c>Commit</c>, on the writer's
/// thread. These tests block there, so the write is inside its transaction for as long as the test
/// says, and the close happens at a point the test chose.
/// </para>
/// <para>
/// <strong>The one bounded wait is on the failing side only.</strong> The first test gives the
/// close 250 ms to return while the write is held. With the guard it cannot return at all, so the
/// bound never decides a pass. Without the guard it returns in microseconds, and the test sees it.
/// </para>
/// </remarks>
public sealed class StoreCloseTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public StoreCloseTests() => Directory.CreateDirectory(_folder);

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

    private static Decision Row() => new(
        TestEvents.At,
        "session-1",
        DecisionKind.StateMoved,
        FromState: "Working",
        ToState: "Unread",
        Reason: "Applied",
        Detail: null);

    // ---- (a) A close during a write waits for it ----------------------------------------------

    /// <summary>
    /// A close that arrives while an event is inside its transaction waits, and the event is in the
    /// file afterwards. #84's order.
    /// </summary>
    [Fact]
    public async Task A_close_during_an_event_write_waits_for_the_commit()
    {
        var path = Db();

        await CloseWhileHeld(
            path,
            store => store.Append(new ArchiveRecord(TestEvents.Hook("""{"held":1}"""), [Row()])));

        Assert.Equal(["""{"held":1}"""], ForeignSqliteReader.Column(path, "SELECT payload_json FROM events"));
        Assert.Single(ForeignSqliteReader.Column(path, "SELECT id FROM decisions"));
    }

    /// <summary>The replay's write is held the same way, and is waited for the same way.</summary>
    [Fact]
    public async Task A_close_during_a_decisions_write_waits_for_the_commit()
    {
        var path = Db();

        await CloseWhileHeld(path, store => store.AppendDecisions(null, [Row(), Row()]));

        Assert.Equal(2, ForeignSqliteReader.Column(path, "SELECT id FROM decisions").Count);
    }

    /// <summary>
    /// Holds <paramref name="write"/> inside its transaction, closes the store from a second
    /// thread, then lets the write go. The write must succeed and the close must have waited.
    /// </summary>
    private static async Task CloseWhileHeld(string path, Func<SqliteEventStore, bool> write)
    {
        var log = new RecordingLogSink();
        var store = new SqliteEventStore(path, Logger(log));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        store.InsideTransaction = () =>
        {
            entered.TrySetResult();
            release.Wait(Generous);
        };

        var writing = Task.Run(() => write(store));
        await entered.Task.WaitAsync(Generous);

        var closing = Task.Run(store.Dispose);
        var closedWhileHeld = await Task.WhenAny(closing, Task.Delay(TimeSpan.FromMilliseconds(250))) == closing;

        release.Set();

        // The write first: without the guard it is the one that fails, with #84's error or a false.
        Assert.True(await writing.WaitAsync(Generous), "The held write did not commit.");
        await closing.WaitAsync(Generous);

        Assert.False(closedWhileHeld, "The close returned while a write was inside its transaction.");
        Assert.True(store.Available);
        Assert.DoesNotContain(log.Events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);

        // And the close released the file once the write was done.
        File.Copy(path, path + ".copy");
        File.Delete(path + ".copy");
    }

    // ---- (b) A write after the close is dropped -----------------------------------------------

    /// <summary>
    /// After the close, each write returns false, writes no log line, leaves Available as it was
    /// and never opens the file again. The opposite order to #84's.
    /// </summary>
    /// <remarks>
    /// The file is deleted after the close and written to again: a write that opened the file
    /// would create it, so its absence at the end is the proof. The delete itself succeeding at
    /// once is T1.17's measure that the close released the handle.
    /// </remarks>
    [Fact]
    public void A_write_after_the_close_returns_false_silently_and_opens_nothing()
    {
        var path = Db();
        var log = new RecordingLogSink();
        var store = new SqliteEventStore(path, Logger(log));

        Assert.True(store.Append(TestEvents.Hook("""{"before":1}""")));
        store.Dispose();

        File.Delete(path);
        var linesBefore = log.Events.Count;

        Assert.False(store.Append(TestEvents.Hook("""{"after":1}""")));
        Assert.False(store.Append(new ArchiveRecord(null, [Row()])));
        Assert.False(store.AppendDecisions(1, [Row()]));

        Assert.False(File.Exists(path));
        Assert.Equal(linesBefore, log.Events.Count);
        Assert.True(store.Available);
        Assert.Equal(0, store.LostCount);
        Assert.Equal(0, store.FailedCount);
    }

    /// <summary>A store closed before its first write stays at "not known yet", not "down".</summary>
    [Fact]
    public void A_store_closed_before_any_write_still_reports_unknown()
    {
        var path = Db();
        var log = new RecordingLogSink();
        var store = new SqliteEventStore(path, Logger(log));

        store.Dispose();

        Assert.False(store.Append(TestEvents.Hook("""{"after":1}""")));
        Assert.Null(store.Available);
        Assert.Empty(log.Events);
        Assert.False(File.Exists(path));
    }

    /// <summary>The reads refuse after the close too, rather than open the file again.</summary>
    [Fact]
    public void A_read_after_the_close_refuses_and_opens_nothing()
    {
        var path = Db();
        var store = new SqliteEventStore(path, Serilog.Core.Logger.None);

        Assert.True(store.Append(TestEvents.Hook("""{"before":1}""")));
        store.Dispose();
        File.Delete(path);

        Assert.Throws<ObjectDisposedException>(() => store.CountDecisions());
        Assert.Throws<ObjectDisposedException>(() => store.ReadEvents());
        Assert.False(File.Exists(path));
    }

    // ---- (c) A close reaches only this store's own connections (T1.78, issue #40) -------------

    /// <summary>
    /// A store's close leaves every other file's connections alone: a pooled connection to another file still
    /// holds that file after the close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why it matters.</strong> Until T1.78 the close called <c>SqliteConnection.ClearAllPools()</c>, which
    /// clears every pool in the process. <c>Microsoft.Data.Sqlite</c> 10.0.0 then disposes each connection it
    /// takes to be "leaked": in use, with no owner. A connection that another thread is opening is in use, with
    /// no owner yet, for a moment (<c>Activate</c> sets the first before the second, with no lock against the
    /// clear). A store opening its file at that moment lost its new connection
    /// (<c>ObjectDisposedException</c> on <c>SQLitePCL.sqlite3</c>) and made no tables, or lost a row. In the test
    /// process, where many stores open and close at once, that was issue #40 and its family of random failures.
    /// </para>
    /// <para>
    /// <strong>What this test can see.</strong> The race lasts two field writes and cannot be held open by a seam.
    /// What the test can see is the reach: a clear of every pool also closes an idle pooled connection to another
    /// file, so that file can be deleted. After a close that reaches only its own pool, the file is still held.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_close_leaves_the_connections_to_other_files_open()
    {
        var other = Path.Combine(_folder, "other.db");

        // Opened and closed with pooling on: the pool keeps its handle, so the file stays held.
        var pooled = new SqliteConnection($"Data Source={other}");

        try
        {
            pooled.Open();
            pooled.Close();

            using (var store = new SqliteEventStore(Db(), Serilog.Core.Logger.None))
            {
                Assert.True(store.Append(TestEvents.Hook("""{"before":1}""")));
            }

            // The store's own file is free again (T1.17), and the other file is still held by its pool.
            File.Delete(Db());
            Assert.Throws<IOException>(() => File.Delete(other));
        }
        finally
        {
            // Its own pool only, as the store does now.
            SqliteConnection.ClearPool(pooled);
            pooled.Dispose();
        }
    }
}
