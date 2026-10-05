using System.IO;
using System.Globalization;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Microsoft.Data.Sqlite;
using Serilog;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// The append-only event table in <c>dashboard.db</c> (Impl Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where it sits, and the permissions it has.</strong>
/// <c>%LOCALAPPDATA%\ClaudeDashboard\dashboard.db</c>, beside <c>settings.json</c> and
/// <c>logs\</c>, moved by <c>CLAUDE_DASHBOARD_HOME</c> like everything else under that root.
/// <strong>No explicit ACL is set, and that is a decision rather than an omission.</strong> The
/// inherited access control on that folder was measured at T1.17 and grants SYSTEM,
/// BUILTIN\Administrators and the user, all inherited, with no <c>Users</c> and no
/// <c>Everyone</c> — it is already per-user. Writing our own DACL would be new security surface
/// in a task about a log table, it can fail on a redirected or roaming profile, and the only
/// principal it would exclude is Administrators, who can read the file regardless.
/// </para>
/// <para>
/// <strong>It keeps the retention window, and here is what that costs</strong> (T1.64, issue #81).
/// <see cref="Prune"/> deletes what is older than the days it is given, at each start and once a day.
/// Since T1.68 (issue #102) those are Claude Code's <c>cleanupPeriodDays</c>, 30 days by default, and
/// a value Claude Code would not use deletes nothing (<c>HistoryRetention</c>). The file stops growing and does not shrink:
/// no <c>VACUUM</c>, by the operator's ruling. Two figures, and they differ by a factor of nine:
/// </para>
/// <list type="bullet">
///   <item><description><strong>The synthetic day: about 310 KiB</strong> (315,392 bytes, with a
///   name and a path on each row since T1.69) — see <see cref="TypicalBytesPerDay"/>. Written through this store at payload sizes taken from
///   4,439 real prompts and 11,757 real assistant messages, with decisions at the real ratio. The
///   busiest synthetic day is about 2.8 MiB.</description></item>
///   <item><description><strong>The operator's real file: about 2.7 MB a day</strong> (2,709,104
///   bytes over the 37.67 days its events spanned, measured on a copy on 2026-10-04). It carries
///   what the synthetic day does not: tool batches, every kind of notification, larger answers.
///   <strong>So the file holds at most the retention window: about 81 MB for 30 days.</strong></description></item>
/// </list>
/// <para>
/// <c>GrowthMeasurement</c> re-measures the synthetic day on every build, and states what it does
/// and does not cover. Anyone changing what is stored should read the new number off a test run
/// rather than reasoning about it.
/// </para>
/// <para>
/// <strong>A dead disk is not a dead dashboard (TS §IV.7).</strong> If the file cannot be opened,
/// created or written, this says so once in the log and returns <see langword="false"/>. The
/// dashboard runs on, with no history, and the window says so (T1.54, issue #71).
/// </para>
/// <para>
/// <strong>It tries again each minute (the operator's ruling in #71).</strong> After a failed
/// write, the next attempt is the first write at least <see cref="RetryAfter"/> after the failure.
/// The records that arrive in between are counted as lost, not queued: a queue would hold the
/// operator's words in memory for as long as the disk stays full. No timer and no thread do this.
/// The attempt rides on the next record, on the writer's thread, so it costs one normal write at
/// most. A full disk that frees up after a minute costs a minute of history, not the rest of the
/// day. A failed retry writes no log line; the write that succeeds writes one, with the count of
/// records lost.
/// </para>
/// <para>
/// <strong>It may be closed while it writes (T1.59, issue #84).</strong> The product stops the
/// archive writer before the container disposes this store, but a host disposed without a stop
/// does not. Without a guard, <see cref="Dispose"/> could close the connection between
/// <c>BeginTransaction</c> and <c>Commit</c>, and the write threw; or a write could pass its check,
/// the close run, and the write open a connection that nothing closed. One private lock, held by
/// the writes, the reads and <see cref="Dispose"/>, closes both orders: the close waits for the
/// current write, which takes milliseconds, and a write after it returns <see langword="false"/>
/// without opening the file. The lock is on the disk path only. The Registry's "one writer, no
/// locks" rule is not touched, because the consumer never calls the store.
/// </para>
/// </remarks>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    /// <summary>
    /// The bound on the synthetic typical day — 350,000 bytes, measured, not estimated, with a margin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Measured at T1.17 through this store, at real payload sizes.</strong> A typical day
    /// wrote 294,912 bytes; this is that, rounded up. Since T1.60 the figure is the growth, less the
    /// pages an empty file already holds: 286,720 bytes, because a new table costs a page that a day
    /// does not add. Since T1.63 it includes the six indexes: 299,008 bytes, under the constant by
    /// less than 1 %. Since T1.64 the day writes decisions too, at 0.284 for each event, the ratio of
    /// the operator's real file: 307,200 bytes. Since T1.69 each row also carries the session's
    /// name, and each decision its full path, at the sizes of a copy of the operator's database (a
    /// 17-character name and a 36-character path, on every row): 315,392 bytes. That left less than
    /// the margin under 340,000, so the constant rose to 350,000 (T1.69, issue #98). The constant is
    /// the figure with a margin of more than 10 %,
    /// so a small change to a row does not fail the build and a large one does. The operator's real
    /// file grows about nine times faster (see the class remarks); the documents state that rate.
    /// It exists because the file holds the operator's prompts and Claude's answers: a retention
    /// window is only reassuring if somebody has said what it holds.
    /// </para>
    /// <para>
    /// <strong>It is asserted, not merely written down.</strong> <c>GrowthMeasurement</c> writes a
    /// day through this store on every build and fails if the real figure has moved above this
    /// number <em>or</em> far below it — a constant that overstates the cost misleads as surely as
    /// one that understates it. So this cannot quietly become a guess wearing a measurement's
    /// clothes: change what is stored and the test says so.
    /// </para>
    /// </remarks>
    public const long TypicalBytesPerDay = 350_000;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            id           INTEGER PRIMARY KEY,
            session_id   TEXT    NOT NULL,
            ts           TEXT    NOT NULL,
            event_type   TEXT    NOT NULL,
            payload_json TEXT    NOT NULL,
            cwd          TEXT    NOT NULL
        );

        -- events.session_title, decisions.session_title and decisions.cwd (T1.69, issue #98) are not
        -- here: AddNameColumns adds them, to a new file and to an old one alike, so there is one path.
        -- One row per decision the dashboard made, or deliberately did not make (T1.37,
        -- issue #48). event_id is the causing row in events, or NULL for a tick. reason and
        -- detail carry enums and identifiers (T1.24); the name and path have their own columns. Created by the
        -- same schema step as events, so an existing database gains it on the next start.
        CREATE TABLE IF NOT EXISTS decisions (
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

        -- One row per start of the dashboard (T1.60, issue #78). stopped_at stays NULL until a
        -- clean stop, so a kill or a crash shows as a start with no stop. Times are UTC and end
        -- in Z from the first row. port is NULL when ingress could not bind. data_root is the one
        -- path, and it is the dashboard's own folder.
        CREATE TABLE IF NOT EXISTS runs (
            id          INTEGER PRIMARY KEY,
            started_at  TEXT    NOT NULL,
            stopped_at  TEXT,
            version     TEXT    NOT NULL,
            port        INTEGER,
            data_root   TEXT    NOT NULL
        );
        """;

    /// <summary>
    /// The indexes (T1.63, issue #79): a query by session, by time or by kind reads only the rows it
    /// needs. Run after the T1.62 conversion, never with the tables: see <c>Connect</c>.
    /// </summary>
    /// <remarks>
    /// <c>ix_decisions_kind_ts</c> serves "every sound played between 14:00 and 14:10", the inner
    /// query of Impl Part 4. The others serve a session's rows in order (Impl Part 4, event flow
    /// §12), an event's decisions, and a time range. Each is <c>IF NOT EXISTS</c>, so an existing file
    /// gains them at its next connection and a later one costs nothing.
    /// </remarks>
    private const string Indexes = """
        CREATE INDEX IF NOT EXISTS ix_events_session_id    ON events (session_id, id);
        CREATE INDEX IF NOT EXISTS ix_events_ts            ON events (ts);
        CREATE INDEX IF NOT EXISTS ix_decisions_session_id ON decisions (session_id, id);
        CREATE INDEX IF NOT EXISTS ix_decisions_event_id   ON decisions (event_id);
        CREATE INDEX IF NOT EXISTS ix_decisions_ts         ON decisions (ts);
        CREATE INDEX IF NOT EXISTS ix_decisions_kind_ts    ON decisions (kind, ts);
        """;

    private const string InsertRun = """
        INSERT INTO runs (started_at, version, port, data_root)
        VALUES ($started_at, $version, $port, $data_root);
        SELECT last_insert_rowid();
        """;

    private const string StopRunStatement = """
        UPDATE runs SET stopped_at = $stopped_at WHERE id = $id;
        """;

    private const string Insert = """
        INSERT INTO events (session_id, ts, event_type, payload_json, cwd, session_title)
        VALUES ($session_id, $ts, $event_type, $payload_json, $cwd, $session_title);
        """;

    private const string InsertDecision = """
        INSERT INTO decisions (event_id, ts, session_id, kind, from_state, to_state, reason, detail, session_title, cwd)
        VALUES ($event_id, $ts, $session_id, $kind, $from_state, $to_state, $reason, $detail, $session_title, $cwd);
        """;

    /// <summary>
    /// The least time from a failed write to the next attempt (the operator's ruling in #71).
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <c>PRAGMA user_version</c> once every time in the file is UTC (T1.62). 0, the default, is a
    /// file from before: its times are converted at its next connection.
    /// </summary>
    public const long UtcTimesVersion = 1;

    /// <summary>
    /// <c>PRAGMA user_version</c> once the file has the session's name and path columns (T1.69,
    /// issue #98). Raised by <c>AddNameColumns</c>, which checks the columns themselves and never
    /// trusts this number alone.
    /// </summary>
    public const long NameColumnsVersion = 2;

    /// <summary>The columns T1.69 adds, in the order they are added: (table, column).</summary>
    private static readonly (string Table, string Column)[] NameColumns =
    [
        ("events", "session_title"),
        ("decisions", "session_title"),
        ("decisions", "cwd"),
    ];

    // Available, as an int, so that it can be published with Volatile: the writer's thread sets
    // it, and the UI thread's tick reads it for the history notice.
    private const int Unknown = 0;
    private const int Up = 1;
    private const int Down = 2;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly IClock _clock;

    // Held by every write, both reads and Dispose (T1.59, issue #84). Only the archive writer's
    // thread, the replay and the disposing thread take it; the consumer never calls the store.
    private readonly Lock _gate = new();

    private SqliteConnection? _connection;
    private DateTimeOffset? _failedAt;
    private bool _announced;
    private bool _stackWritten;
    private bool _disposed;
    private int _available = Unknown;

    /// <summary>Creates the store over the dashboard's data folder.</summary>
    /// <param name="paths">The data folder.</param>
    /// <param name="logger">Where the open, the first failure and a recovery are logged.</param>
    /// <param name="clock">The clock that spaces the attempts after a failure; the system clock if null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="logger"/> is null.</exception>
    public SqliteEventStore(DashboardPaths paths, ILogger logger, IClock? clock = null)
        : this(Located(paths), logger, clock)
    {
    }

    /// <summary>Creates the store over an explicit file, so tests can use a temporary one.</summary>
    /// <param name="path">The database file.</param>
    /// <param name="logger">Where the open, the first failure and a recovery are logged.</param>
    /// <param name="clock">The clock that spaces the attempts after a failure; the system clock if null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="logger"/> is null.</exception>
    public SqliteEventStore(string path, ILogger logger, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(logger);

        _path = path;
        _logger = logger;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>How many records have been written. Diagnostic only.</summary>
    public long WrittenCount { get; private set; }

    /// <summary>How many attempts to write failed. Diagnostic only.</summary>
    public long FailedCount { get; private set; }

    /// <summary>
    /// How many records were lost since the last failure began: those whose write failed, and those
    /// that arrived before the next attempt was due. Zero again once a write succeeds.
    /// </summary>
    public long LostCount { get; private set; }

    /// <summary>
    /// Whether the last write succeeded: null before the first attempt, then true or false.
    /// </summary>
    /// <remarks>
    /// The one member read from another thread: the history notice reads it on the UI thread's tick.
    /// It is published with <see cref="Volatile"/>, so the tick never reads a stale value for long.
    /// </remarks>
    public bool? Available => Volatile.Read(ref _available) switch
    {
        Up => true,
        Down => false,
        _ => null,
    };

    /// <summary>The file this store writes to.</summary>
    public string Path => _path;

    /// <summary>
    /// A test seam: runs inside each write's transaction, just before the commit, on the writer's
    /// thread. A test blocks here to hold a write open while it closes the store (T1.59).
    /// </summary>
    internal Action? InsideTransaction { get; set; }

    /// <summary>
    /// The waits before each further open of a new connection that SQLite opened read-only (T1.74, issue #109):
    /// four more opens within 1.0 s, the operator's "a few times within about one second", and under the 1.5 s
    /// that SQLite itself waits for a file another program holds.
    /// </summary>
    internal static readonly TimeSpan[] ReadOnlyWaits =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(400),
    ];

    /// <summary>
    /// A test seam: the wait before a further open of a read-only connection, on the writer's thread under the
    /// store's lock. A blocking sleep in the product; a test replaces it, so no test sleeps (T1.74).
    /// </summary>
    internal Action<TimeSpan> Pause { get; set; } = Thread.Sleep;

    /// <summary>
    /// A test seam: runs inside the conversion's transaction, just before the commit (T1.62). A test
    /// throws here to make the conversion fail and roll back.
    /// </summary>
    internal Action? InsideConversion { get; set; }

    /// <summary>
    /// A test seam: runs inside the prune's transaction, just before the commit (T1.64). A test
    /// throws here to make the prune fail and roll back.
    /// </summary>
    internal Action? InsidePrune { get; set; }

    /// <inheritdoc/>
    public bool Append(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        return Append(new ArchiveRecord(inboundEvent, []));
    }

    /// <inheritdoc/>
    public bool Append(ArchiveRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.IsEmpty)
        {
            return false;
        }

        lock (_gate)
        {
            // A write after the close is dropped without a sound: no log line, no change to
            // Available. The store is going away, so the board has nothing to show (T1.59).
            return !_disposed && AppendUnderGate(record);
        }
    }

    /// <summary>The write itself. The caller holds <see cref="_gate"/> and has seen the store open.</summary>
    private bool AppendUnderGate(ArchiveRecord record)
    {
        if (Waiting())
        {
            return false;
        }

        try
        {
            var connection = Connect();

            // ONE TRANSACTION FOR THE EVENT AND ITS DECISIONS (T1.37). The decision rows take
            // the event's id from this insert, on this thread — no other thread ever waits for
            // an id — and a throw between the two inserts leaves neither row, so the record can
            // never say an event happened while losing why, or the reverse.
            using var transaction = connection.BeginTransaction();

            long? eventId = null;

            if (record.Event is { } inboundEvent)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = Insert;
                command.Parameters.AddWithValue("$session_id", inboundEvent.SessionId.Value);
                command.Parameters.AddWithValue("$ts", Utc(inboundEvent.Timestamp));
                command.Parameters.AddWithValue("$event_type", inboundEvent.HookEventName);

                // The hook body, as a bound parameter, never string concatenation: text is data. A
                // SqliteException's message names the error and the schema, not a parameter value (probed
                // at T1.17).
                command.Parameters.AddWithValue("$payload_json", inboundEvent.Payload.Reveal());

                command.Parameters.AddWithValue("$cwd", inboundEvent.Cwd);

                // The session's name as the Registry holds it after this event (T1.69): its own column,
                // and a bound parameter like the payload.
                command.Parameters.AddWithValue("$session_title", (object?)record.EventSessionTitle ?? DBNull.Value);

                command.ExecuteNonQuery();

                using var lastId = connection.CreateCommand();
                lastId.Transaction = transaction;
                lastId.CommandText = "SELECT last_insert_rowid();";
                eventId = (long)lastId.ExecuteScalar()!;
            }

            WriteDecisions(connection, transaction, eventId, record.Decisions);

            InsideTransaction?.Invoke();
            transaction.Commit();

            WrittenCount++;
            Recorded();

            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FailedCount++;

            Unavailable(ex);

            return false;
        }
    }

    /// <summary>
    /// Appends decision rows against an event id that already exists — the replay's write
    /// (T1.37). Never touches <c>events</c>.
    /// </summary>
    /// <returns>Whether the rows were written.</returns>
    public bool AppendDecisions(long? eventId, IReadOnlyList<Decision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        if (decisions.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return !_disposed && AppendDecisionsUnderGate(eventId, decisions);
        }
    }

    /// <summary>The replay's write itself. The caller holds <see cref="_gate"/> and has seen the store open.</summary>
    private bool AppendDecisionsUnderGate(long? eventId, IReadOnlyList<Decision> decisions)
    {
        if (Waiting())
        {
            return false;
        }

        try
        {
            var connection = Connect();

            using var transaction = connection.BeginTransaction();
            WriteDecisions(connection, transaction, eventId, decisions);
            InsideTransaction?.Invoke();
            transaction.Commit();
            Recorded();

            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FailedCount++;

            Unavailable(ex);

            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <strong>One transaction</strong> (T1.64, issue #81): the decisions of the events older than
    /// the limit, those events, the decisions with no event older than the limit, and the runs that
    /// started before it, except <paramref name="keepRunId"/>. A failure rolls every row back and
    /// follows T1.54's rule, so an event never loses part of its record.
    /// </para>
    /// <para>
    /// The limit is written in the one UTC form of T1.62 and compared as text, which T1.62 made
    /// correct, and the index on <c>ts</c> of T1.63 makes cheap. No <c>VACUUM</c> (the operator's
    /// ruling): the space of the deleted rows is used again for new rows, so the file stops growing
    /// and does not shrink.
    /// </para>
    /// </remarks>
    public PruneCounts? Prune(int retentionDays, DateTimeOffset now, long? keepRunId)
    {
        if (retentionDays <= 0)
        {
            return PruneCounts.None;
        }

        lock (_gate)
        {
            // Inside the minute after a failure: not now, and not counted as a lost record.
            if (_disposed || _failedAt is { } failedAt && _clock.Now - failedAt < RetryAfter)
            {
                return null;
            }

            // A window older than the calendar keeps everything; this also keeps FromDays in range.
            if (retentionDays >= (now - DateTimeOffset.MinValue).TotalDays)
            {
                return PruneCounts.None;
            }

            var limit = Utc(now - TimeSpan.FromDays(retentionDays));

            try
            {
                var connection = Connect();
                var watch = System.Diagnostics.Stopwatch.StartNew();

                using var transaction = connection.BeginTransaction();

                long Delete(string sql)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$limit", limit);
                    command.Parameters.AddWithValue("$keep", (object?)keepRunId ?? DBNull.Value);
                    return command.ExecuteNonQuery();
                }

                var decisions = Delete("DELETE FROM decisions WHERE event_id IN (SELECT id FROM events WHERE ts < $limit);");
                var events = Delete("DELETE FROM events WHERE ts < $limit;");
                decisions += Delete("DELETE FROM decisions WHERE event_id IS NULL AND ts < $limit;");
                var runs = Delete("DELETE FROM runs WHERE started_at < $limit AND id IS NOT $keep;");

                InsidePrune?.Invoke();
                transaction.Commit();

                Recorded();

                var counts = new PruneCounts(events, decisions, runs);

                if (counts.Total > 0)
                {
                    // Counts, the limit and the time it took: never a payload and never a row's time.
                    _logger.Information(
                        "Pruned {DatabaseFile} to the last {RetentionDays} days: deleted {Events} events, " +
                        "{Decisions} decisions and {Runs} runs from before {Limit}, in {ElapsedMs} ms.",
                        _path,
                        retentionDays,
                        events,
                        decisions,
                        runs,
                        limit,
                        watch.ElapsedMilliseconds);
                }

                return counts;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                FailedCount++;

                Unavailable(ex);

                return null;
            }
        }
    }

    /// <inheritdoc/>
    public long? StartRun(RunStart run, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(run);

        lock (_gate)
        {
            if (_disposed || Waiting())
            {
                return null;
            }

            try
            {
                var connection = Connect();

                using var command = connection.CreateCommand();
                command.CommandText = InsertRun;
                command.Parameters.AddWithValue("$started_at", Utc(startedAt));
                command.Parameters.AddWithValue("$version", run.Version);
                command.Parameters.AddWithValue("$port", (object?)run.Port ?? DBNull.Value);
                command.Parameters.AddWithValue("$data_root", run.DataRoot);

                var id = (long)command.ExecuteScalar()!;

                WrittenCount++;
                Recorded();

                return id;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                FailedCount++;

                Unavailable(ex);

                return null;
            }
        }
    }

    /// <inheritdoc/>
    public bool StopRun(long runId, DateTimeOffset stoppedAt)
    {
        lock (_gate)
        {
            if (_disposed || Waiting())
            {
                return false;
            }

            try
            {
                var connection = Connect();

                using var command = connection.CreateCommand();
                command.CommandText = StopRunStatement;
                command.Parameters.AddWithValue("$stopped_at", Utc(stoppedAt));
                command.Parameters.AddWithValue("$id", runId);
                command.ExecuteNonQuery();

                WrittenCount++;
                Recorded();

                return true;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                FailedCount++;

                Unavailable(ex);

                return false;
            }
        }
    }

    /// <summary>
    /// A time as <c>dashboard.db</c> holds it, in every table: UTC, round-trip format, seven
    /// fractional digits and <c>Z</c>, for example <c>2026-10-02T12:03:11.1230000Z</c> (T1.60 for
    /// <c>runs</c>, T1.62 for <c>events</c> and <c>decisions</c>).
    /// </summary>
    /// <remarks>
    /// Text order is time order only when every row has the same form, so every writer of
    /// <c>ts</c>, <c>started_at</c> and <c>stopped_at</c> uses this, and the conversion writes it
    /// too. Not <c>ToUniversalTime().ToString("o")</c> on a <see cref="DateTimeOffset"/>, which
    /// ends in <c>+00:00</c>.
    /// </remarks>
    internal static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static void WriteDecisions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? eventId,
        IReadOnlyList<Decision> decisions)
    {
        foreach (var decision in decisions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertDecision;
            command.Parameters.AddWithValue("$event_id", (object?)eventId ?? DBNull.Value);
            command.Parameters.AddWithValue("$ts", Utc(decision.Ts));
            command.Parameters.AddWithValue("$session_id", (object?)decision.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", decision.Kind.ToString());
            command.Parameters.AddWithValue("$from_state", (object?)decision.FromState ?? DBNull.Value);
            command.Parameters.AddWithValue("$to_state", (object?)decision.ToState ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", (object?)decision.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)decision.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$session_title", (object?)decision.SessionTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$cwd", (object?)decision.Cwd ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>One archived event, as the replay reads it back (T1.37).</summary>
    /// <param name="Id">The row id — what a decision's <c>event_id</c> points at.</param>
    /// <param name="SessionId">The session.</param>
    /// <param name="Ts">The event's timestamp, ISO-8601.</param>
    /// <param name="EventType">The hook event name.</param>
    /// <param name="Payload">The verbatim payload.</param>
    /// <param name="Cwd">The working directory.</param>
    public sealed record ArchivedEvent(
        long Id,
        string SessionId,
        string Ts,
        string EventType,
        PayloadJson Payload,
        string Cwd);

    /// <summary>
    /// Reads every event row in id order — the replay's input (T1.37). Never modifies anything.
    /// </summary>
    /// <remarks>
    /// Into memory rather than streamed, deliberately: the replay writes <c>decisions</c> rows on
    /// this same connection while it walks, and holding a reader open across those writes is the
    /// kind of same-connection interleaving that works until it does not. A month of history is
    /// about 80 MB at the operator's real rate (T1.64); the simplicity is worth the allocation, and
    /// replay is run by hand on a copy.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The store is closed.</exception>
    public IReadOnlyList<ArchivedEvent> ReadEvents()
    {
        lock (_gate)
        {
            // A closed store never opens the file again (T1.59). No caller reads after the close.
            ObjectDisposedException.ThrowIf(_disposed, this);

            return ReadEventsUnderGate();
        }
    }

    private List<ArchivedEvent> ReadEventsUnderGate()
    {
        var connection = Connect();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, session_id, ts, event_type, payload_json, cwd FROM events ORDER BY id;";

        var rows = new List<ArchivedEvent>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new ArchivedEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                new PayloadJson(reader.GetString(4)),
                reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>
    /// Every run's <c>started_at</c> and <c>stopped_at</c> (null after a kill), as text, in id
    /// order: where replay forgets its sessions (T1.60). Never modifies anything.
    /// </summary>
    /// <remarks>
    /// Text, not parsed here: the caller compares instants, and a row that does not parse is the
    /// caller's to count. Connecting runs the schema step, so a database older than T1.60 gains an
    /// empty table and reads none, which replays as one run, as before.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The store is closed.</exception>
    public IReadOnlyList<(string StartedAt, string? StoppedAt)> ReadRuns()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var connection = Connect();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT started_at, stopped_at FROM runs ORDER BY id;";

            var runs = new List<(string StartedAt, string? StoppedAt)>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                runs.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            return runs;
        }
    }

    /// <summary>How many decisions rows the file already holds — replay's refusal check (T1.37).</summary>
    /// <remarks>
    /// Connecting runs the schema step, so a database older than T1.37 gains an empty table and
    /// reads zero, which is the case replay exists for.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The store is closed.</exception>
    public long CountDecisions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var connection = Connect();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM decisions;";

            return (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Closes the file. Safe to call twice, and safe while the writer's thread is inside a write:
    /// it waits for that write to commit (T1.59).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Microsoft.Data.Sqlite pools connections, so disposing one does not release the file
            // handle — measured at T1.17, where a File.Delete straight after a using block failed
            // with "used by another process". A resident app that never released the handle would
            // hold dashboard.db open against a backup or a copy for the life of the process.
            //
            // This store's own pool only (T1.78, issue #40; the operator's ruling). ClearAllPools cleared
            // every pool in the process, and the library then disposes a connection that another thread
            // has just opened, for a moment: a store opening its file then lost its new connection and
            // made no tables. One dashboard has one store; the tests run many in one process.
            if (_connection is not null)
            {
                CloseForReal(_connection);
            }

            _connection = null;
        }
    }

    private static string Located(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return paths.DatabaseFile;
    }

    private SqliteConnection Connect()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var connection = OpenWritable();

        try
        {
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }

            // The first upgrade step this file has had (T1.62). On the writer's thread, like every
            // connection, so the start never waits; records that arrive meanwhile wait in the channel.
            ConvertTimesOnce(connection);

            // The session's name and path (T1.69), after the times and before the indexes, on the same
            // connection and thread. Safe on every open: it adds only the columns that are not there.
            AddNameColumns(connection);

            // THEN THE INDEXES (T1.63), and the order is certain because it is this sequence: one
            // connection, one thread, under the store's lock, the conversion's transaction committed
            // before this line runs. On an old file the conversion then rewrites no index entries, and
            // the indexes are built once from the converted times. A failure here is caught below like
            // any other, and the next attempt, a minute later, creates what is still absent.
            using (var indexes = connection.CreateCommand())
            {
                indexes.CommandText = Indexes;
                indexes.ExecuteNonQuery();
            }
        }
        catch
        {
            // Not kept, and closed for real: the next attempt opens afresh, after T1.54's minute (T1.74).
            CloseForReal(connection);
            throw;
        }

        _connection = connection;

        // Once per process, not once per connection. A connection made while a failure is open is
        // announced by the recovery line instead, with what was lost: one line, not two (T1.54).
        var first = !_announced;
        _announced = true;

        if (first && _failedAt is null)
        {
            _logger.Information(
                "Recording events to {DatabaseFile}. It holds hook payloads and keeps as many days as Claude Code's " +
                "cleanupPeriodDays; a synthetic typical day adds up to {BytesPerDay} bytes.",
                _path,
                TypicalBytesPerDay);
        }

        return connection;
    }

    /// <summary>
    /// Opens a new connection that can write, with a short second try when SQLite opens it read-only (T1.74,
    /// issue #109).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why it can be read-only, with no error.</strong> When SQLite cannot open the file for writing at
    /// that moment, it opens it read-only: the file is marked read-only, or another program reads it and lets
    /// others only read (PowerShell's <c>Get-FileHash</c>, .NET's <c>File.OpenRead</c>). The connection keeps that
    /// mode for as long as it lives, and the first write then fails with SQLite's code 8.
    /// </para>
    /// <para>
    /// <strong>So the check comes first,</strong> before the schema steps: <c>sqlite3_db_readonly</c>, SQLite's
    /// own answer. A read-only connection is closed for real and opened again after each of
    /// <see cref="ReadOnlyWaits"/>: a program that reads the file for a moment then costs nothing. SQLite waits
    /// about that long for a file that is held, but not in this case, so the store does the wait itself. Still
    /// read-only after the last open: the store throws SQLite's code 8, and T1.54's failure path goes on as
    /// before (the notice, the retry each minute, the lost count).
    /// </para>
    /// <para>
    /// <strong>Each further open is a new connection</strong> only because the one before it was closed for real
    /// (<see cref="CloseForReal"/>): a plain close returns it to the pool, and the next open gets the same
    /// read-only connection back.
    /// </para>
    /// </remarks>
    private SqliteConnection OpenWritable()
    {
        var connection = OpenOne();
        var more = 0;

        while (IsReadOnly(connection))
        {
            CloseForReal(connection);

            if (more == ReadOnlyWaits.Length)
            {
                throw new SqliteException(
                    "The database was opened read-only: SQLite could not open it for writing after " +
                    (more + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " opens within " +
                    ReadOnlyWaits.Sum(wait => wait.TotalSeconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                    " s. The file may be marked read-only, or another program may hold it.",
                    SqliteReadOnly);
            }

            Pause(ReadOnlyWaits[more]);
            more++;
            connection = OpenOne();
        }

        if (more > 0)
        {
            // Only when it happened: a normal open writes no line.
            _logger.Information(
                "{DatabaseFile} was opened read-only, and could be written after {MoreOpens} more opens.",
                _path,
                more);
        }

        return connection;
    }

    /// <summary>SQLite's result code for a write to a read-only database (<c>SQLITE_READONLY</c>).</summary>
    private const int SqliteReadOnly = 8;

    /// <summary>One new connection, opened; closed for real if the open throws.</summary>
    private SqliteConnection OpenOne()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        try
        {
            connection.Open();
        }
        catch
        {
            CloseForReal(connection);
            throw;
        }

        return connection;
    }

    /// <summary>Whether SQLite opened this connection's main database read-only: 1 from <c>sqlite3_db_readonly</c>.</summary>
    private static bool IsReadOnly(SqliteConnection connection) =>
        SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "main") == 1;

    /// <summary>
    /// Closes a connection that failed, for real, so that it is never used again (T1.74, issue #109; the operator's
    /// ruling: pooling stays on, and a failed connection is closed for real).
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Data.Sqlite</c> pools connections, and a plain close returns the real connection to the pool:
    /// the next open gets the same one back, in the mode it was opened with. <c>ClearPool</c> first marks the
    /// connection's pool, so the close that follows closes the file handle. Every place that gives up a connection
    /// after a failure uses this: the <c>catch</c> in <see cref="Connect"/>, <see cref="OpenOne"/>,
    /// <see cref="OpenWritable"/>, <see cref="Unavailable"/> and, since T1.78, <see cref="Dispose"/>, which
    /// released the file with <c>ClearAllPools</c> until then (T1.17).
    /// </remarks>
    private static void CloseForReal(SqliteConnection connection)
    {
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }

    /// <summary>
    /// Rewrites every <c>ts</c> in <c>events</c> and <c>decisions</c> in the one UTC form, once, and
    /// sets <c>PRAGMA user_version</c> to 1 (T1.62, issue #80).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One transaction.</strong> A failure (a full disk, a locked file) rolls back every row
    /// and leaves <c>user_version</c> at 0. The exception goes to the caller's catch, which is T1.54's:
    /// the history notice, and another attempt a minute later.
    /// </para>
    /// <para>
    /// <strong>A time that will not parse is left as it is and counted.</strong> One bad row does not
    /// stop the rest. The parse is replay's: an ISO 8601 time with its offset or <c>Z</c>.
    /// </para>
    /// <para>
    /// Rows, ids and payloads are never changed: only the text of the time. No <c>VACUUM</c> (the
    /// operator's ruling on #81). The log line has counts and the time it took, never a time from a
    /// row.
    /// </para>
    /// </remarks>
    private void ConvertTimesOnce(SqliteConnection connection)
    {
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";

            if (Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture) >= UtcTimesVersion)
            {
                return;
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var counts = new ConversionCounts();

        using (var transaction = connection.BeginTransaction())
        {
            ConvertTable(connection, transaction, "events", counts);
            ConvertTable(connection, transaction, "decisions", counts);

            using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = $"PRAGMA user_version = {UtcTimesVersion};";
                version.ExecuteNonQuery();
            }

            InsideConversion?.Invoke();
            transaction.Commit();
        }

        // A new file has no rows and nothing to say; it only gains its version.
        if (counts.Rewritten + counts.AlreadyUtc + counts.Unparsed == 0)
        {
            return;
        }

        _logger.Information(
            "Stored the times in {DatabaseFile} in UTC, once: {Rewritten} rows converted, {AlreadyUtc} " +
            "already in UTC, {Unparsed} left as they were because their time would not parse. It took " +
            "{ElapsedMs} ms.",
            _path,
            counts.Rewritten,
            counts.AlreadyUtc,
            counts.Unparsed,
            watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Adds <c>events.session_title</c>, <c>decisions.session_title</c> and <c>decisions.cwd</c> where
    /// they are not, and raises <c>PRAGMA user_version</c> to 2 (T1.69, issue #98).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Safe to run again, on every open.</strong> Each column is looked up in
    /// <c>pragma_table_info</c> before it is added, so a second start adds nothing, and a file whose
    /// version says 2 but lacks a column (a copy put back, a hand edit) still gains it. The version is
    /// raised, never lowered, and never trusted alone.
    /// </para>
    /// <para>
    /// <strong>Old rows keep NULL</strong>, and are not filled in afterwards: <c>ALTER TABLE … ADD
    /// COLUMN</c> writes no row, so it is quick on a large file. One transaction: a failure adds no
    /// column and leaves the version, and T1.54's catch tries again a minute later. The log line has
    /// counts and the time, and is written only for a file that had rows.
    /// </para>
    /// </remarks>
    private void AddNameColumns(SqliteConnection connection)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var added = 0;

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var (table, column) in NameColumns)
            {
                if (HasColumn(connection, transaction, table, column))
                {
                    continue;
                }

                using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT;";
                alter.ExecuteNonQuery();
                added++;
            }

            using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = "PRAGMA user_version;";

                if (Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture) < NameColumnsVersion)
                {
                    version.CommandText = $"PRAGMA user_version = {NameColumnsVersion};";
                    version.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        if (added == 0 || !HasRows(connection))
        {
            return;
        }

        _logger.Information(
            "Added the session's name and path columns to {DatabaseFile}: {Added} columns, in {ElapsedMs} ms. " +
            "Rows written before keep them empty.",
            _path,
            added,
            watch.ElapsedMilliseconds);
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // The table name is one of NameColumns' constants, never input.
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool HasRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM events) OR EXISTS (SELECT 1 FROM decisions);";

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void ConvertTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        ConversionCounts counts)
    {
        // Read first, then write: no reader is held open across the updates on this connection.
        var rows = new List<(long Id, string Ts)>();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT id, ts FROM {table};";

            using var reader = select.ExecuteReader();

            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE {table} SET ts = $ts WHERE id = $id;";
        var tsParameter = update.Parameters.Add("$ts", SqliteType.Text);
        var idParameter = update.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, ts) in rows)
        {
            if (!DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                counts.Unparsed++;
                continue;
            }

            var utc = Utc(at);

            if (string.Equals(utc, ts, StringComparison.Ordinal))
            {
                counts.AlreadyUtc++;
                continue;
            }

            tsParameter.Value = utc;
            idParameter.Value = id;
            update.ExecuteNonQuery();
            counts.Rewritten++;
        }
    }

    private sealed class ConversionCounts
    {
        public long Rewritten { get; set; }

        public long AlreadyUtc { get; set; }

        public long Unparsed { get; set; }
    }

    /// <summary>
    /// Whether this record falls inside the minute after a failure, and so is lost without an
    /// attempt. Counts it if so.
    /// </summary>
    private bool Waiting()
    {
        if (_failedAt is not { } failedAt || _clock.Now - failedAt >= RetryAfter)
        {
            return false;
        }

        LostCount++;

        return true;
    }

    /// <summary>A write succeeded. After a failure, says so once, with what was lost.</summary>
    private void Recorded()
    {
        Volatile.Write(ref _available, Up);

        if (_failedAt is null)
        {
            return;
        }

        _logger.Information(
            "Recording events to {DatabaseFile} again. {LostCount} records were lost while it could " +
            "not be written; each is one event with its decisions, or the decisions of one tick.",
            _path,
            LostCount);

        _failedAt = null;
        LostCount = 0;
    }

    /// <summary>A write failed: the record is lost, and the next attempt waits a minute.</summary>
    private void Unavailable(Exception ex)
    {
        var first = _failedAt is null;

        _failedAt = _clock.Now;
        LostCount++;
        Volatile.Write(ref _available, Down);

        // Closed for real (T1.74): a plain close would hand this connection back at the next attempt, and a
        // connection that SQLite opened read-only stays read-only.
        if (_connection is not null)
        {
            CloseForReal(_connection);
        }

        _connection = null;

        if (!first)
        {
            // A retry that fails writes nothing. The disk said why at the first failure, and a line
            // a minute for a disk that stays full would bury that line.
            return;
        }

        // ONCE per failure. A failing disk fails on every event, and a line per event would bury the
        // log in the one situation where the operator most needs to read it.
        const string Template =
            "Cannot record events to {DatabaseFile}, so the dashboard runs with no history. This " +
            "is a lost feature, not a fault: everything on screen still works, and the window says " +
            "so. The dashboard tries again each minute, and says so here when it records again.";

        if (!_stackWritten)
        {
            // The stack once per process (T1.55, from the T1.54 review): the first failure is the
            // one a reader needs to diagnose.
            _stackWritten = true;
            _logger.Warning(ex, Template, _path);

            return;
        }

        // A later failure writes the type and the message, not the stack. A disk that flaps (a
        // backup program that locks the file) then costs one short line a minute, not a stack a
        // minute. The message names the error and the file, never a payload (see Append).
        _logger.Warning(Template + " {ErrorType}: {ErrorMessage}", _path, ex.GetType().Name, ex.Message);
    }
}
