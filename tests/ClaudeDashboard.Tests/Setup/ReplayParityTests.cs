using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Storage;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// <strong>The live recorder and <c>--replay</c> produce identical decisions rows for the same
/// events</strong> — T1.37's parity acceptance, asserted row for row.
/// </summary>
/// <remarks>
/// <para>
/// One fixture, two paths. The live half runs raw hook bodies through the real mapper, the real
/// pipeline, the real consumer and the real store — the product's own path, not a re-enactment
/// of it. The replay half is handed a copy of the resulting database with its decisions deleted,
/// and must rebuild them: same kinds, same fields, same <c>event_id</c>s, same order.
/// </para>
/// <para>
/// This equality is the whole warrant for trusting a replayed record. It holds by construction —
/// replay drives the same registry, engine and recorder — and this test is where the
/// construction is made to prove it on disk.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class ReplayParityTests : IAsyncLifetime
{
    /// <summary>The working directory as JSON carries it, and as the domain sees it.</summary>
    private const string Cwd = @"C:\\projects\\dashboard";
    private const string PlainCwd = @"C:\projects\dashboard";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RosterStore _rosters = new(new RecordingEventSink());

    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);

        var recorder = new DecisionRecorder(_registry, _rosters, _archive, Logger.None);

        var engine = new SoundPolicyEngine(
            new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions(), recorder);

        _registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            engine,
            _clock,
            _guard,
            Logger.None,
            new RecordingUiTick(),
            _archive,
            _rosters,
            recorder: recorder,
            tickInterval: TimeSpan.FromMilliseconds(20),
            silenceThreshold: TimeSpan.FromMinutes(10));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_live_record_and_the_replayed_record_are_identical_row_for_row()
    {
        var livePath = Path.Combine(_folder, "live.db");
        var mapper = new HookEventMapper(_clock);
        var start = FakeClock.DefaultStart;

        // The fixture: a session's whole life, ending on a stale straggler. Raw bodies, so the
        // live path enters through the same door ingress uses and replay reads the same bytes.
        (DateTimeOffset At, string Body)[] fixture =
        [
            (start, $$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-1","cwd":"{{Cwd}}","prompt_id":"p-1","prompt":"go"}"""),
            (start + TimeSpan.FromSeconds(10), $$"""{"hook_event_name":"Notification","session_id":"s-1","cwd":"{{Cwd}}","notification_type":"permission_prompt"}"""),
            (start + TimeSpan.FromSeconds(20), $$"""{"hook_event_name":"Stop","session_id":"s-1","cwd":"{{Cwd}}","last_assistant_message":"done"}"""),
        ];

        using (var store = new SqliteEventStore(livePath, Logger.None))
        {
            foreach (var (at, body) in fixture)
            {
                _clock.Now = at;

                var payload = JsonSerializer.Deserialize<HookPayload>(body)!;
                var mapping = mapper.Map(payload, new PayloadJson(body));

                Assert.True(mapping.Mapped);
                await PublishAndPersist(store, mapping.Event!);
            }

            // The operator acknowledges; the Ack is archived since T1.37, and replays as Manual
            // because its source was never archived — which this ack already is.
            _clock.Now = start + TimeSpan.FromSeconds(30);
            await PublishAndPersist(store, new Ack
            {
                SessionId = new SessionId("s-1"),
                Timestamp = _clock.Now,
                Cwd = PlainCwd,
                Source = AckSource.Manual,
            });

            // And a straggler from before the session's last-seen: declined live, declined the
            // same way on replay.
            _clock.Now = start - TimeSpan.FromMinutes(5);
            var stale = JsonSerializer.Deserialize<HookPayload>(fixture[1].Body)!;
            var staleMapping = mapper.Map(stale, new PayloadJson(fixture[1].Body));
            await PublishAndPersist(store, staleMapping.Event!);
        }

        var replayedPath = Path.Combine(_folder, "replayed.db");
        File.Copy(livePath, replayedPath);

        using (var wipe = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={replayedPath};Pooling=False"))
        {
            wipe.Open();
            using var delete = wipe.CreateCommand();
            delete.CommandText = "DELETE FROM decisions;";
            delete.ExecuteNonQuery();
        }

        var reported = new List<string>();

        Assert.Equal(0, ReplaySwitch.Run(replayedPath, reported.Add, Logger.None));

        const string rows =
            "SELECT IFNULL(event_id, 'NULL'), ts, IFNULL(session_id, 'NULL'), kind, " +
            "IFNULL(from_state, 'NULL'), IFNULL(to_state, 'NULL'), IFNULL(reason, 'NULL'), " +
            "IFNULL(detail, 'NULL') FROM decisions ORDER BY id";

        var live = ForeignSqliteReader.Query(livePath, rows);
        var replayed = ForeignSqliteReader.Query(replayedPath, rows);

        Assert.NotEmpty(live);
        Assert.Equal(live.Count, replayed.Count);

        for (var i = 0; i < live.Count; i++)
        {
            Assert.Equal(live[i], replayed[i]);
        }

        // And replay never touched events.
        Assert.Equal(
            ForeignSqliteReader.Query(livePath, "SELECT id, session_id, ts, event_type, payload_json FROM events ORDER BY id"),
            ForeignSqliteReader.Query(replayedPath, "SELECT id, session_id, ts, event_type, payload_json FROM events ORDER BY id"));

        Assert.Contains(reported, line => line.Contains("Replayed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Publishes the event, waits for the consumer's record, and persists it the way the
    /// writer does — the same <c>store.Append(record)</c>, one transaction.
    /// </summary>
    private async Task PublishAndPersist(SqliteEventStore store, InboundEvent inboundEvent)
    {
        Assert.True(_pipeline.Sink.TryPublish(inboundEvent));

        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (_archive.Reader.TryRead(out var record))
            {
                Assert.True(store.Append(record));
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException($"No record arrived for {inboundEvent.HookEventName}.");
    }
}
