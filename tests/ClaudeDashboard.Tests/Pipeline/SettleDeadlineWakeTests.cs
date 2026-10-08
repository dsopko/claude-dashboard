using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// <strong>A roster's settle is seen at its deadline, not at the next tick</strong> (#131), and at that moment the
/// counts strip, the tray and <c>/state</c> read <c>1 unread</c> (T1.83, #130).
/// </summary>
/// <remarks>
/// <para>
/// <strong>In real time, with a long tick, on purpose.</strong> The defect was in the consumer's loop: an event that
/// started a roster's settle window left the wait aimed at the next ordinary tick, so the settle was seen up to a tick
/// after its 1.5 s deadline (measured: 4.7 s late with a 5 s tick). <c>SettleWakeTests</c> checks <c>WaitFor</c>, which
/// was always right, and the pipeline tests use a 20 ms tick, which hides any lateness. So this test uses the system
/// clock, the shipped settle window and a tick of ten seconds, and states the lateness it allows as a bound.
/// </para>
/// <para>
/// <strong>End to end.</strong> The consumer ticks a real <see cref="UiTick"/>, which posts to the window and the tray
/// through the test's dispatcher, and builds <c>/state</c> again through a real <see cref="StateBoard"/>. Nothing
/// here ticks them by hand: if the wake is late, all three are late.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Each test disposes what it builds.")]
public sealed class SettleDeadlineWakeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The ordinary tick here: long, so a wake that waits for it is seconds late.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How late after the deadline the three may read <c>1 unread</c>. The wake waits for the deadline, then the
    /// window and the tray hear it through the dispatcher, which this test pumps every 10 ms; Windows' timer is about
    /// 15 ms. A few hundred milliseconds is far more than that, and far less than a tick.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(400);

    private static readonly IngressStatus Healthy = IngressStatus.Healthy(DashboardSettings.IngressPortBase);

    [Fact]
    public async Task At_the_settle_deadline_the_strip_the_tray_and_state_read_one_unread()
    {
        var clock = new SystemClock();
        var guard = new SingleWriterGuard();
        var registry = new SessionRegistry(new SingleWriterGuard());
        var pipeline = new EventPipeline(Logger.None);
        var archive = new EventArchive(Logger.None);
        var rosters = new RosterStore(pipeline.Sink, RosterBook.From([("orchestration", ["Coder", "Reviewer"])]), clock);
        var recorder = new DecisionRecorder(registry, rosters, archive, Logger.None);
        var engine = new SoundPolicyEngine(new RecordingSoundPlayer(), clock, guard, new SoundPolicyOptions(), recorder);

        // As AppHost wires it: the engine hears each change first, then the board subscribes.
        registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, rosters.Book));
        using var board = new StateBoard(registry, engine, clock, Logger.None, rosters);

        var ui = new QueueingDispatcher();
        using var projection = new SessionProjection(registry, ui);
        using var window = new MainViewModel(
            projection,
            new MotionPolicy(() => false, observeChanges: false),
            new StubAckPublisher(),
            new FakeClipboard(),
            rosters,
            new RecordingRosterPersistence());
        using var tray = new TrayViewModel(projection, new NoModes(), new RecordingEventSink(), clock, Healthy, Logger.None, rosters: rosters);
        var uiTick = new UiTick(ui);
        uiTick.Attach(window);
        uiTick.Attach(tray);

        using var consumer = new EventConsumer(
            pipeline, registry, engine, clock, guard, Logger.None, uiTick, archive, rosters, recorder,
            tickInterval: Tick,
            state: board);
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            // The first wait is armed for the tick, as in the product, before anything is quiet.
            await Task.Delay(200);

            Publish(pipeline, Prompt("s-1", "Coder", "p-1", clock.Now));
            Publish(pipeline, Prompt("s-2", "Reviewer", "p-2", clock.Now));
            Publish(pipeline, Finished("s-1", "p-1", clock.Now));
            var lastStop = clock.Now;
            Publish(pipeline, Finished("s-2", "p-2", lastStop));
            var deadline = lastStop + RosterSettle.DefaultWindow;

            DateTimeOffset? seen = null;
            var giveUp = deadline + TimeSpan.FromSeconds(3);

            while (clock.Now < giveUp)
            {
                ui.Pump();

                if (window.CountsText == "2 sessions · 1 unread"
                    && tray.Colour == TrayColour.Green
                    && board.Current.Bands[AttentionBand.Unread] == 1
                    && board.Current.Bands[AttentionBand.Working] == 0)
                {
                    seen = clock.Now;
                    break;
                }

                await Task.Delay(10);
            }

            Assert.True(seen is not null, $"The strip, the tray and /state did not read 1 unread within 3 s of the deadline (strip: {window.CountsText}; tray: {tray.Colour}).");

            var late = seen!.Value - deadline;
            output.WriteLine($"WAKE late by {late.TotalMilliseconds:F0} ms after the deadline");

            Assert.True(late >= TimeSpan.Zero, $"They read 1 unread {-late.TotalMilliseconds:F0} ms before the deadline.");
            Assert.True(late <= Bound, $"They read 1 unread {late.TotalMilliseconds:F0} ms after the deadline; the bound is {Bound.TotalMilliseconds} ms.");
            Assert.Equal(2, board.Current.SessionCount);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    private static void Publish(EventPipeline pipeline, InboundEvent inboundEvent) =>
        Assert.True(pipeline.Sink.TryPublish(inboundEvent));

    private static UserPromptSubmit Prompt(string id, string title, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id), Timestamp = at, Cwd = @"C:\w\" + id, PromptId = promptId, Prompt = "go", SessionTitle = title,
    };

    private static Stop Finished(string id, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id), Timestamp = at, Cwd = @"C:\w\" + id, PromptId = promptId, LastAssistantMessage = "done",
    };

    /// <summary>No mute and no pause.</summary>
    private sealed class NoModes : ISoundModeReader
    {
        public bool IsMonitoringPaused => false;

        public DateTimeOffset? AllMutedUntil => null;
    }
}
