using System.IO;
using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core.Events;
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
/// created or written, this says so exactly once, stops trying, and returns
/// <see langword="false"/> for ever after. The dashboard runs with no history.
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

    private readonly string _path;
    private readonly ILogger _logger;

    private SqliteConnection? _connection;
    private bool _unavailable;
    private bool _disposed;

    /// <summary>Creates the store over the dashboard's data folder.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SqliteEventStore(DashboardPaths paths, ILogger logger)
        : this(Located(paths), logger)
    {
    }

    /// <summary>Creates the store over an explicit file, so tests can use a temporary one.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SqliteEventStore(string path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(logger);

        _path = path;
        _logger = logger;
    }

    /// <summary>How many rows have been written. Diagnostic only.</summary>
    public long WrittenCount { get; private set; }

    /// <summary>How many rows were lost to a failing disk. Diagnostic only.</summary>
    public long FailedCount { get; private set; }

    /// <summary>Whether the store has given up. Null until the first attempt.</summary>
    public bool? Available { get; private set; }

    /// <summary>The file this store writes to.</summary>
    public string Path => _path;

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

        if (_unavailable || _disposed || record.IsEmpty)
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

            transaction.Commit();

            WrittenCount++;

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

        if (_unavailable || _disposed || decisions.Count == 0)
        {
            return false;
        }

        try
        {
            var connection = Connect();

            using var transaction = connection.BeginTransaction();
            WriteDecisions(connection, transaction, eventId, decisions);
            transaction.Commit();

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
    public IReadOnlyList<ArchivedEvent> ReadEvents()
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

    /// <summary>Closes the file. Safe to call twice.</summary>
    public void Dispose()
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
        Available = true;

        _logger.Information(
            "Recording events to {DatabaseFile}. It is not pruned before Phase 5 and holds hook " +
            "payloads, so it grows by roughly {BytesPerDay} bytes a day on typical traffic.",
            _path,
            TypicalBytesPerDay);

        return connection;
    }

    private void Unavailable(Exception ex)
    {
        _unavailable = true;
        Available = false;

        _connection?.Dispose();
        _connection = null;

        // ONCE. A failing disk fails on every event, and a line per event would bury the log in
        // the one situation where the operator most needs to read it.
        _logger.Warning(
            ex,
            "Cannot record events to {DatabaseFile}, so the dashboard runs with no history. This " +
            "is a lost feature, not a fault: everything on screen still works. No further attempt " +
            "will be made until restart.",
            _path);
    }
}
