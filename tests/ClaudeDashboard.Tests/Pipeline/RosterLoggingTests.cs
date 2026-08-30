using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// A roster is logged by its own name and never by its membership, and a group settles through the
/// real pipeline (T1.25, issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A member name is a session title, and a title can carry the operator's words.</strong>
/// A session nobody named gets a title written by a background model call summarising their first
/// prompt (T1.24), so the never-log rule follows it into a roster. The roster's own name is the
/// operator's label and is deliberately logged, which is what lets the mis-mark warning say
/// anything useful at all.
/// </para>
/// <para>
/// <strong>This test is the guard, not <c>UnprotectedTextInventory</c>.</strong> That inventory
/// scans public instance <em>string</em> properties, so a collection of strings is invisible to it
/// — a real hole, filed separately. What closes the actual exposure is this: drive the paths that
/// log and assert the member never appears.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class RosterLoggingTests : IAsyncLifetime
{
    /// <summary>A member name unique enough that a hit is this and not a coincidence.</summary>
    private const string Member = "zqx-roster-member-marker-4b1";

    private const string RosterName = "orchestration";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly CapturingSink _sink = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    // The REAL sink, so a roster edit in these tests wakes the loop exactly as it does in the
    // running application. A recording fake here would test the wiring with the wiring removed.
    private readonly RosterStore _rosters;

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public RosterLoggingTests() => _rosters = new RosterStore(_pipeline.Sink);

    public Task InitializeAsync()
    {
        _rosters.Replace(RosterBook.From([(RosterName, [Member])]));

        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();

        var sound = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions());

        // Wired exactly as AppHost wires it, and this line is load-bearing rather than scenery: it
        // is the ONLY thing that gives a member the chance to sound its own done notice. Without it
        // the suppression test below would pass because nothing ever asked the engine about a
        // member — which is what it did until a planted defect failed to break it.
        _registry.SessionChanged += (_, e) =>
            sound.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            sound,
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            _rosters,
            tickInterval: TimeSpan.FromMilliseconds(20),
            watch: new RosterGroupWatch(window: TimeSpan.Zero, misMarkWindow: TimeSpan.FromHours(1)));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>A roster group settles through the real pipeline, and the log never names a member.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settle window is zero here so the group settles on the next pass rather than on a real
    /// clock — the window itself is proved in <c>RosterGroupingTests</c>, and repeating it through
    /// a running pipeline would only add a wait.
    /// </para>
    /// <para>
    /// The mis-mark path is walked too, because it is the one line in the feature that mentions a
    /// group at all and therefore the one most likely to grow a member name later.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_group_settles_and_mis_marks_without_the_log_naming_a_member()
    {
        // Every event is stamped at the fake clock's own instant. An event stamped in that
        // clock's future would leave the group permanently mid-settle, because the settle is
        // measured from the member's EnteredAt against a clock that never reaches it.
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));
        _pipeline.Sink.TryPublish(Finished("s-1", At, "p-1"));

        Assert.True(await Until(() => _consumer.SettledCount >= 1));
        Assert.Equal(SoundId.Finished, Assert.Single(_player.Played).Sound);

        // Back to work inside the mis-mark window: the finished was wrong.
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-2"));

        Assert.True(await Until(() => _consumer.MisMarkedCount >= 1));

        // The control: the mis-mark line was really written, so the silence below is the member
        // being withheld rather than nothing having been logged.
        Assert.Contains(Rendered, line => line.Contains("settle window is too short", StringComparison.Ordinal));
        Assert.Contains(Rendered, line => line.Contains(RosterName, StringComparison.Ordinal));

        var leaked = Rendered.Where(line => line.Contains(Member, StringComparison.Ordinal)).ToList();

        Assert.True(
            leaked.Count == 0,
            $"A roster member name reached the log: {string.Join(" | ", leaked)}");

        // …and the session really was in the roster, so the arrangement was not silently ungrouped.
        Assert.Equal(Member, _registry.Sessions[new SessionId("s-1")].Title);
    }

    /// <summary>
    /// <strong>The member's own done chime is suppressed; the group's is the only one.</strong>
    /// </summary>
    /// <remarks>
    /// End to end rather than against the engine alone, because the suppression depends on the
    /// consumer handing the engine the <em>effective</em> group — and a wiring that passed the
    /// workspace key instead would sound both and pass every unit test.
    /// </remarks>
    [Fact]
    public async Task Only_the_group_sounds_when_a_member_finishes()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));
        _pipeline.Sink.TryPublish(Finished("s-1", At, "p-1"));

        Assert.True(await Until(() => _consumer.SettledCount >= 1));

        Assert.Single(_player.Played, played => played.Sound == SoundId.Finished);
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

    private static UserPromptSubmit Prompt(string id, DateTimeOffset at, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = Member,
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
