using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.App.Ui;

/// <summary>A rectangle in virtual-screen coordinates, independent of WPF.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => Left + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Top + Height;

    /// <summary>Whether this rectangle overlaps <paramref name="other"/> at all.</summary>
    public bool Intersects(ScreenRect other) =>
        Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}

/// <summary>Where the window should open, and why (Impl §5.4).</summary>
/// <param name="Bounds">The rectangle to place the window in.</param>
/// <param name="Restored">
/// True when the saved position was used; false when it was refused and a monitor was chosen.
/// </param>
public readonly record struct PlacementDecision(ScreenRect Bounds, bool Restored);

/// <summary>
/// Decides where the dashboard window opens (Impl §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separated from the window so the decision can be tested without a screen.</strong> The
/// interesting behaviour is not "apply these coordinates" — it is what happens when the monitor
/// they name is gone. A laptop undocked overnight, a screen unplugged, a resolution changed: the
/// saved rectangle is then somewhere no display covers, and a window restored there is invisible
/// and unreachable, with no way for the operator to work out what happened.
/// </para>
/// <para>
/// <strong>Overlap, not containment.</strong> A window is accepted if it intersects any monitor at
/// all, rather than requiring it to sit wholly inside one. Requiring containment would refuse the
/// perfectly ordinary case of a window deliberately straddling two screens, and would move it out
/// from under an operator who put it there.
/// </para>
/// </remarks>
public static class WindowPlacement
{
    /// <summary>How big the window is when nothing has been saved and XAML gives no size.</summary>
    public const double FallbackWidth = 520;

    /// <summary>…and how tall.</summary>
    public const double FallbackHeight = 720;

    /// <summary>
    /// Chooses where to open, given what was saved and which monitors exist now.
    /// </summary>
    /// <param name="saved">The remembered placement, or null on a first run.</param>
    /// <param name="monitors">Every monitor's working area, in virtual-screen coordinates.</param>
    /// <param name="focused">
    /// The monitor the operator is working on, used when the saved position cannot be honoured.
    /// </param>
    public static PlacementDecision Decide(
        WindowSettings? saved,
        IReadOnlyList<ScreenRect> monitors,
        ScreenRect focused)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var width = Sane(saved?.Width, FallbackWidth);
        var height = Sane(saved?.Height, FallbackHeight);

        if (saved is { HasPosition: true })
        {
            var wanted = new ScreenRect(saved.Left!.Value, saved.Top!.Value, width, height);

            if (monitors.Any(monitor => monitor.Intersects(wanted)))
            {
                return new PlacementDecision(wanted, Restored: true);
            }
        }

        // Nothing saved, or the monitor it named has gone. Centre on the focused one, which is
        // where the operator is looking — not on the primary, which may be somewhere else
        // entirely on a three-monitor desk.
        return new PlacementDecision(
            new ScreenRect(
                focused.Left + ((focused.Width - width) / 2),
                focused.Top + ((focused.Height - height) / 2),
                width,
                height),
            Restored: false);
    }

    /// <summary>
    /// A saved size only counts if it is a size. Zero, negative and NaN all reach here from a
    /// hand-edited file or from a window that was minimised when it was saved.
    /// </summary>
    private static double Sane(double? value, double fallback) =>
        value is { } candidate && candidate > 0 && !double.IsNaN(candidate) && !double.IsInfinity(candidate)
            ? candidate
            : fallback;
}
