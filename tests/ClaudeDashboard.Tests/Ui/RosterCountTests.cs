using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The counts strip and the tray count a roster once (T1.83, issue #130).
/// </summary>
/// <remarks>
/// <para>
/// The issue's orchestration, through the real Registry: Director, Coder and Reviewer in three folders, each finishing
/// in turn, so that one works and two have finished. The strip read <c>3 sessions · 2 unread · 1 working</c> and the
/// tray was green; both now read the roster as its heading does.
/// </para>
/// <para>
/// <strong>The settle reaches both by a tick at the deadline.</strong> Neither has a timer of its own; the consumer's
/// tick reaches them as <c>Tick(now)</c>, and its settle wake ticks them at the deadline. These tests call
/// <c>Tick</c> with the instant, as that wake does.
/// </para>
/// </remarks>
public sealed class RosterCountTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>When the last member stops, in <see cref="Settle"/>.</summary>
    private static readonly DateTimeOffset LastStop = At.AddMinutes(4);

    private static readonly IngressStatus Healthy = IngressStatus.Healthy(DashboardSettings.IngressPortBase);

    private readonly RegistryHarness _harness = new();
    private readonly RosterStore _rosters =
        new(new RecordingEventSink(), RosterBook.From([("orchestration", ["Director", "Coder", "Reviewer"])]));

    private readonly MainViewModel _window;
    private readonly TrayViewModel _tray;
    private readonly DecisionLog _decisions = new();
    private string _reviewerPrompt = string.Empty;

    public RosterCountTests()
    {
        _window = new MainViewModel(
            _harness.Projection,
            new MotionPolicy(() => false, observeChanges: false),
            new StubAckPublisher(),
            new FakeClipboard(),
            _rosters,
            new RecordingRosterPersistence());

        _tray = new TrayViewModel(
            _harness.Projection,
            new FakeSoundModes(),
            new RecordingEventSink(),
            new FakeClock(),
            Healthy,
            Logger.None,
            decisions: _decisions,
            rosters: _rosters);
    }

    public void Dispose()
    {
        _tray.Dispose();
        _window.Dispose();
        _harness.Dispose();
    }

    // ---- The window ---------------------------------------------------------------------------

    /// <summary>One member working and two finished: <c>3 sessions · 1 working</c>, in the grouped view.</summary>
    [Fact]
    public void A_working_orchestration_reads_one_working_in_the_grouped_view()
    {
        Orchestrate();
        Tick(At.AddMinutes(3));

        Assert.Equal("3 sessions · 1 working", _window.CountsText);
    }

    /// <summary>The same in the flat view: a roster exists whichever way the window is drawn.</summary>
    [Fact]
    public void A_working_orchestration_reads_one_working_in_the_flat_view()
    {
        _window.IsGrouped = false;
        Orchestrate();
        Tick(At.AddMinutes(3));

        Assert.Equal("3 sessions · 1 working", _window.CountsText);
    }

    /// <summary>
    /// The last member stops: inside the settle window it still reads <c>1 working</c>, and at the deadline it reads
    /// <c>1 unread</c>, in both views.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void At_the_settle_deadline_the_strip_reads_one_unread(bool grouped)
    {
        _window.IsGrouped = grouped;
        Settle();

        Tick(LastStop + TimeSpan.FromSeconds(1));
        Assert.Equal("3 sessions · 1 working", _window.CountsText);

        Tick(LastStop + RosterSettle.DefaultWindow);
        Assert.Equal("3 sessions · 1 unread", _window.CountsText);
    }

    // ---- The tray -----------------------------------------------------------------------------

    /// <summary>
    /// The tray's tooltip reads <c>1 working</c> and the light is blue while the orchestration works; the light names
    /// the working member.
    /// </summary>
    [Fact]
    public void A_working_orchestration_lights_the_tray_blue()
    {
        Orchestrate();
        Tick(At.AddMinutes(3));

        Assert.Equal(TrayColour.Blue, _tray.Colour);
        Assert.Contains("1 working", _tray.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("unread", _tray.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The light's decision record names the member whose state the roster shows: when the settled roster turns blue
    /// because the Reviewer starts, the change names the Reviewer.
    /// </summary>
    [Fact]
    public void The_light_change_names_the_member_the_roster_shows()
    {
        var director = _harness.Working("s-1", At, cwd: @"C:\director", title: "Director");
        _harness.Finished("s-1", At.AddMinutes(1), director, cwd: @"C:\director", title: "Director");
        var coder = _harness.Working("s-2", At.AddMinutes(1), cwd: @"C:\coder", title: "Coder");
        _harness.Finished("s-2", At.AddMinutes(2), coder, cwd: @"C:\coder", title: "Coder");
        Tick(At.AddMinutes(2) + RosterSettle.DefaultWindow);
        Assert.Equal(TrayColour.Green, _tray.Colour);

        _harness.Working("s-3", At.AddMinutes(3), cwd: @"C:\reviewer", title: "Reviewer");
        Tick(At.AddMinutes(3));

        Assert.Equal(TrayColour.Blue, _tray.Colour);
        var changed = _decisions.Rows.Last(row => row.Kind == DecisionKind.TrayLightChanged);
        Assert.Equal(nameof(TrayColour.Green), changed.FromState);
        Assert.Equal(nameof(TrayColour.Blue), changed.ToState);
        Assert.Equal("s-3", changed.SessionId);
    }

    /// <summary>After the settle the tray is green and reads <c>1 unread</c>; inside the window it is still blue.</summary>
    [Fact]
    public void At_the_settle_deadline_the_tray_turns_green_with_one_unread()
    {
        Settle();

        Tick(LastStop + TimeSpan.FromSeconds(1));
        Assert.Equal(TrayColour.Blue, _tray.Colour);

        Tick(LastStop + RosterSettle.DefaultWindow);
        Assert.Equal(TrayColour.Green, _tray.Colour);
        Assert.Contains("1 unread", _tray.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("working", _tray.Tooltip, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>Director and Coder have finished, Reviewer works: the issue's moment.</summary>
    private void Orchestrate()
    {
        var director = _harness.Working("s-1", At, cwd: @"C:\director", title: "Director");
        _harness.Finished("s-1", At.AddMinutes(1), director, cwd: @"C:\director", title: "Director");

        var coder = _harness.Working("s-2", At.AddMinutes(1), cwd: @"C:\coder", title: "Coder");
        _harness.Finished("s-2", At.AddMinutes(2), coder, cwd: @"C:\coder", title: "Coder");

        _reviewerPrompt = _harness.Working("s-3", At.AddMinutes(2), cwd: @"C:\reviewer", title: "Reviewer");
    }

    /// <summary>The orchestration, then the Reviewer finishes too, at <see cref="LastStop"/>.</summary>
    private void Settle()
    {
        Orchestrate();
        _harness.Finished("s-3", LastStop, _reviewerPrompt, cwd: @"C:\reviewer", title: "Reviewer");
    }

    /// <summary>The consumer's tick, or its settle wake, at <paramref name="now"/>: the window and the tray hear it.</summary>
    private void Tick(DateTimeOffset now)
    {
        _window.Tick(now);
        _tray.Tick(now);
    }

    /// <summary>No mute and no pause: the tray's modes, held still.</summary>
    private sealed class FakeSoundModes : ISoundModeReader
    {
        public bool IsMonitoringPaused => false;

        public DateTimeOffset? AllMutedUntil => null;
    }

    /// <summary>Records each decision the tray hands it.</summary>
    private sealed class DecisionLog : IDecisionLog
    {
        public List<Decision> Rows { get; } = [];

        public void External(Decision decision) => Rows.Add(decision);
    }
}
