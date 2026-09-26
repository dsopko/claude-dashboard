using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// <strong>An Apply that throws still archives its event</strong>, inside a running consumer
/// (T1.37 review, must-fix 1).
/// </summary>
/// <remarks>
/// <para>
/// Before T1.37 the event was handed to the archive before <c>Apply</c> ran, so a throwing
/// <c>Apply</c> could not lose it — the guarantee was structural. Since T1.37 the hand-off
/// happens after the decisions are known, and the guarantee rests on one <c>finally</c> in
/// <c>EventConsumer.Apply</c>. This is the test that sees that <c>finally</c>: the recorder-level
/// test re-enacts the scope by hand and would pass with it gone.
/// </para>
/// <para>
/// <strong>How the Registry is made to throw.</strong> The Registry here has its own guard,
/// separate from the consumer's, and a dedicated thread holds it. The consumer enters its own
/// guard, opens the scope, and calls <c>Registry.Apply</c>, which throws
/// <see cref="SingleWriterViolationException"/> — the real exception on the real path, which the
/// consumer's outer catch already handles. A dedicated thread rather than the test's own,
/// because the guard is re-entrant by thread id and a pool thread could turn out to be the
/// consumer's.
/// </para>
/// <para>
/// Plant: replacing the <c>finally</c> that calls <c>_recorder.Complete()</c> with
/// <c>catch { throw; }</c> followed by <c>Complete()</c> fails this test — no record arrives.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class ApplyFailedArchiveTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _consumerGuard = new();
    private readonly SingleWriterGuard _registryGuard = new();
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly ManualResetEventSlim _held = new();
    private readonly ManualResetEventSlim _release = new();

    private SessionRegistry _registry = null!;
    private EventConsumer _consumer = null!;
    private Thread _holder = null!;

    public Task InitializeAsync()
    {
        _registry = new SessionRegistry(_registryGuard);

        var recorder = new DecisionRecorder(_registry, _rosters, _archive, Logger.None);

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _consumerGuard, new SoundPolicyOptions(), recorder),
            _clock,
            _consumerGuard,
            Logger.None,
            new RecordingUiTick(),
            _archive,
            _rosters,
            recorder: recorder,

            // Long enough that no tick runs during the test: a tick's sweep would hit the held
            // guard too, and its record would only be noise here.
            tickInterval: TimeSpan.FromHours(1));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _release.Set();
        _holder?.Join(TimeSpan.FromSeconds(5));
        _consumer?.Dispose();
        _held.Dispose();
        _release.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_throwing_apply_archives_the_event_with_an_apply_failed_row()
    {
        _holder = new Thread(() =>
        {
            using (_registryGuard.Enter("a plant holding the Registry's guard"))
            {
                _held.Set();
                _release.Wait();
            }
        })
        {
            IsBackground = true,
        };

        _holder.Start();
        Assert.True(_held.Wait(TimeSpan.FromSeconds(5)));

        var prompt = new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = _clock.Now,
            Cwd = @"C:\projects\dashboard",
            Prompt = "go",
            Payload = new PayloadJson("""{"hook_event_name":"UserPromptSubmit"}"""),
        };

        Assert.True(_pipeline.Sink.TryPublish(prompt));

        var record = await NextRecord();

        Assert.Same(prompt, record.Event);

        var failed = Assert.Single(record.Decisions);
        Assert.Equal(DecisionKind.ApplyFailed, failed.Kind);
        Assert.Equal("s-1", failed.SessionId);
        Assert.Equal(nameof(SingleWriterViolationException), failed.Reason);

        // The Registry really did refuse it, and the consumer is still running.
        Assert.Empty(_registry.Sessions);
        Assert.True(_registryGuard.ViolationCount >= 1);
    }

    private async Task<ArchiveRecord> NextRecord()
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (_archive.Reader.TryRead(out var record))
            {
                return record;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException("No record arrived: the throwing Apply archived nothing.");
    }
}
