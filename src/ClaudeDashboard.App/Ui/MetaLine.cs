using System.Windows;
using System.Windows.Controls;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The row's meta line: the badge, its detail, the speaker sign, the age and the tags, left to
/// right (Design Document §9). It lays out as a horizontal <see cref="StackPanel"/> does, with one
/// rule more: a child marked <see cref="GivesWayFirstProperty"/> is the first thing to go when the
/// line is too wide (T1.67, issue #99).
/// </summary>
/// <remarks>
/// <para>
/// <strong>How the row gave up width before this, and still does.</strong> The line was a
/// horizontal <c>StackPanel</c>: each child takes the width it wants, and the row clips the line at
/// its right edge. So the group tag goes first, then the waiting summary, then the age. That is
/// unchanged here, child for child.
/// </para>
/// <para>
/// <strong>The sign sits before the age</strong>, so clipping alone would take the age first and
/// leave the sign. The issue says the sign is the first thing to go. So the marked child is laid
/// out only when the whole line fits; when it does not, the marked child takes no room, and the
/// rest clip as before. The age then keeps the place it had before the sign existed.
/// </para>
/// <para>
/// <strong>Arranged empty, not collapsed.</strong> The sign's own visibility is bound to its view
/// model, and a local value written here would outrank that binding for good. So the panel leaves
/// <see cref="UIElement.Visibility"/> alone and arranges the child in an empty rectangle, as
/// <see cref="FittingStrip"/> does for a count it drops. The decision is made once, in measure, from
/// widths that do not depend on it, so it cannot oscillate; arrange draws what measure decided
/// (the T1.42 lesson).
/// </para>
/// </remarks>
public sealed class MetaLine : Panel
{
    /// <summary>
    /// Marks the child that is the first thing to go when the line is too wide (T1.67).
    /// </summary>
    public static readonly DependencyProperty GivesWayFirstProperty =
        DependencyProperty.RegisterAttached(
            "GivesWayFirst",
            typeof(bool),
            typeof(MetaLine),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>
    /// How far a sum may exceed the room and still fit: a hundredth of a DIP, as in
    /// <see cref="FittingStrip"/> (T1.42, issue #55).
    /// </summary>
    private const double Tolerance = 0.01;

    /// <summary>Sets <see cref="GivesWayFirstProperty"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static void SetGivesWayFirst(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(GivesWayFirstProperty, value);
    }

    /// <summary>Reads <see cref="GivesWayFirstProperty"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static bool GetGivesWayFirst(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (bool)element.GetValue(GivesWayFirstProperty);
    }

    /// <summary>
    /// Whether the last measure left the marked child out because the line did not fit. For tests.
    /// </summary>
    public bool GaveWay { get; private set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var wanted = 0.0;
        var marked = 0.0;
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            // Against infinity, as a horizontal StackPanel measures: each child asks for what it wants.
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            wanted += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);

            if (GetGivesWayFirst(child))
            {
                marked += child.DesiredSize.Width;
            }
        }

        GaveWay = marked > 0 && wanted > availableSize.Width + Tolerance;

        return new Size(GaveWay ? wanted - marked : wanted, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            if (GaveWay && GetGivesWayFirst(child))
            {
                child.Arrange(default);
                continue;
            }

            var width = child.DesiredSize.Width;

            child.Arrange(new Rect(x, 0, width, Math.Max(finalSize.Height, child.DesiredSize.Height)));
            x += width;
        }

        return finalSize;
    }
}
