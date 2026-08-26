using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// Where the window opens, and what happens when the monitor it remembers is gone (Impl §5.4).
/// </summary>
/// <remarks>
/// The decision is separated from the window so it can be tested without a screen. What is worth
/// testing is not "apply these coordinates" — it is the refusal: a saved rectangle on a monitor
/// that no longer exists restores a window the operator cannot see and cannot reach, with nothing
/// on screen to explain it.
/// </remarks>
public sealed class WindowPlacementTests
{
    private static readonly ScreenRect Laptop = new(0, 0, 1920, 1040);
    private static readonly ScreenRect Left = new(-2560, 0, 2560, 1400);
    private static readonly IReadOnlyList<ScreenRect> BothMonitors = [Laptop, Left];

    private static WindowSettings Saved(double left, double top) =>
        new() { Left = left, Top = top, Width = 500, Height = 700 };

    [Fact]
    public void A_saved_position_on_a_monitor_that_still_exists_is_restored()
    {
        var decision = WindowPlacement.Decide(Saved(-2000, 200), BothMonitors, Laptop);

        Assert.True(decision.Restored);
        Assert.Equal(new ScreenRect(-2000, 200, 500, 700), decision.Bounds);
    }

    /// <summary>
    /// A saved position on a monitor that has gone opens on the focused one instead.
    /// </summary>
    /// <remarks>
    /// The criterion, and the reason the whole type exists. The undocked laptop is the ordinary
    /// case: the second screen's coordinates are still in the settings file and now name nothing.
    /// </remarks>
    [Fact]
    public void A_saved_position_on_a_vanished_monitor_falls_back_to_the_focused_one()
    {
        var decision = WindowPlacement.Decide(Saved(-2000, 200), [Laptop], Laptop);

        Assert.False(decision.Restored);
        Assert.True(
            Laptop.Intersects(decision.Bounds),
            $"the window opened at {decision.Bounds}, which is on no monitor");
    }

    /// <summary>A window straddling two monitors is left where the operator put it.</summary>
    /// <remarks>
    /// Overlap rather than containment. Requiring the window to sit wholly inside one monitor
    /// would move it out from under an operator who deliberately spanned two, which is a real
    /// arrangement on a wide desk and not a mistake to correct.
    /// </remarks>
    [Fact]
    public void A_window_straddling_two_monitors_is_not_moved()
    {
        var decision = WindowPlacement.Decide(Saved(-260, 100), BothMonitors, Laptop);

        Assert.True(decision.Restored);
        Assert.Equal(-260, decision.Bounds.Left);
    }

    [Fact]
    public void A_first_run_with_nothing_saved_opens_on_the_focused_monitor()
    {
        var decision = WindowPlacement.Decide(new WindowSettings(), BothMonitors, Left);

        Assert.False(decision.Restored);
        Assert.True(Left.Intersects(decision.Bounds));
        Assert.Equal(WindowPlacement.FallbackWidth, decision.Bounds.Width);
    }

    [Fact]
    public void No_settings_at_all_still_produces_a_usable_window()
    {
        var decision = WindowPlacement.Decide(null, BothMonitors, Laptop);

        Assert.False(decision.Restored);
        Assert.True(Laptop.Intersects(decision.Bounds));
    }

    /// <summary>
    /// Sizes that are not sizes fall back rather than producing an invisible window.
    /// </summary>
    /// <remarks>
    /// All three reach here from real places: zero from a window saved while minimised, negative
    /// and NaN from a hand-edited file. A window of zero width is on a monitor and cannot be seen,
    /// which is the same outcome as being off-screen and harder to diagnose.
    /// </remarks>
    [Theory]
    [InlineData(0d)]
    [InlineData(-100d)]
    [InlineData(double.NaN)]
    public void A_saved_size_that_is_not_a_size_falls_back(double width)
    {
        var saved = new WindowSettings { Left = 10, Top = 10, Width = width, Height = 700 };

        var decision = WindowPlacement.Decide(saved, BothMonitors, Laptop);

        Assert.Equal(WindowPlacement.FallbackWidth, decision.Bounds.Width);
    }

    /// <summary>With no monitors at all it still returns something rather than throwing.</summary>
    /// <remarks>
    /// Enumeration can fail, and a dashboard that refuses to start because it could not list
    /// screens is worse than one that opens in the wrong place.
    /// </remarks>
    [Fact]
    public void No_monitors_at_all_is_survivable()
    {
        var decision = WindowPlacement.Decide(Saved(-2000, 200), [], Laptop);

        Assert.False(decision.Restored);
        Assert.True(decision.Bounds.Width > 0 && decision.Bounds.Height > 0);
    }

    /// <summary>Always-on-top is off unless the operator turned it on (Impl §5.4).</summary>
    [Fact]
    public void Always_on_top_defaults_to_off() =>
        Assert.False(new WindowSettings().AlwaysOnTop);
}
