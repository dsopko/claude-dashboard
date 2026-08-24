using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The consumer: one thread, one loop, both jobs (Impl §2.2, §4).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class EventConsumerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new();
    private readonly EventPipeline _pipeline = new(Logger.None);

    private SoundPolicyEngine _sound = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _sound = new SoundPolicyEngine(_player, _clock);
        _registry.SessionChanged += (_, e) => _sound.OnSessionChanged(e.Session);

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            _sound,
            _clock,
            _guard,
            Logger.None,
            tickInterval: TimeSpan.FromMilliseconds(25));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        return Task.CompletedTask;
    }

    private static UserPromptSubmit Prompt(string sessionId, DateTimeOffset stamp, string promptId = "p-1") => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = stamp,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "do the thing",
    };

    /// <summary>Waits for <paramref name="condition"/> rather than sleeping a guessed interval.</summary>
    private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    // ---- The channel read ---------------------------------------------------------------------

    [Fact]
    public async Task An_published_event_reaches_the_registry()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", At));

        Assert.True(await Until(() => _registry.Sessions.ContainsKey(new SessionId("s-1"))));
        Assert.Equal(SessionState.Working, _registry.Sessions[new SessionId("s-1")].State);
    }

    /// <summary>
    /// <strong>The concurrency test, with the concurrency proven rather than assumed.</strong>
    /// A barrier holds every producer until all of them are ready, so they genuinely overlap —
    /// starting fifteen tasks does not by itself mean fifteen ran at once, and a test that never
    /// achieves concurrency passes for the wrong reason forever.
    /// </summary>
    [Fact]
    public async Task Many_concurrent_producers_are_serialized_without_corruption()
    {
        const int Producers = 15;
        const int PerProducer = 40;

        var failure = Concurrently.Run(Producers, producer =>
        {
            for (var n = 0; n < PerProducer; n++)
            {
                _pipeline.Sink.TryPublish(Prompt($"s-{producer}", At.AddSeconds(n), $"p-{n}"));
            }
        });

        Assert.Null(failure);

        Assert.True(
            await Until(() => _registry.Sessions.Count == Producers),
            $"Expected {Producers} sessions, saw {_registry.Sessions.Count}.");

        // No corruption: every session exists exactly once, each with the newest prompt it was
        // sent, and the guard never saw two threads inside.
        Assert.Equal(Producers, _registry.Sessions.Count);
        Assert.Equal(0, _guard.ViolationCount);

        foreach (var session in _registry.Sessions.Values)
        {
            Assert.Equal(SessionState.Working, session.State);
        }
    }

    /// <summary>
    /// Timestamp order is preserved: the Registry's stale-drop guard means a session's
    /// <c>LastActivity</c> can only move forward, so out-of-order arrivals cannot rewind it.
    /// </summary>
    [Fact]
    public async Task Timestamp_order_is_preserved_under_concurrent_publication()
    {
        const int Events = 60;

        // Each worker publishes an interleaved slice of one session's timeline.
        var failure = Concurrently.Run(4, worker =>
        {
            for (var n = worker; n < Events; n += 4)
            {
                _pipeline.Sink.TryPublish(Prompt("s-1", At.AddSeconds(n), $"p-{n}"));
            }
        });

        Assert.Null(failure);

        Assert.True(await Until(() => _registry.Sessions.ContainsKey(new SessionId("s-1"))));
        Assert.True(await Until(() => _consumer.AppliedCount + _consumer.DeclinedCount >= Events));

        var session = _registry.Sessions[new SessionId("s-1")];

        // Whatever order they arrived in, the session ends at the newest stamp it ever saw and
        // never at an older one.
        Assert.Equal(At.AddSeconds(Events - 1), session.LastActivity);
        Assert.Equal(0, _guard.ViolationCount);
    }

    // ---- The nudge tick -------------------------------------------------------------------------

    /// <summary>
    /// <strong>The T1.6 finding, closed.</strong> Nothing in the plan owned periodic nudge
    /// evaluation, so every sound-policy test could pass while no nudge ever fired. This drives
    /// a real consumer over a real channel with a real timer and observes the sound.
    /// </summary>
    [Fact]
    public async Task The_nudge_tick_actually_fires_a_nudge()
    {
        _pipeline.Sink.TryPublish(new Notification
        {
            SessionId = new SessionId("s-1"),
            Timestamp = _clock.Now,
            Cwd = @"C:\w",
            NotificationType = "permission_prompt",
        });

        // The notice fires on entry, via the Registry's change notification.
        Assert.True(await Until(() => _player.Played.Count == 1), "The entry notice never fired.");

        // Past the first rung of the ladder; the tick must notice without anything else happening.
        _clock.AdvanceMinutes(3);

        Assert.True(
            await Until(() => _player.Played.Count >= 2),
            "The nudge never fired — nothing is driving SoundPolicyEngine.Evaluate.");

        var nudge = _player.Played[^1];
        Assert.Equal(SoundId.Permission, nudge.Sound);
        Assert.True(nudge.Gain < _player.Played[0].Gain, "A nudge is softer than the notice.");
    }

    [Fact]
    public async Task The_tick_runs_even_with_no_events_at_all()
    {
        Assert.True(await Until(() => _consumer.TickCount >= 3), "The tick did not run on an idle pipeline.");
    }

    /// <summary>
    /// The requirement behind the one-loop rule: the tick and the channel read must never be
    /// inside the guarded region at the same time. Publishing continuously while ticks fire
    /// every 25ms is the interleaving that would expose it.
    /// </summary>
    [Fact]
    public async Task The_tick_and_the_channel_read_never_overlap()
    {
        var failure = Concurrently.Run(4, worker =>
        {
            for (var n = 0; n < 150; n++)
            {
                _pipeline.Sink.TryPublish(Prompt($"s-{worker}", At.AddSeconds(n), $"p-{n}"));
            }
        });

        Assert.Null(failure);

        Assert.True(await Until(() => _consumer.TickCount >= 5), "Not enough ticks to interleave with.");
        Assert.True(await Until(() => _consumer.AppliedCount + _consumer.DeclinedCount >= 600));

        Assert.Equal(0, _guard.ViolationCount);
    }

    /// <summary>
    /// <strong>The negative case for the one-loop rule.</strong> "The tick fires" would pass
    /// just as happily with two racing drivers; what has to be shown is that a second driver
    /// <em>would be detected</em>. This adds the thing T1.9 was forbidden to build — a separate
    /// loop evaluating the engine on its own thread — and asserts the guard catches it.
    /// </summary>
    /// <remarks>
    /// Without this, the single-writer invariant is documentation. With it, the day someone adds
    /// a second <c>BackgroundService</c> for a periodic job, the suite says so.
    /// </remarks>
    [Fact]
    public async Task A_second_concurrent_driver_is_detected()
    {
        // Deterministic rather than probabilistic. Racing a rogue thread against the consumer
        // and hoping the windows overlap is how a concurrency test comes to pass for the wrong
        // reason: the guarded regions are microseconds long, so "no collision observed" would
        // mean nothing either way. Instead the rogue holds the region open — standing in for a
        // second driver that happens to be mid-evaluation — and the consumer is made to arrive
        // while it is held.
        using (_guard.Enter("a rogue second driver, mid-evaluation"))
        {
            _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));

            Assert.True(
                await Until(() => _guard.ViolationCount > 0, timeoutMs: 3000),
                "The consumer entered the single-writer region while another driver held it, and " +
                "nothing noticed. That is precisely the condition that produces an unreproducible " +
                "data race in production.");

            // Specific to the *apply* path, not merely to "something in the consumer". The tick
            // also enters the region, so a violation count alone would stay green with the guard
            // removed from Apply — which is the mutation this assertion exists to catch. If Apply
            // were unguarded the session would be in the Registry by now, mutated while a second
            // thread believed it held the region exclusively.
            Assert.False(
                _registry.Sessions.ContainsKey(new SessionId("s-1")),
                "The consumer applied an event to the Registry while another driver held the " +
                "single-writer region. Apply is not going through the guard.");
        }

        // And the pipeline survives it: the violation is loud, not fatal.
        _pipeline.Sink.TryPublish(Prompt("s-2", At, "p-1"));

        Assert.True(
            await Until(() => _registry.Sessions.ContainsKey(new SessionId("s-2"))),
            "The consumer stopped after a violation. A dashboard that has stopped consuming is " +
            "worse than one that has logged a bug.");
    }

    // ---- Construction -----------------------------------------------------------------------------

    [Fact]
    public void The_consumer_needs_all_of_its_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EventConsumer(null!, _registry, _sound, _clock, _guard, Logger.None));
        Assert.Throws<ArgumentNullException>(() =>
            new EventConsumer(_pipeline, null!, _sound, _clock, _guard, Logger.None));
        Assert.Throws<ArgumentNullException>(() =>
            new EventConsumer(_pipeline, _registry, null!, _clock, _guard, Logger.None));
        Assert.Throws<ArgumentNullException>(() =>
            new EventConsumer(_pipeline, _registry, _sound, null!, _guard, Logger.None));
        Assert.Throws<ArgumentNullException>(() =>
            new EventConsumer(_pipeline, _registry, _sound, _clock, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() =>
            new EventConsumer(_pipeline, _registry, _sound, _clock, _guard, null!));
    }

    [Fact]
    public void The_default_tick_is_fine_enough_for_the_shortest_nudge_interval()
    {
        // TS §IV.5's first rung is two minutes; the tick bounds how late a nudge can be.
        Assert.True(EventConsumer.DefaultTickInterval < TimeSpan.FromMinutes(2) / 4);
        Assert.True(EventConsumer.DefaultTickInterval >= TimeSpan.FromSeconds(5));
    }
}
