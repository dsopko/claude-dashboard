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
    /// The tick posts and returns. It must not run the view model's work on the caller's thread,
    /// which in production is the consumer thread — it owns the Registry and would be blocked
    /// behind a render.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This test failed intermittently twice, and the second time the comment was the
    /// bug.</strong> It read: "a stray consumer tick cannot change either assertion below: it can
    /// only carry a time at or after this one". That is a capability claim, nothing tested it, and
    /// it is false. <see cref="UiTick.Tick"/> reads the instant it is given and then posts, so the
    /// consumer can read the clock <em>before</em> this test moves it and post <em>after</em> this
    /// test's own tick. The queue then ends with a stale instant, the pump runs it last, and the
    /// row's age is zero when four minutes was expected. Reproduced deterministically by forcing
    /// exactly that order — see <see cref="The_last_tick_drained_wins_even_when_it_carries_an_earlier_instant"/>,
    /// which now asserts that behaviour instead of denying it.
    /// </para>
    /// <para>
    /// <strong>The fix is to stop manufacturing a second caller.</strong> In production
    /// <c>EventConsumer</c> is the only thing that calls <see cref="IUiTick.Tick"/>, on one thread,
    /// so posts arrive in the order their instants were read and a stale one cannot overtake a
    /// fresh one. The old test attached the view model to the <em>consumer's</em> tick and then
    /// ticked it by hand as well — two callers, an interleaving the product cannot have, and an
    /// assertion that it would not occur. This test uses a tick of its own; the consumer's has no
    /// target and therefore posts nothing at all.
    /// </para>
    /// <para>
    /// That the running consumer really does drive the view model is a different claim, and it is
    /// covered by the tests above, which are the ones that must keep a live consumer.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_tick_is_posted_rather_than_run_on_the_callers_thread()
    {
        var row = GivenABlockedSession();

        // Deliberately not the fixture's _tick. That one belongs to the running consumer, and
        // leaving it with no target is what makes this test free of stray posts by construction
        // rather than by timing.
        var tick = new UiTick(_dispatcher);
        tick.Attach(_viewModel);

        tick.Tick(At.AddMinutes(4));

        // Delivered, but not yet run: the age only moves when this thread drains the queue.
        Assert.Equal(TimeSpan.Zero, row.Age);
        Assert.Equal(1, _tick.DeliveredCount + tick.DeliveredCount);

        _dispatcher.Pump();
        Assert.Equal(TimeSpan.FromMinutes(4), row.Age);
    }

    /// <summary>
    /// A posted tick does not run until something drains the queue.
    /// </summary>
    /// <remarks>
    /// The claim the old comment made and nothing checked. It is true — the fake dispatcher only
    /// enqueues — and it is exactly the sort of sentence that quietly takes a test's job, so it is
    /// now a test. Several ticks are posted and none of them has run.
    /// </remarks>
    [Fact]
    public void A_posted_tick_does_not_run_until_the_queue_is_drained()
    {
        var row = GivenABlockedSession();
        var tick = new UiTick(_dispatcher);
        tick.Attach(_viewModel);

        tick.Tick(At.AddMinutes(1));
        tick.Tick(At.AddMinutes(2));
        tick.Tick(At.AddMinutes(3));

        Assert.Equal(TimeSpan.Zero, row.Age);
        Assert.Equal(3, _dispatcher.Pump());
        Assert.Equal(TimeSpan.FromMinutes(3), row.Age);
    }

    /// <summary>
    /// Draining runs ticks in the order they were posted, and the last one wins — including a
    /// tick carrying an <em>earlier</em> instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the mechanism that broke the test above, written down as behaviour rather
    /// than denied in a comment.</strong> <see cref="MainViewModel.Tick"/> assigns the instant it
    /// is given without comparing it to the last one, so an out-of-order delivery rewinds every
    /// age on screen until the next tick.
    /// </para>
    /// <para>
    /// <strong>That is not a defect today, and the reason is worth stating because it is an
    /// assumption rather than a guarantee:</strong> exactly one thing in the product calls
    /// <see cref="IUiTick.Tick"/> — <c>EventConsumer</c>, on one thread — so the instants are read
    /// and posted in the same order and a stale tick can never follow a fresh one. A second caller
    /// would make this rewind real, and it would show as ages that jump backwards for up to
    /// fifteen seconds. If one is ever added, the cheap guard is for <c>Tick</c> to ignore an
    /// instant earlier than the one it already holds.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_last_tick_drained_wins_even_when_it_carries_an_earlier_instant()
    {
        var row = GivenABlockedSession();
        var tick = new UiTick(_dispatcher);
        tick.Attach(_viewModel);

        tick.Tick(At.AddMinutes(4));
        tick.Tick(At);

        _dispatcher.Pump();

        Assert.Equal(TimeSpan.Zero, row.Age);
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
