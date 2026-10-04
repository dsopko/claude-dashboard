using System.Diagnostics;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;
using Xunit.Abstractions;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The bound that keeps issue #3 out of reach, asserted: the consumer clears a full queue of
/// state-changing events quickly (T1.58).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A full queue only happens if the consumer stops keeping up.
/// Nothing asserted that it does; #3's figures measured the cheapest path, a decline. This drives
/// the real consumer on the expensive path: each event applies, raises <c>SessionChanged</c>,
/// runs the sound policy, records its decisions and is posted to the projection. It fails if
/// someone later puts blocking work on the consumer loop, which is how #3 becomes reachable.
/// </para>
/// <para>
/// <strong>The limit is generous on purpose.</strong> Measured on the development box (T1.58): see
/// <see cref="Limit"/>. A test that guards a bound must not fail for a busy machine, and a stall
/// worth catching, such as ten milliseconds of blocking work for each event, costs ten seconds for
/// a full queue, far past the limit.
/// </para>
/// </remarks>
public sealed class QueueThroughputTests(ITestOutputHelper output)
{
    /// <summary>
    /// The longest a full queue of state-changing events may take to clear. Measured on the
    /// development box (T1.58) at about 31 ms, 30.2 to 32.0 ms over three runs; this is about 160 times
    /// that, well over the fifty times the plan asks for.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_full_queue_of_state_changing_events_clears_within_the_limit()
    {
        var clock = new FakeClock();
        var guard = new SingleWriterGuard();
        var registry = new SessionRegistry(new SingleWriterGuard());
        var pipeline = new EventPipeline(Logger.None, clock: clock);
        var archive = new EventArchive(Logger.None, capacity: 4 * EventPipeline.DefaultCapacity);
        var rosters = new RosterStore(new RecordingEventSink());
        var recorder = new DecisionRecorder(registry, rosters, archive, Logger.None);
        var engine = new SoundPolicyEngine(new RecordingSoundPlayer(), clock, guard, new SoundPolicyOptions(), recorder);
        var dispatcher = new QueueingDispatcher();
        using var projection = new SessionProjection(registry, dispatcher);

        registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, rosters.Book));

        // Half the queue starts sessions, half blocks them on a permission prompt: every event is
        // applied, and every permission plays its notice through the engine.
        var sessions = EventPipeline.DefaultCapacity / 2;

        for (var i = 0; i < sessions; i++)
        {
            Assert.True(pipeline.Sink.TryPublish(new UserPromptSubmit
            {
                SessionId = new SessionId($"s-{i}"), Timestamp = clock.Now, Cwd = $@"C:\work\{i % 15}", PromptId = $"p-{i}", Prompt = "go",
            }));
        }

        for (var i = 0; i < sessions; i++)
        {
            Assert.True(pipeline.Sink.TryPublish(new Notification
            {
                SessionId = new SessionId($"s-{i}"), Timestamp = clock.Now, Cwd = $@"C:\work\{i % 15}", NotificationType = "permission_prompt",
            }));
        }

        Assert.Equal(0, pipeline.ShedCount);

        // The timings as the product wires them (T1.66): queue wait, apply time and the archive backlog
        // are measured on this path, so their cost is inside the bound.
        var timings = new Timings(Logger.None);
        archive.Backlog = timings.ArchiveBacklog;

        using var consumer = new EventConsumer(
            pipeline,
            registry,
            engine,
            clock,
            guard,
            Logger.None,
            new RecordingUiTick(),
            archive,
            rosters,
            recorder,
            tickInterval: TimeSpan.FromHours(1),
            silenceThreshold: TimeSpan.FromHours(1),
            health: new HealthBoard(new HealthSources(), clock, Logger.None, timings));

        var watch = Stopwatch.StartNew();
        await consumer.StartAsync(CancellationToken.None);

        var cleared = SpinWait.SpinUntil(
            () => consumer.AppliedCount >= EventPipeline.DefaultCapacity,
            Limit + TimeSpan.FromSeconds(30));

        watch.Stop();
        await consumer.StopAsync(CancellationToken.None);

        output.WriteLine($"Cleared {consumer.AppliedCount} state-changing events in {watch.Elapsed.TotalMilliseconds:F1} ms (limit {Limit.TotalMilliseconds} ms).");

        Assert.True(cleared, $"The consumer applied only {consumer.AppliedCount} of {EventPipeline.DefaultCapacity}.");
        Assert.True(
            watch.Elapsed < Limit,
            $"A full queue of {EventPipeline.DefaultCapacity} state-changing events took {watch.Elapsed.TotalMilliseconds:F0} ms; " +
            $"the limit is {Limit.TotalMilliseconds} ms. Something on the consumer loop now blocks, and a full queue (#3) is reachable.");
        Assert.True(dispatcher.PostedCount > 0, "Nothing reached the projection.");
        Assert.Equal(registry.Sessions.Count, sessions);
    }
}
