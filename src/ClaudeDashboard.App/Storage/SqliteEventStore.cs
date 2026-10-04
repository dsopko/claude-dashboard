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
/// <strong>This file holds the operator's words, and the log is not meant to.</strong> The
/// reasoning is on <see cref="PayloadJson"/>: the log is diagnostic and leaves the machine, this
/// file is the product's own store and does not. Everything here follows from that one asymmetry —
/// the body goes into a bound parameter and never into a message template.
/// </para>
/// <para>
/// <strong>Stated as an intent rather than a guarantee, deliberately.</strong> Nothing in this
/// class puts the body in a log line, and its tests hold that. But the intent is enforced by
/// construction only for the raw body: the same words live unprotected on a further eleven
/// properties spanning the wire DTO, the domain events, the Registry's <c>Exchange</c> and the
/// row the screen binds to. <c>UnprotectedTextInventory</c> holds that set exactly;
/// <see cref="PayloadJson"/>'s remarks carry the reasoning and the filed follow-up. A sentence
/// here promising more than that would be the same mistake in a second file.
/// </para>
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
/// <strong>It grows without limit until Phase 5, and here is what that costs.</strong> There is no
/// pruning here on purpose — retention is Phase 5's, and building half of it now would mean
/// deleting the operator's history by a policy nobody has agreed. Measured at T1.17 through this
/// store, at payload sizes taken from 4,439 real prompts and 11,757 real assistant messages across
/// 95 active days:
/// </para>
/// <list type="bullet">
///   <item><description><strong>a typical day: about 288 KiB</strong> — see <see cref="TypicalBytesPerDay"/>.</description></item>
///   <item><description><strong>the busiest day in 95: about 2.6 MiB</strong>, roughly nine times a typical one.</description></item>
///   <item><description><strong>a year of typical days: about 103 MiB</strong>, unpruned.</description></item>
/// </list>
/// <para>
/// Those are upper bounds by construction — the per-day counts come from transcript entries, which
/// over-count the hooks that actually arrive. <c>GrowthMeasurement</c> re-measures all of it on
/// every build, and states what the figures do and do not cover. Anyone changing what is stored
/// should read the new number off a test run rather than reasoning about it.
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
    /// About how much this table grows on a typical active day — 300 KB, measured, not estimated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Measured at T1.17 through this store, at real payload sizes.</strong> A typical day
    /// wrote 294,912 bytes; this is that, rounded up. It exists because the file is unpruned until
    /// Phase 5 and holds the operator's prompts and Claude's answers: "retention is Phase 5" is
    /// only reassuring if somebody has said what Phase 5 will be cleaning up.
    /// </para>
    /// <para>
    /// <strong>It is asserted, not merely written down.</strong> <c>GrowthMeasurement</c> writes a
    /// day through this store on every build and fails if the real figure has moved above this
    /// number <em>or</em> far below it — a constant that overstates the cost misleads as surely as
    /// one that understates it. So this cannot quietly become a guess wearing a measurement's
    /// clothes: change what is stored and the test says so.
    /// </para>
    /// </remarks>
    public const long TypicalBytesPerDay = 300_000;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            id           INTEGER PRIMARY KEY,
            session_id   TEXT    NOT NULL,
            ts           TEXT    NOT NULL,
            event_type   TEXT    NOT NULL,
            payload_json TEXT    NOT NULL,
            cwd          TEXT    NOT NULL
        );

        -- One row per decision the dashboard made, or deliberately did not make (T1.37,
        -- issue #48). event_id is the causing row in events, or NULL for a tick. reason and
        -- detail carry enums and identifiers only, never operator text (T1.24). Created by the
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
        """;

    private const string Insert = """
        INSERT INTO events (session_id, ts, event_type, payload_json, cwd)
        VALUES ($session_id, $ts, $event_type, $payload_json, $cwd);
        """;

    private const string InsertDecision = """
        INSERT INTO decisions (event_id, ts, session_id, kind, from_state, to_state, reason, detail)
        VALUES ($event_id, $ts, $session_id, $kind, $from_state, $to_state, $reason, $detail);
        """;

    /// <summary>
    /// The least time from a failed write to the next attempt (the operator's ruling in #71).
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

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
                command.Parameters.AddWithValue(
                    "$ts",
                    inboundEvent.Timestamp.ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$event_type", inboundEvent.HookEventName);

                // THE ONE PLACE THE OPERATOR'S WORDS ARE READ. A bound parameter, never string
                // concatenation and never a message template — see PayloadJson. A SqliteException's
                // message names the error and the schema, never a parameter value; that was probed at
                // T1.17 with the body as the failing parameter, and is why a failure here can be
                // logged at all.
                command.Parameters.AddWithValue("$payload_json", inboundEvent.Payload.Reveal());

                command.Parameters.AddWithValue("$cwd", inboundEvent.Cwd);

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
            command.Parameters.AddWithValue("$ts", decision.Ts.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$session_id", (object?)decision.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", decision.Kind.ToString());
            command.Parameters.AddWithValue("$from_state", (object?)decision.FromState ?? DBNull.Value);
            command.Parameters.AddWithValue("$to_state", (object?)decision.ToState ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", (object?)decision.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)decision.Detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>One archived event, as the replay reads it back (T1.37).</summary>
    /// <param name="Id">The row id — what a decision's <c>event_id</c> points at.</param>
    /// <param name="SessionId">The session.</param>
    /// <param name="Ts">The event's timestamp, ISO-8601.</param>
    /// <param name="EventType">The hook event name.</param>
    /// <param name="Payload">The verbatim payload, in its unprintable wrapper: operator text.</param>
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
    /// ~10 MB (the store's own measured 300 KB/day); the simplicity is worth the allocation.
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

            _connection?.Dispose();
            _connection = null;

            // Microsoft.Data.Sqlite pools connections, so disposing one does not release the file
            // handle — measured at T1.17, where a File.Delete straight after a using block failed
            // with "used by another process". A resident app that never released the handle would
            // hold dashboard.db open against a backup or a copy for the life of the process.
            SqliteConnection.ClearAllPools();
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

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            schema.ExecuteNonQuery();
        }

        _connection = connection;

        // Once per process, not once per connection. A connection made while a failure is open is
        // announced by the recovery line instead, with what was lost: one line, not two (T1.54).
        var first = !_announced;
        _announced = true;

        if (first && _failedAt is null)
        {
            _logger.Information(
                "Recording events to {DatabaseFile}. It is not pruned before Phase 5 and holds hook " +
                "payloads, so it grows by roughly {BytesPerDay} bytes a day on typical traffic.",
                _path,
                TypicalBytesPerDay);
        }

        return connection;
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

        _connection?.Dispose();
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
