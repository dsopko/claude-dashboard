using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The event table, checked by a SQLite the product does not use (Impl Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion about what was written goes through
/// <see cref="ForeignSqliteReader"/>.</strong> Reading back with <c>Microsoft.Data.Sqlite</c> would
/// be our writer agreeing with our reader about our schema, which is the shape of assertion that
/// cannot fail for the reasons it is supposed to catch. Windows' own <c>winsqlite3.dll</c> has no
/// stake in it, and <see cref="ForeignSqliteReaderTests"/> is the control proving that reader can
/// tell an empty database from one it could not open.
/// </para>
/// <para>
/// <strong>What is not tested here is reading.</strong> Phase 1 is write-only by ruling, so there
/// is no query surface to test; the foreign reader is a test instrument and not a product feature.
/// </para>
/// </remarks>
public sealed class SqliteEventStoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public SqliteEventStoreTests() => Directory.CreateDirectory(_folder);

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

    // ---- What lands in the file ---------------------------------------------------------------

    /// <summary>The raw body is stored exactly as it arrived.</summary>
    /// <remarks>
    /// The point of buffering it rather than re-serializing the mapped payload: the field below is
    /// one Phase 1 does not map, and it survives. A re-serialized record would have dropped it and
    /// nobody would have noticed until Phase 5 searched for it.
    /// </remarks>
    [Fact]
    public void The_body_is_stored_byte_for_byte_including_fields_phase_one_does_not_map()
    {
        var body = """{"hook_event_name":"UserPromptSubmit","prompt":"ship it","unmapped_future_field":42}""";
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            Assert.True(store.Append(TestEvents.Hook(body)));
        }

        var stored = Assert.Single(ForeignSqliteReader.Column(path, "SELECT payload_json FROM events"));

        Assert.Equal(body, stored);
        Assert.Contains("unmapped_future_field", stored, StringComparison.Ordinal);
    }

    /// <summary>Every column Impl Part 8 names is filled with the value it names.</summary>
    [Fact]
    public void Each_column_carries_what_part_eight_says_it_carries()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            store.Append(TestEvents.Hook("""{"a":1}""", sessionId: "sess-42", cwd: @"D:\work\repo"));
        }

        var row = Assert.Single(ForeignSqliteReader.Query(
            path,
            "SELECT session_id, ts, event_type, payload_json, cwd FROM events"));

        Assert.Equal("sess-42", row[0]);
        Assert.Equal("UserPromptSubmit", row[2]);
        Assert.Equal("""{"a":1}""", row[3]);
        Assert.Equal(@"D:\work\repo", row[4]);

        // Round-trips as an instant rather than as whatever the machine's locale renders.
        Assert.Equal(TestEvents.At, DateTimeOffset.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Events persist across runs — the task's own acceptance criterion.</summary>
    /// <remarks>
    /// Two stores over one path, the first disposed before the second opens, so this is a real
    /// reopen of a file on disk and not one connection's view of its own writes.
    /// </remarks>
    [Fact]
    public void Events_persist_across_runs()
    {
        var path = Db();

        using (var first = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            first.Append(TestEvents.Hook("""{"run":1}"""));
        }

        using (var second = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            second.Append(TestEvents.Hook("""{"run":2}"""));
        }

        var stored = ForeignSqliteReader.Column(path, "SELECT payload_json FROM events ORDER BY id");

        Assert.Equal(["""{"run":1}""", """{"run":2}"""], stored);
    }

    /// <summary>Appending is append-only: nothing rewrites or removes an earlier row.</summary>
    [Fact]
    public void The_table_is_append_only()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            for (var i = 0; i < 25; i++)
            {
                store.Append(TestEvents.Hook($$"""{"n":{{i}}}"""));
            }
        }

        var ids = ForeignSqliteReader.Column(path, "SELECT id FROM events ORDER BY id")
            .Select(int.Parse)
            .ToList();

        Assert.Equal(25, ids.Count);
        Assert.Equal([.. Enumerable.Range(1, 25)], ids);
    }

    /// <summary>
    /// Text that would break a query if it were concatenated is stored verbatim.
    /// </summary>
    /// <remarks>
    /// Not a SQL-injection test in the web sense — the body comes from Claude Code on loopback.
    /// It is a test that the body is <em>bound</em>, and it would fail loudly if somebody ever
    /// built this statement by concatenation. Execution Plan Part 1: hook text is data.
    /// </remarks>
    [Theory]
    [InlineData("""{"prompt":"it's a quote'; DROP TABLE events; --"}""")]
    [InlineData("""{"prompt":"unicode: \u00e9\u4e2d\ud83d\ude00"}""")]
    [InlineData("""{"prompt":"line\nbreak\ttab"}""")]
    public void Text_that_would_break_a_concatenated_query_is_stored_verbatim(string body)
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            store.Append(TestEvents.Hook(body));
        }

        // The table still exists and holds exactly what was handed over.
        Assert.Equal(body, Assert.Single(ForeignSqliteReader.Column(path, "SELECT payload_json FROM events")));
    }

    // ---- What reaches the log ------------------------------------------------------------------

    /// <summary>
    /// <strong>The body never reaches the log, on the success path or the failure path.</strong>
    /// </summary>
    /// <remarks>
    /// The invariant the whole design turns on: the database has the operator's words, the log
    /// does not. This asserts against everything the sink received rather than against a
    /// particular line, so a log statement added later is covered by it without being edited in.
    /// </remarks>
    [Fact]
    public void No_log_line_ever_carries_the_body()
    {
        const string Secret = "REMEMBER-THE-MILK-AND-THE-PASSPHRASE";

        var log = new RecordingLogSink();
        var path = Db();

        using (var store = new SqliteEventStore(path, Logger(log)))
        {
            store.Append(TestEvents.Hook($$"""{"prompt":"{{Secret}}"}"""));
        }

        var everything = string.Join(
            "\n",
            log.Events.Select(entry => entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);

        // The control: the sink really did receive something, so the assertion above is not
        // passing because nothing was logged at all.
        Assert.NotEmpty(log.Events);

        // And the row really was written, so it is not passing because nothing happened.
        Assert.Single(ForeignSqliteReader.Column(path, "SELECT payload_json FROM events"));
    }

    /// <summary>Opening the file says so, and says what it will cost per day.</summary>
    /// <remarks>
    /// The success is logged as well as the failure. A recording feature silent when it works and
    /// silent when it fails leaves nobody able to tell "never started" from "started and wrote
    /// nothing" — the absence that has cost this project a diagnosis three times.
    /// </remarks>
    [Fact]
    public void Opening_the_database_is_announced_with_what_it_will_cost()
    {
        var log = new RecordingLogSink();

        using (var store = new SqliteEventStore(Db(), Logger(log)))
        {
            store.Append(TestEvents.Hook("{}"));
        }

        var line = Assert.Single(
            log.Events,
            entry => entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("Recording events to", StringComparison.Ordinal));

        var rendered = line.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("dashboard.db", rendered, StringComparison.Ordinal);
        Assert.Contains("not pruned", rendered, StringComparison.Ordinal);
        Assert.Contains(
            SqliteEventStore.TypicalBytesPerDay.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rendered,
            StringComparison.Ordinal);
    }

    // ---- A dead disk is not a dead dashboard ---------------------------------------------------

    /// <summary>An unusable path degrades to false and never throws.</summary>
    /// <remarks>
    /// This runs on the archive writer's thread. A throw here would take a hosted service down and
    /// turn "no history" into "a background service crashed", which is the inversion TS §IV.7
    /// forbids.
    /// </remarks>
    [Fact]
    public void A_path_that_cannot_be_opened_degrades_to_false()
    {
        var log = new RecordingLogSink();

        // A directory where a file must be: openable by nothing, on every machine, with no ACL
        // games and no reliance on a path that happens not to exist.
        var occupied = Path.Combine(_folder, "occupied.db");
        Directory.CreateDirectory(occupied);

        using var store = new SqliteEventStore(occupied, Logger(log));

        Assert.False(store.Append(TestEvents.Hook("""{"a":1}""")));
        Assert.False(store.Available);
    }

    /// <summary>It says why, exactly once, however many events follow.</summary>
    [Fact]
    public void A_failing_disk_is_reported_once_and_not_once_per_event()
    {
        var log = new RecordingLogSink();

        var occupied = Path.Combine(_folder, "occupied.db");
        Directory.CreateDirectory(occupied);

        using var store = new SqliteEventStore(occupied, Logger(log));

        for (var i = 0; i < 50; i++)
        {
            store.Append(TestEvents.Hook($$"""{"n":{{i}}}"""));
        }

        var complaints = log.Events
            .Where(entry => entry.Level >= LogEventLevel.Warning)
            .ToList();

        Assert.Single(complaints);
        Assert.Contains(
            "no history",
            complaints[0].RenderMessage(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        // Fifty attempts, one row lost per attempt — the count is what a later reader would use
        // to know how much went missing.
        Assert.Equal(1, store.FailedCount);
    }

    /// <summary>The failure message names the file, not the payload.</summary>
    [Fact]
    public void The_failure_message_names_the_file_and_not_the_body()
    {
        const string Secret = "DO-NOT-PRINT-ME";

        var log = new RecordingLogSink();

        var occupied = Path.Combine(_folder, "occupied.db");
        Directory.CreateDirectory(occupied);

        using var store = new SqliteEventStore(occupied, Logger(log));

        store.Append(TestEvents.Hook($$"""{"prompt":"{{Secret}}"}"""));

        var everything = string.Join(
            "\n",
            log.Events.Select(entry =>
                entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) +
                entry.Exception?.ToString()));

        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
        Assert.Contains("occupied.db", everything, StringComparison.Ordinal);
    }

    // ---- Wiring ---------------------------------------------------------------------------------

    /// <summary>The store writes where <c>DashboardPaths</c> says, so the home override moves it.</summary>
    [Fact]
    public void It_writes_under_the_dashboard_data_folder()
    {
        var paths = new DashboardPaths(_folder);

        using var store = new SqliteEventStore(paths, Serilog.Core.Logger.None);

        Assert.Equal(paths.DatabaseFile, store.Path);
        Assert.StartsWith(_folder, store.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void It_needs_its_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new SqliteEventStore((string)null!, Serilog.Core.Logger.None));
        Assert.Throws<ArgumentNullException>(() => new SqliteEventStore(Db(), null!));
        Assert.Throws<ArgumentNullException>(() => new SqliteEventStore((DashboardPaths)null!, Serilog.Core.Logger.None));
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var store = new SqliteEventStore(Db(), Serilog.Core.Logger.None);
        store.Append(TestEvents.Hook("{}"));

        store.Dispose();
        store.Dispose();

        // And it refuses politely afterwards rather than throwing on a disposed connection.
        Assert.False(store.Append(TestEvents.Hook("{}")));
    }

    /// <summary>Disposing releases the file, which connection pooling does not do by itself.</summary>
    /// <remarks>
    /// Measured at T1.17: a <c>File.Delete</c> straight after a <c>using</c> block failed with
    /// "used by another process", because <c>Microsoft.Data.Sqlite</c> pools connections. A
    /// resident app holding <c>dashboard.db</c> open for its whole life would block a backup or a
    /// copy, and the symptom would be somebody else's error message.
    /// </remarks>
    [Fact]
    public void Disposing_releases_the_file()
    {
        var path = Db();

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            store.Append(TestEvents.Hook("{}"));
        }

        // The assertion is that this does not throw.
        File.Delete(path);

        Assert.False(File.Exists(path));
    }
}
