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
    private readonly SessionRegistry _registry = new();
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
            new SoundPolicyEngine(_player, _clock),
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
    [Fact]
    public async Task An_age_advances_with_no_event_arriving()
    {
        var row = GivenABlockedSession();
        _tick.Attach(_viewModel);

        Assert.Equal(TimeSpan.Zero, row.Age);

        _clock.Now = At.AddMinutes(9);

        // The consumer ticks on its own loop; the dispatcher is this test's UI thread.
        Assert.True(await Until(() => _tick.DeliveredCount > 0), "the consumer never ticked the UI");
        _dispatcher.Pump();

        Assert.Equal(TimeSpan.FromMinutes(9), row.Age);
        Assert.Equal("waiting 9 min", row.AgeText);

        // …and it keeps moving, which a one-off would not.
        _clock.Now = At.AddMinutes(10);
        var delivered = _tick.DeliveredCount;
        Assert.True(await Until(() => _tick.DeliveredCount > delivered));
        _dispatcher.Pump();

        Assert.Equal("waiting 10 min", row.AgeText);
    }

    /// <summary>
    /// Staleness is the same clock asking a different question, so it must ride the same tick —
    /// a second timer for the collapse rules would be the mistake T1.9's single loop prevents.
    /// </summary>
    [Fact]
    public async Task A_group_goes_stale_on_the_same_tick()
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

        Assert.True(await Until(() => _tick.DeliveredCount > 0));
        _dispatcher.Pump();

        Assert.True(header.IsStale);
    }

    /// <summary>
    /// The tick posts and returns. It must not run the view model's work on the consumer thread,
    /// which owns the Registry and would be blocked behind a render.
    /// </summary>
    [Fact]
    public async Task The_tick_is_posted_rather_than_run_on_the_consumer_thread()
    {
        var row = GivenABlockedSession();
        _tick.Attach(_viewModel);
        _clock.Now = At.AddMinutes(4);

        Assert.True(await Until(() => _tick.DeliveredCount > 0));

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
            new SoundPolicyEngine(_player, _clock),
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
