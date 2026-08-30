using System.Windows;
using System.Windows.Controls;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// A horizontal strip that shows what fits and drops the rest whole, from the right.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the caption needs one.</strong> Design option 2c draws its summary slot in an
/// 820-pixel window and puts two counts in it, "11 sessions · 3 need you". This window opens at
/// 520 (<see cref="WindowPlacement.FallbackWidth"/>) and carries four, because the counts strip
/// moved into the caption rather than being redrawn for it — so the strip is about sixty pixels
/// wider than the slot at the size the operator actually sees.
/// </para>
/// <para>
/// <strong>Whole segments, never half a word.</strong> A <c>StackPanel</c> in a starred column
/// clips wherever the pixels run out, and "8 wor" is worse than nothing: it reads as a rendering
/// fault rather than as a count. This drops a child entirely or shows it entirely.
/// </para>
/// <para>
/// <strong>Last declared is first dropped</strong>, which is why the caption declares the counts
/// in the order the design reads them — total, needs-you, unread, working. Widening the window
/// brings them back in the reverse order, so what survives at every width is a prefix of what
/// 2c draws rather than an arbitrary subset of it.
/// </para>
/// <para>
/// A child already collapsed — every count is, at zero — measures to nothing, costs no room, and
/// so cannot push a later one out. Visibility is not touched here: the counts' own bindings own
/// it, and a panel that wrote to the same property would fight them.
/// </para>
/// </remarks>
public sealed class FittingStrip : Panel
{
    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var used = 0.0;
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            // Measured against infinity: what is wanted has to be known before it can be judged
            // against what is left, and a child measured against the remainder would shrink to
            // fit instead of being dropped.
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));

            var wanted = child.DesiredSize;

            // The strip is as tall as its tallest child whether or not that child is shown, so
            // the caption's line does not jump as counts come and go.
            height = Math.Max(height, wanted.Height);

            if (used + wanted.Width <= availableSize.Width)
            {
                used += wanted.Width;
            }
        }

        return new Size(used, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            var wanted = child.DesiredSize;

            if (x + wanted.Width <= finalSize.Width)
            {
                child.Arrange(new Rect(x, 0, wanted.Width, finalSize.Height));
                x += wanted.Width;

                continue;
            }

            // No room. Arranged empty rather than collapsed, so the count's own visibility
            // binding is left alone and the child comes back the moment the window widens.
            child.Arrange(default);
        }

        return finalSize;
    }
}
