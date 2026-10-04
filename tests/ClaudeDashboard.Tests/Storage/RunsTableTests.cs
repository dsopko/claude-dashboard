using System.IO;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The <c>runs</c> table: one row per start, a stop time on a clean stop (T1.60, issue #78).
/// </summary>
/// <remarks>
/// The store and the writer on their own. <see cref="RunsHostTests"/> holds the same rows to the
/// composed host. Every read of the file goes through <see cref="ForeignSqliteReader"/>.
/// </remarks>
public sealed class RunsTableTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public RunsTableTests() => Directory.CreateDirectory(_folder);

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

    private const string Rows =
        "SELECT id, started_at, IFNULL(stopped_at, 'NULL'), version, IFNULL(port, 'NULL'), data_root FROM runs ORDER BY id";

    // ---- The store ----------------------------------------------------------------------------

    /// <summary>
    /// A start and a stop are one row, both times in UTC ending in Z, whatever offset the clock had.
    /// </summary>
    [Fact]
    public void A_start_and_a_stop_are_one_row_in_utc()
    {
        var path = Db();
        var startedAt = new DateTimeOffset(2026, 10, 3, 9, 15, 0, TimeSpan.FromHours(2));

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            var id = store.StartRun(new RunStart("1.2.3+abc", 51234, _folder), startedAt);

            Assert.NotNull(id);
            Assert.True(store.StopRun(id.Value, startedAt + TimeSpan.FromHours(1)));
        }

        var row = Assert.Single(ForeignSqliteReader.Query(path, Rows));

        Assert.Equal("2026-10-03T07:15:00.0000000Z", row[1]);
        Assert.Equal("2026-10-03T08:15:00.0000000Z", row[2]);
        Assert.Equal("1.2.3+abc", row[3]);
        Assert.Equal("51234", row[4]);
        Assert.Equal(_folder, row[5]);
    }

    /// <summary>A start that could not bind has no port, and a run that never stopped has no stop.</summary>
    [Fact]
    public void A_start_with_no_port_and_no_stop_leaves_both_null()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.NotNull(store.StartRun(new RunStart("1.2.3", null, _folder), TestEvents.At));
        }

        var row = Assert.Single(ForeignSqliteReader.Query(path, Rows));

        Assert.Equal("NULL", row[2]);
        Assert.Equal("NULL", row[4]);
    }

    /// <summary>
    /// A database from before T1.60 gains the table on its next connection, and its events and
    /// decisions rows are unchanged.
    /// </summary>
    [Fact]
    public void A_database_without_the_table_gains_it_and_keeps_its_rows()
    {
        var path = Db();
        OldDatabase.Create(path);

        var events = ForeignSqliteReader.Query(path, OldDatabase.EventRows);
        var decisions = ForeignSqliteReader.Query(path, OldDatabase.DecisionRows);

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.NotNull(store.StartRun(new RunStart("1.2.3", 5000, _folder), TestEvents.At));
        }

        Assert.Single(ForeignSqliteReader.Query(path, Rows));
        Assert.Equal(events, ForeignSqliteReader.Query(path, OldDatabase.EventRows));
        Assert.Equal(decisions, ForeignSqliteReader.Query(path, OldDatabase.DecisionRows));
    }

    /// <summary>
    /// A start row the disk refuses is lost like any other record and counted, and returns no id.
    /// </summary>
    [Fact]
    public void A_start_the_disk_refuses_is_lost_and_counted()
    {
        // A directory where the file must be, as the store's own degrade test does.
        var occupied = Path.Combine(_folder, "occupied.db");
        Directory.CreateDirectory(occupied);

        using var store = new SqliteEventStore(occupied, Serilog.Core.Logger.None);

        Assert.Null(store.StartRun(new RunStart("1.2.3", 5000, _folder), TestEvents.At));
        Assert.Equal(1, store.LostCount);
        Assert.False(store.Available);
    }

    // ---- The writer ---------------------------------------------------------------------------

    /// <summary>
    /// The writer writes the start row only once the host has started, first, before the records
    /// queued while it waited, and not on the thread that started the host.
    /// </summary>
    /// <remarks>
    /// The time is the clock's at the moment the host started, taken on the starting thread. The
    /// write is on the writer's loop. The consumer never holds the writer or the store, so the row
    /// cannot be written on the consumer's thread; this shows it is not written on the starting one.
    /// </remarks>
    [Fact]
    public async Task The_start_row_waits_for_the_host_and_comes_first()
    {
        var store = new RecordingStore();
        var archive = new EventArchive(Serilog.Core.Logger.None);
        var clock = new FakeClock(TestEvents.At);
        using var started = new CancellationTokenSource();

        using var writer = new EventArchiveWriter(
            archive, store, Serilog.Core.Logger.None, new RunStart("1.2.3", 5000, _folder), clock, started.Token);

        await writer.StartAsync(CancellationToken.None);

        archive.TryArchive(new ArchiveRecord(TestEvents.Hook("""{"queued":1}"""), []));
        Assert.Empty(store.Calls);

        clock.Now = TestEvents.At + TimeSpan.FromMinutes(1);
        var startingThread = Environment.CurrentManagedThreadId;
        started.Cancel();
        clock.Now = TestEvents.At + TimeSpan.FromMinutes(2);

        await store.Started.Task.WaitAsync(Generous);

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal("StartRun", store.Calls[0]);
        Assert.Equal("Append", store.Calls[1]);
        Assert.Equal(TestEvents.At + TimeSpan.FromMinutes(1), store.StartedAt);
        Assert.NotEqual(startingThread, store.StartThread);
    }

    /// <summary>
    /// A record queued at shutdown is written before the stop time: the stop comes after the drain.
    /// </summary>
    /// <remarks>
    /// The store holds the start row until the stop has begun, so the loop never reads the record
    /// queued meanwhile: the stop's drain writes it, and the stop time must come after that.
    /// </remarks>
    [Fact]
    public async Task A_record_queued_at_shutdown_is_written_before_the_stop_time()
    {
        var store = new RecordingStore { HoldStart = true };
        var archive = new EventArchive(Serilog.Core.Logger.None);
        using var started = new CancellationTokenSource();

        using var writer = new EventArchiveWriter(
            archive,
            store,
            Serilog.Core.Logger.None,
            new RunStart("1.2.3", 5000, _folder),
            new FakeClock(TestEvents.At),
            started.Token);

        await writer.StartAsync(CancellationToken.None);
        started.Cancel();
        await store.Started.Task.WaitAsync(Generous);

        archive.TryArchive(new ArchiveRecord(TestEvents.Hook("""{"at_shutdown":1}"""), []));

        var stopping = writer.StopAsync(CancellationToken.None);
        store.Release.Set();
        await stopping.WaitAsync(Generous);

        Assert.Equal(["StartRun", "Append", "StopRun"], store.Calls);
        Assert.Equal(RecordingStore.RunId, store.StoppedRun);
    }

    /// <summary>A host that never started writes no row, and so no stop either.</summary>
    [Fact]
    public async Task A_writer_stopped_before_the_host_started_writes_no_run()
    {
        var store = new RecordingStore();
        var archive = new EventArchive(Serilog.Core.Logger.None);
        using var started = new CancellationTokenSource();

        using var writer = new EventArchiveWriter(
            archive,
            store,
            Serilog.Core.Logger.None,
            new RunStart("1.2.3", 5000, _folder),
            new FakeClock(TestEvents.At),
            started.Token);

        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        Assert.Empty(store.Calls);
    }

    /// <summary>Records each call in order. Can hold the start row until released.</summary>
    private sealed class RecordingStore : IEventStore
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];

        public bool HoldStart { get; init; }

        public ManualResetEventSlim Release { get; } = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public const long RunId = 7;

        public DateTimeOffset? StartedAt { get; private set; }

        public int StartThread { get; private set; }

        public long? StoppedRun { get; private set; }

        public List<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public bool Append(InboundEvent inboundEvent) => Append(new ArchiveRecord(inboundEvent, []));

        public bool Append(ArchiveRecord record)
        {
            Add("Append");

            return true;
        }

        public long? StartRun(RunStart run, DateTimeOffset startedAt)
        {
            Add("StartRun");
            StartedAt = startedAt;
            StartThread = Environment.CurrentManagedThreadId;
            Started.TrySetResult();

            if (HoldStart)
            {
                Release.Wait(Generous);
            }

            return RunId;
        }

        public bool StopRun(long runId, DateTimeOffset stoppedAt)
        {
            Add("StopRun");
            StoppedRun = runId;

            return true;
        }

        private void Add(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }
    }
}

/// <summary>A database as a build before T1.60 left it: events and decisions, no runs.</summary>
internal static class OldDatabase
{
    // Without ts: since T1.62 the next connection converts the old local times to UTC (UtcTimesTests holds
    // that). Everything else in a row is unchanged.
    public const string EventRows = "SELECT id, session_id, event_type, payload_json, cwd FROM events ORDER BY id";

    public const string DecisionRows =
        "SELECT id, IFNULL(event_id, 'NULL'), kind, IFNULL(reason, 'NULL') FROM decisions ORDER BY id";

    public static void Create(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
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
            INSERT INTO events (session_id, ts, event_type, payload_json, cwd)
            VALUES ('s-old', '2026-09-01T10:00:00.0000000+02:00', 'UserPromptSubmit', '{"old":1}', 'C:\work');
            INSERT INTO decisions (event_id, ts, session_id, kind, reason)
            VALUES (1, '2026-09-01T10:00:00.0000000+02:00', 's-old', 'SessionAdded', 'Applied');
            """;
        command.ExecuteNonQuery();
    }
}
