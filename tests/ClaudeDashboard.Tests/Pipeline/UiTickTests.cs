using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Ui;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The wire that makes an age advance while nothing is happening (T1.9 → Impl §4 → T1.11).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is actually in doubt.</strong> <c>MainViewModel.Tick</c> was built and tested at
/// T1.10 and nothing called it, so every age test passed while a session blocked for nine minutes
/// read "9 min" for the rest of the afternoon. The claim under test is therefore not "Tick
/// updates ages" — that is T1.10's — but "the running consumer causes it to happen, with no
/// event arriving". These tests drive the real <see cref="EventConsumer"/> and never post a
/// second event.
/// </para>
/// <para>
/// This is also why there is no timer here: a test that started its own would prove nothing about
/// the process, which is allowed exactly one periodic loop.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class UiTickTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly QueueingDispatcher _dispatcher = new();

    private SessionProjection _projection = null!;
    private MainViewModel _viewModel = null!;
    private UiTick _tick = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _projection = new SessionProjection(_registry, _dispatcher);
        _viewModel = new MainViewModel(_projection, new MotionPolicy(() => false, observeChanges: false), new StubAckPublisher());
        _tick = new UiTick(_dispatcher);

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            Logger.None,
            tickInterval: TimeSpan.FromMilliseconds(25),
            uiTick: _tick);

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _viewModel?.Dispose();
        _projection?.Dispose();
        return Task.CompletedTask;
    }

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

    /// <summary>Applies an event directly, as the consumer would, and drains the marshalling queue.</summary>
    private SessionViewModel GivenABlockedSession()
    {
        _registry.Apply(new Core.Events.UserPromptSubmit
        {
            SessionId = new SessionId("blocked"),
            Timestamp = At,
            Cwd = @"C:\dev\PennCustQuote",
            PromptId = "p-1",
            Prompt = "run the tests",
        });

        _registry.Apply(new Core.Events.Notification
        {
            SessionId = new SessionId("blocked"),
            Timestamp = At,
            Cwd = @"C:\dev\PennCustQuote",
            NotificationType = "permission_prompt",
        });

        _dispatcher.Pump();
        return _viewModel.Rows.OfType<SessionViewModel>().Single();
    }

    /// <summary>
    /// <strong>The whole point of the wiring.</strong> Nothing is posted to the channel; the
    /// clock simply moves, and the row's age moves with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The tick is driven here rather than waited for, and that is the fix for a real
    /// intermittent failure.</strong> This used to wait on <c>DeliveredCount &gt; 0</c> and then
    /// assert what the delivered tick carried. Those are different facts: the counter says a tick
    /// was posted, not that a tick carrying <em>this</em> clock value was posted. The consumer
    /// free-runs at 25ms from the moment the fixture is built, so a tick fired between
    /// <c>Attach</c> and the clock moving satisfies the counter while carrying the old time, and
    /// <c>Pump</c> then runs a closure that ages the row to zero.
    /// </para>
    /// <para>
    /// It is asserted the other way round now: the tick is delivered explicitly, at an instant
    /// this test chose, so there is nothing to wait for and no window to lose. That the
    /// <em>consumer</em> produces ticks at all is a separate fact and is asserted separately, by
    /// <see cref="The_consumers_loop_is_what_delivers_ticks_to_the_ui"/> — where it can be
    /// asserted without any clock value riding on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_age_advances_with_no_event_arriving()
    {
        var row = GivenABlockedSession();
        _tick.Attach(_viewModel);

        Assert.Equal(TimeSpan.Zero, row.Age);

        _clock.Now = At.AddMinutes(9);
        _tick.Tick(_clock.Now);
        _dispatcher.Pump();

        Assert.Equal(TimeSpan.FromMinutes(9), row.Age);
        Assert.Equal("waiting 9 min", row.AgeText);

        // …and it keeps moving, which a one-off would not.
        _clock.Now = At.AddMinutes(10);
        _tick.Tick(_clock.Now);
        _dispatcher.Pump();

        Assert.Equal("waiting 10 min", row.AgeText);
    }

    /// <summary>
    /// A tick carries the instant it was <em>raised</em> at, not the instant it is run at — and a
    /// later tick supersedes an earlier one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the behaviour the intermittent failure turned on, and it is correct.</strong>
    /// <see cref="UiTick.Tick"/> captures <c>now</c> when it posts, deliberately: one tick means
    /// "the clock reached this instant", and every row it updates must agree about when now was.
    /// A tick raised before the clock moved therefore carries the old instant however late it is
    /// pumped — which is right, and is exactly why a test may not use "a tick was delivered" as a
    /// stand-in for "a tick carrying this time was delivered". Pinned here so the capture is
    /// intended rather than incidental, and so anyone tempted to re-read the clock inside the
    /// posted closure has to argue with a test first.
    /// </para>
    /// <para>
    /// The consumer is stopped for this one. It is the only test here that asserts a negative
    /// <em>after</em> draining the queue, and a stray tick carrying the new time would land in
    /// that drain — the free-running producer is precisely what has to be absent for the
    /// assertion to mean what it says.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_tick_carries_the_instant_it_was_raised_at()
    {
        var row = GivenABlockedSession();

        await _consumer.StopAsync(CancellationToken.None);
        _dispatcher.Pump();

        _tick.Attach(_viewModel);

        // Raised before the clock moves, so it carries the old instant.
        _tick.Tick(_clock.Now);

        _clock.Now = At.AddMinutes(4);
        _dispatcher.Pump();

        Assert.Equal(TimeSpan.Zero, row.Age);

        // …and the next tick supersedes it.
        _tick.Tick(_clock.Now);
        _dispatcher.Pump();

        Assert.Equal(TimeSpan.FromMinutes(4), row.Age);
    }

    /// <summary>
    /// The consumer's own loop is what delivers ticks to the UI — nothing else drives it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half the other tests used to carry implicitly and raced on. Here it is the
    /// only claim: a tick was delivered because the consumer produced one. <strong>No clock value
    /// rides on it</strong>, so there is no stale-capture hazard — the counter is being asked
    /// exactly the question it can answer.
    /// </para>
    /// <para>
    /// It matters on its own: T1.6 shipped a tick nobody drove, and the failure mode is a
    /// dashboard whose ages, quiet-group collapse and tray tooltip all quietly stop moving while
    /// every other test stays green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_consumers_loop_is_what_delivers_ticks_to_the_ui()
    {
        GivenABlockedSession();
        _tick.Attach(_viewModel);

        Assert.True(
            await Until(() => _tick.DeliveredCount > 0),
            "the consumer never ticked the UI");

        Assert.True(_dispatcher.PostedCount > 0, "the tick was counted but never posted");
    }

    /// <summary>
    /// Staleness is the same clock asking a different question, so it must ride the same tick —
    /// a second timer for the collapse rules would be the mistake T1.9's single loop prevents.
    /// </summary>
    /// <remarks>
    /// Driven rather than waited for, for the reason given on
    /// <see cref="An_age_advances_with_no_event_arriving"/> — and here it also makes "the same
    /// tick" literal rather than approximate: one <see cref="UiTick.Tick"/> call, and both the
    /// age and the staleness move on it.
    /// </remarks>
    [Fact]
    public void A_group_goes_stale_on_the_same_tick()
    {
        _registry.Apply(new Core.Events.SessionStart
        {
            SessionId = new SessionId("quiet"),
            Timestamp = At,
            Cwd = @"C:\dev\PennCustQuote",
            Source = "startup",
        });

        _dispatcher.Pump();
        _tick.Attach(_viewModel);

        var header = _viewModel.Rows.OfType<GroupViewModel>().Single();
        Assert.False(header.IsStale);

        _clock.Now = At + MainViewModel.DefaultStaleAfter;
        _tick.Tick(_clock.Now);
        _dispatcher.Pump();

        Assert.True(header.IsStale);
    }

    /// <summary>
    /// The tick posts and returns. It must not run the view model's work on the consumer thread,
    /// which owns the Registry and would be blocked behind a render.
    /// </summary>
    /// <remarks>
    /// <strong>This is the test that failed intermittently, and the reason was the wait.</strong>
    /// It waited on <c>DeliveredCount &gt; 0</c>, which counts posts and says nothing about what
    /// they carry. A consumer tick landing between <c>Attach</c> and the clock moving satisfied
    /// it while carrying the old instant, and with no later tick before <c>Pump</c> the row aged
    /// to zero and the last assertion failed. Reproduced deterministically by forcing exactly
    /// that interleaving; see the commit.
    /// </remarks>
    [Fact]
    public void The_tick_is_posted_rather_than_run_on_the_consumer_thread()
    {
        var row = GivenABlockedSession();
        _tick.Attach(_viewModel);
        _clock.Now = At.AddMinutes(4);

        // One tick, at an instant this test chose. A stray consumer tick cannot change either
        // assertion below: it can only carry a time at or after this one, and it still has to be
        // pumped before it can run at all.
        _tick.Tick(_clock.Now);

        // Delivered, but not yet run: the age only moves when this thread drains the queue.
        Assert.Equal(TimeSpan.Zero, row.Age);

        _dispatcher.Pump();
        Assert.Equal(TimeSpan.FromMinutes(4), row.Age);
    }

    /// <summary>
    /// Before there is a window there is nothing to age, and the tick is dropped rather than
    /// queued — the dashboard starts headless (T1.7).
    /// </summary>
    [Fact]
    public async Task A_tick_with_no_view_model_attached_goes_nowhere()
    {
        _clock.Now = At.AddMinutes(3);

        Assert.True(await Until(() => _consumer.TickCount > 0), "the consumer never ticked");

        Assert.Equal(0, _tick.DeliveredCount);
        Assert.Equal(0, _dispatcher.PostedCount);
    }

    /// <summary>
    /// The nudge schedule and the UI clock are independent: a UI that throws must not stop
    /// nudges, and the consumer must not stop consuming either.
    /// </summary>
    [Fact]
    public async Task A_failing_ui_tick_does_not_stop_the_nudge_schedule()
    {
        _consumer.Dispose();

        var failing = new ThrowingTick();
        using var consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            Logger.None,
            tickInterval: TimeSpan.FromMilliseconds(25),
            uiTick: failing);

        await consumer.StartAsync(CancellationToken.None);

        Assert.True(await Until(() => failing.Calls >= 3), "the tick stopped after it threw");
        Assert.True(consumer.TickCount >= 3);

        await consumer.StopAsync(CancellationToken.None);
    }

    private sealed class ThrowingTick : IUiTick
    {
        public int Calls { get; private set; }

        public void Tick(DateTimeOffset now)
        {
            Calls++;
            throw new InvalidOperationException("the UI is on fire");
        }
    }
}
