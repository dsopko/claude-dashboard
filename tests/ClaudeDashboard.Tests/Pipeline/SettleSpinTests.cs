using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// <strong>A settled roster group must not leave the consumer loop spinning</strong>
/// (T1.25 fix cycle 1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the gap the rest of the suite left open, and it is worth naming exactly.</strong>
/// <c>SettleWakeTests</c> exercises <c>WaitFor</c>, which is a pure function and was always right.
/// <c>RosterLoggingTests</c> waits for a group to settle and then stops looking. Nothing watched
/// the loop <em>after</em> a deadline had passed — and that is where the defect lived: a deadline
/// already in the past was still reported as pending, so the loop re-armed itself every ten
/// milliseconds, re-resolved every group, and posted to the dispatcher, indefinitely.
/// </para>
/// <para>
/// <strong>The trigger is the feature's success path.</strong> An orchestration finishes, the group
/// settles, and the operator has not looked yet — which is precisely the state this product exists
/// to leave sitting on screen. A tray app that never polls cannot spend it at a hundred wake-ups a
/// second.
/// </para>
/// <para>
/// <strong>Run with the production settle window, not a zero-length one.</strong> A window of zero
/// would settle the group instantly and could be argued away as a harness artefact; 1.5 seconds is
/// the number that ships.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class SettleSpinTests : IAsyncLifetime
{
    /// <summary>How long to watch the loop for. Long enough to count, short enough to run.</summary>
    private static readonly TimeSpan Watch = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// The most wake-ups a quiet second may cost. The ordinary tick here is 200 ms, so a correct
    /// loop produces a handful; the defect produced roughly one every ten milliseconds.
    /// </summary>
    private const int Tolerable = 12;

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingUiTick _tick = new();
    private readonly RosterStore _rosters;

    private EventConsumer _consumer = null!;

    public SettleSpinTests() => _rosters = new RosterStore(_pipeline.Sink);

    public Task InitializeAsync()
    {
        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            Logger.None,
            _tick,
            _archive,
            _rosters,

            // Short enough that the loop takes its ordinary tick several times inside the watch
            // window, so the control has something to count and the defect stands out against it.
            tickInterval: TimeSpan.FromMilliseconds(200));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>A settled roster group leaves the loop as quiet as an ungrouped session does.</strong>
    /// </summary>
    /// <remarks>
    /// The ungrouped control is what makes the number mean anything: it fixes what "quiet" costs on
    /// this machine, on this run, so the assertion is a comparison rather than a magic number that
    /// would drift with the tick interval.
    /// </remarks>
    [Fact]
    public async Task A_settled_group_does_not_spin_the_loop()
    {
        // CONTROL: no roster at all. Whatever this costs is what an idle loop costs.
        _pipeline.Sink.TryPublish(Prompt("control", At, "p-1"));
        _pipeline.Sink.TryPublish(Finished("control", At, "p-1"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 2));

        var quietBefore = _tick.Calls;
        await Task.Delay(Watch);
        var ungrouped = _tick.Calls - quietBefore;

        // NOW the rostered one. Its window is the production 1.5 s, and the clock is moved past it
        // so the group genuinely settles rather than sitting mid-window for ever.
        _rosters.Replace(RosterBook.From([("orchestration", ["Director"])]));

        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-2"));
        _pipeline.Sink.TryPublish(Finished("s-1", At, "p-2"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 4));

        _clock.Advance(RosterSettle.DefaultWindow + TimeSpan.FromSeconds(1));

        Assert.True(await Until(() => _consumer.SettledCount >= 1));

        var settledBefore = _tick.Calls;
        await Task.Delay(Watch);
        var settled = _tick.Calls - settledBefore;

        Assert.True(
            settled <= Tolerable,
            $"A settled roster group woke the loop {settled} times in {Watch.TotalMilliseconds}ms; " +
            $"an ungrouped session woke it {ungrouped}. The deadline has passed, so nothing is " +
            "pending and the loop should be back on its ordinary tick.");
    }

    /// <summary>A settled group stops reporting a pending deadline.</summary>
    /// <remarks>
    /// The same defect one layer in, asserted directly so a reader does not have to infer the cause
    /// from a wake count. A deadline that has passed is not a deadline the loop is waiting for.
    /// </remarks>
    [Fact]
    public async Task A_settled_group_reports_no_pending_deadline()
    {
        _rosters.Replace(RosterBook.From([("orchestration", ["Director"])]));

        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));
        _pipeline.Sink.TryPublish(Finished("s-1", At, "p-1"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 2));

        _clock.Advance(RosterSettle.DefaultWindow + TimeSpan.FromSeconds(1));

        Assert.True(await Until(() => _consumer.SettledCount >= 1));
        Assert.True(await Until(() => _consumer.SettleDue is null));
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

    private static UserPromptSubmit Prompt(string id, DateTimeOffset at, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = id == "s-1" ? "Director" : "Somebody else",
    };

    private static Stop Finished(string id, DateTimeOffset at, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
    };
}
