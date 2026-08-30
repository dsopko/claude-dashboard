using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// <strong>A roster edit reaches the consumer without waiting for a tick</strong> (T1.26, issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The tick is the whole point, so it is fifteen minutes here.</strong> The consumer reads
/// the roster book on its own thread and re-resolves groups only after a drain or a tick. An edit
/// made on the dispatcher that did not wake it would be invisible to the sound side until the next
/// tick — a dissolved group still able to nudge, a new group unable to settle, the screen already
/// right and the sound fifteen seconds behind it. A tick long enough that it cannot possibly fire
/// is what makes this test about the wake rather than about the tick.
/// </para>
/// <para>
/// This is the T1.26 form of the question that found T1.25's spin: not "does it work" but "what
/// happens after — and how long does the wrong answer last".
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class RosterEditWakeTests : IAsyncLifetime
{
    private const string Member = "zqx-roster-member-marker-9c2";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly CapturingSink _sink = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    private readonly RosterStore _rosters;

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public RosterEditWakeTests() => _rosters = new RosterStore(_pipeline.Sink);

    public Task InitializeAsync()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            _rosters,

            // Long enough that nothing here can be explained by a tick having happened.
            tickInterval: TimeSpan.FromMinutes(15),
            watch: new RosterGroupWatch(window: TimeSpan.Zero));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>A group formed by an edit settles without a tick; dissolving it unsettles it, also
    /// without a tick.</strong>
    /// </summary>
    /// <remarks>
    /// Both halves matter. Forming proves the consumer re-read membership it had never seen;
    /// dissolving proves it noticed a group disappearing — which is the half that would otherwise
    /// leave a nudge scheduled for a group that no longer exists.
    /// </remarks>
    [Fact]
    public async Task An_edit_is_observed_without_a_tick()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", "p-1"));
        _pipeline.Sink.TryPublish(Finished("s-1", "p-1"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 2));

        // Not in a roster yet, so nothing has settled.
        Assert.Equal(0, _consumer.SettledCount);

        // THE EDIT. Nothing else happens: no event, no tick.
        _rosters.Replace(RosterBook.From([("orchestration", [Member])]));

        Assert.True(await Until(() => _consumer.SettledCount >= 1));
        Assert.True(_consumer.RosterEditCount >= 1);
        Assert.Equal(0, _consumer.TickCount);

        // …and dissolving it is noticed the same way.
        _rosters.Replace(RosterBook.Empty);

        Assert.True(await Until(() => _consumer.RosterEditCount >= 2));
        Assert.True(await Until(() => _consumer.SettleDue is null));
        Assert.Equal(0, _consumer.TickCount);
    }

    /// <summary>The wake is not applied to the Registry and is not archived.</summary>
    /// <remarks>
    /// It changes no session and carries nothing, so a Registry that saw it would decline it and a
    /// history that stored it would be recording a UI gesture as though it were an observation.
    /// </remarks>
    [Fact]
    public async Task The_wake_never_reaches_the_registry()
    {
        _rosters.Replace(RosterBook.From([("orchestration", [Member])]));

        Assert.True(await Until(() => _consumer.RosterEditCount >= 1));

        Assert.Equal(0, _consumer.AppliedCount);
        Assert.Equal(0, _consumer.DeclinedCount);
        Assert.Empty(_registry.Sessions);
    }

    /// <summary>
    /// <strong>No log line names a roster member, on the paths T1.26 added.</strong>
    /// </summary>
    /// <remarks>
    /// T1.25 closed this for the settle and mis-mark paths. Forming and dissolving are new paths,
    /// and a member name is a session title — which for an unnamed session is a model-written
    /// summary of the operator's prompt (T1.24). The control asserts the run logged something at
    /// all, so the silence is the member being withheld rather than nothing having happened.
    /// </remarks>
    [Fact]
    public async Task No_log_line_names_a_member_when_a_roster_is_edited()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", "p-1"));
        _pipeline.Sink.TryPublish(Finished("s-1", "p-1"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 2));

        _rosters.Replace(RosterBook.From([("orchestration", [Member])]));
        Assert.True(await Until(() => _consumer.SettledCount >= 1));

        _rosters.Replace(RosterBook.Empty);
        Assert.True(await Until(() => _consumer.RosterEditCount >= 2));

        Assert.NotEmpty(Rendered);

        var leaked = Rendered.Where(line => line.Contains(Member, StringComparison.Ordinal)).ToList();

        Assert.True(
            leaked.Count == 0,
            $"A roster member name reached the log: {string.Join(" | ", leaked)}");

        Assert.Equal(Member, _registry.Sessions[new SessionId("s-1")].Title);
    }

    private IReadOnlyList<string> Rendered =>
        [.. _sink.Events.Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture))];

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

    private static UserPromptSubmit Prompt(string id, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = At,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = Member,
    };

    private static Stop Finished(string id, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = At,
        Cwd = @"C:\w",
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
    };
}
