using System.IO;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The one thread that touches the file (Impl Part 8; T1.17).
/// </summary>
public sealed class EventArchiveWriterTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public EventArchiveWriterTests() => Directory.CreateDirectory(_folder);

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

    /// <summary>A store that says no to everything, for the disk-is-dead paths.</summary>
    private sealed class RefusingStore : IEventStore
    {
        public int Attempts { get; private set; }

        public bool Append(InboundEvent inboundEvent)
        {
            Attempts++;

            return false;
        }
    }

    /// <summary>What the channel is given reaches the file.</summary>
    /// <remarks>
    /// End to end through the real channel, the real writer and the real store, checked by the
    /// foreign reader. This is the assertion that the parts are actually connected — each of them
    /// passing its own tests would not say that.
    /// </remarks>
    [Fact]
    public async Task Events_handed_to_the_archive_reach_the_file()
    {
        var path = Db();
        var archive = new EventArchive(Serilog.Core.Logger.None);

        using var store = new SqliteEventStore(path, Serilog.Core.Logger.None);
        var writer = new EventArchiveWriter(archive, store, Serilog.Core.Logger.None);

        await writer.StartAsync(CancellationToken.None);

        archive.TryArchive(TestEvents.Hook("""{"one":1}"""));
        archive.TryArchive(TestEvents.Hook("""{"two":2}"""));
        archive.Complete();

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["""{"one":1}""", """{"two":2}"""],
            ForeignSqliteReader.Column(path, "SELECT payload_json FROM events ORDER BY id"));
    }

    /// <summary>
    /// Events already handed over are written on the way out, not thrown away.
    /// </summary>
    /// <remarks>
    /// The end of a run is the part nearest whatever the operator was doing when they quit. A
    /// writer that abandoned its queue on cancellation would lose exactly that, and the loss would
    /// be invisible — the rows would simply not be there.
    /// </remarks>
    [Fact]
    public async Task Whatever_is_queued_at_shutdown_is_still_written()
    {
        var path = Db();
        var archive = new EventArchive(Serilog.Core.Logger.None);

        using var store = new SqliteEventStore(path, Serilog.Core.Logger.None);
        var writer = new EventArchiveWriter(archive, store, Serilog.Core.Logger.None);

        // Queued before the writer ever runs, then stopped immediately: the drain on the way out
        // is the only thing that can write these.
        for (var i = 0; i < 20; i++)
        {
            archive.TryArchive(TestEvents.Hook($$"""{"n":{{i}}}"""));
        }

        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(20, ForeignSqliteReader.Column(path, "SELECT id FROM events").Count);
    }

    /// <summary>A refusing store is counted, never logged per event.</summary>
    /// <remarks>
    /// The store has already said once why it cannot write. A line per lost row would bury that
    /// one line under thousands, in the situation where the operator most needs to read the log.
    /// </remarks>
    [Fact]
    public async Task A_store_that_refuses_is_counted_and_not_narrated()
    {
        var log = new RecordingLogSink();
        var archive = new EventArchive(Logger(log));
        var store = new RefusingStore();
        var writer = new EventArchiveWriter(archive, store, Logger(log));

        await writer.StartAsync(CancellationToken.None);

        for (var i = 0; i < 30; i++)
        {
            archive.TryArchive(TestEvents.Hook($$"""{"n":{{i}}}"""));
        }

        archive.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(30, store.Attempts);
        Assert.Equal(30, writer.RefusedCount);
        Assert.Equal(0, writer.WrittenCount);

        Assert.DoesNotContain(log.Events, entry => entry.Level >= LogEventLevel.Warning);
    }

    /// <summary>Starting and stopping is announced, with what happened in between.</summary>
    /// <remarks>
    /// <strong>The success as well as the failure.</strong> A recording feature that is silent
    /// when it works and silent when it fails cannot be told apart from one that never ran, which
    /// is the shape that has cost this project a diagnosis three times.
    /// </remarks>
    [Fact]
    public async Task The_writer_says_that_it_ran_and_what_it_did()
    {
        var log = new RecordingLogSink();
        var archive = new EventArchive(Logger(log));

        using var store = new SqliteEventStore(Db(), Logger(log));
        var writer = new EventArchiveWriter(archive, store, Logger(log));

        await writer.StartAsync(CancellationToken.None);
        archive.TryArchive(TestEvents.Hook("""{"a":1}"""));
        archive.Complete();
        await writer.StopAsync(CancellationToken.None);

        var lines = log.Events
            .Select(entry => entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.Contains(lines, line => line.Contains("Event archive writer started", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 written", StringComparison.Ordinal));
    }

    [Fact]
    public void It_needs_its_collaborators()
    {
        var archive = new EventArchive(Serilog.Core.Logger.None);
        var store = new RefusingStore();

        Assert.Throws<ArgumentNullException>(() => new EventArchiveWriter(null!, store, Serilog.Core.Logger.None));
        Assert.Throws<ArgumentNullException>(() => new EventArchiveWriter(archive, null!, Serilog.Core.Logger.None));
        Assert.Throws<ArgumentNullException>(() => new EventArchiveWriter(archive, store, null!));
    }
}
