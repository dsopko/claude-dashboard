using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// "Red blinks; working breathes; nothing else moves" (Design Document §9), and nothing moves at
/// all when the operator has asked for reduced motion.
/// </summary>
/// <remarks>
/// <para>
/// The rule is asserted on the view model rather than on rendered pixels, because that is where
/// it is decided — the XAML has two storyboards and reaches them only through
/// <see cref="SessionViewModel.Motion"/>. That the templates really are wired that way, and that
/// an LED really does animate, is <c>MainWindowTests</c>'s job.
/// </para>
/// <para>
/// <strong>Both halves, always.</strong> "Nothing else moves" is satisfied by an implementation
/// that animates nothing, and "red blinks" by one that animates everything. Each test here
/// carries its opposite.
/// </para>
/// </remarks>
public sealed class MotionTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private static readonly MotionPolicy Allowed = new(() => true, observeChanges: false);
    private static readonly MotionPolicy Suppressed = new(() => false, observeChanges: false);

    private static SessionViewModel Row(SessionState state, MotionPolicy? policy = null) =>
        new(
            new Session
            {
                Id = new SessionId("s-1"),
                State = state,
                Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
                Cwd = @"C:\dev\PennCustQuote",
                Group = GroupKeys.ForWorkspace(@"C:\dev\PennCustQuote"),
                EnteredAt = At,
                LastActivity = At,
            },
            policy ?? Allowed);

    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    public void Red_blinks(SessionState state)
    {
        Assert.Equal(MotionKind.Blink, Row(state).Motion);
    }

    [Fact]
    public void Working_breathes()
    {
        Assert.Equal(MotionKind.Breathe, Row(SessionState.Working).Motion);
    }

    /// <summary>
    /// Every other state is still — enumerated from the enum, so a state added later is covered
    /// the day it appears rather than the day someone remembers this file.
    /// </summary>
    [Fact]
    public void Nothing_else_moves()
    {
        var moving = Enum.GetValues<SessionState>()
            .Where(state => Row(state).Motion != MotionKind.None)
            .ToList();

        Assert.Equal(
            [SessionState.NeedsPermission, SessionState.NeedsQuestion, SessionState.Working],
            moving.OrderBy(state => state.ToString(), StringComparer.Ordinal));
    }

    /// <summary>
    /// An error sits in the Needs-You band and still does not blink: it is amber, and §9 gives
    /// the blink to red. The distinction is the mockups' — "a turn died" must not read as "it is
    /// asking you" — and a rule keyed on the band rather than the state would lose it.
    /// </summary>
    [Fact]
    public void An_error_is_in_the_needs_you_band_and_still_does_not_blink()
    {
        var row = Row(SessionState.Error);

        Assert.Equal(AttentionBand.NeedsYou, row.Band);
        Assert.Equal(Accent.Amber, row.Accent);
        Assert.Equal(MotionKind.None, row.Motion);
    }

    /// <summary>Reduced motion stops the two things that move, and only those two were moving.</summary>
    [Fact]
    public void Reduced_motion_stops_everything()
    {
        foreach (var state in Enum.GetValues<SessionState>())
        {
            Assert.Equal(MotionKind.None, Row(state, Suppressed).Motion);
        }

        // …and the same states under the same rule do move when motion is allowed, so this is
        // the setting being honoured rather than an implementation that never animates.
        Assert.Equal(MotionKind.Blink, Row(SessionState.NeedsPermission).Motion);
        Assert.Equal(MotionKind.Breathe, Row(SessionState.Working).Motion);
    }

    /// <summary>
    /// The setting is followed while the app runs, not read once at startup: an operator who
    /// turns animations off should not have to restart the dashboard to stop it blinking.
    /// </summary>
    [Fact]
    public void Turning_motion_off_while_running_stops_the_rows()
    {
        var allowed = true;
        using var policy = new MotionPolicy(() => allowed, observeChanges: false);
        var row = Row(SessionState.NeedsPermission, policy);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(MotionKind.Blink, row.Motion);

        allowed = false;
        policy.Refresh();
        row.RefreshMotion();

        Assert.Equal(MotionKind.None, row.Motion);
        Assert.Contains(nameof(SessionViewModel.Motion), changed);
    }

    /// <summary>
    /// The view model tells its rows when the policy moves, so a live change reaches the screen
    /// without anything else being touched.
    /// </summary>
    [Fact]
    public void The_view_model_relays_a_policy_change_to_its_rows()
    {
        var allowed = true;
        using var policy = new MotionPolicy(() => allowed, observeChanges: false);
        using var registry = new RegistryHarness();
        using var viewModel = new MainViewModel(registry.Projection, policy, new StubAckPublisher(), new FakeClipboard());

        registry.Working("s-1", At);
        var row = viewModel.Rows.OfType<SessionViewModel>().Single();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(MotionKind.Breathe, row.Motion);

        allowed = false;
        policy.Refresh();

        Assert.Equal(MotionKind.None, row.Motion);
        Assert.Contains(nameof(SessionViewModel.Motion), changed);
    }

    /// <summary>
    /// A row handed no policy still honours the setting rather than animating regardless — the
    /// default is the system's answer, not "yes".
    /// </summary>
    [Fact]
    public void A_row_with_no_policy_asks_the_system()
    {
        var row = Row(SessionState.Working, MotionPolicy.System);
        var expected = MotionPolicy.System.IsMotionAllowed ? MotionKind.Breathe : MotionKind.None;

        Assert.Equal(expected, new SessionViewModel(row.Session).Motion);
    }
}
