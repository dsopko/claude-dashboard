using System.Windows;
using System.Windows.Controls;

namespace ClaudeDashboard.App.Ui;

/// <summary>A piece of markup that holds one <see cref="FittingStrip"/>: the counts, or the usage.</summary>
public interface IFittingStripHost
{
    /// <summary>The strip inside.</summary>
    FittingStrip Strip { get; }
}

/// <summary>
/// The caption's summary slot, and the row under the caption: the counts and the usage on one line, with the
/// counts measured first (MOD.7, issue #133; ruling R9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two children, in this order: the counts, then the usage.</strong> The order is the order of
/// measuring, not of drawing. The counts are measured against the whole slot, exactly as they were when they
/// had the slot alone, so their tiers, their order and their <see cref="FittingStrip.HasDropped"/> do not
/// change. The usage is then measured against what the counts left. So the usage never changes what the counts
/// decide, and the choice on each line is still a pure function of the line's width and what the strips hold.
/// </para>
/// <para>
/// <strong>Each child places itself.</strong> The counts are arranged over the whole slot, and their own
/// alignment puts them at the right edge; their layout slot is the whole line, as it was. The usage is arranged
/// over the part left of the counts, and its own alignment puts it beside them in the caption and at the left
/// edge on the row. Neither child is stretched, so each <see cref="FittingStrip"/> is arranged at the width it
/// measured, which it needs (its remark on <c>ArrangeOverride</c>).
/// </para>
/// <para>
/// <strong>Why not a <c>DockPanel</c>.</strong> It measures in the same order, but in the caption the counts
/// that left for the row are still measured (hidden, not collapsed), and a <c>DockPanel</c> would give the usage
/// the few pixels the hidden counts did not use. See <see cref="UsageLeavesWithCountsProperty"/>.
/// </para>
/// </remarks>
public sealed class SummarySlot : Panel
{
    /// <summary>
    /// Whether the usage gets no room when the counts have dropped a count: set on the caption, not on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>In the caption, counts that drop move to the row and leave their place empty</strong> (T1.43): the
    /// place is held, so the width that decided to move them does not change. The usage must not take the
    /// pixels that are left in that place: it would then show beside an empty gap, in the place the operator
    /// reads the counts in. So when the counts leave the caption, the usage is measured against nothing, drops,
    /// and leaves with them. Both are then on the row, the usage at the left and the counts at the right.
    /// </para>
    /// <para>
    /// <strong>On the row this is off.</strong> There the counts are present or collapsed, never held, and counts
    /// that drop a count on the row stay on the row; the usage takes what is left as usual.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty UsageLeavesWithCountsProperty =
        DependencyProperty.Register(
            nameof(UsageLeavesWithCounts),
            typeof(bool),
            typeof(SummarySlot),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>See <see cref="UsageLeavesWithCountsProperty"/>.</summary>
    public bool UsageLeavesWithCounts
    {
        get => (bool)GetValue(UsageLeavesWithCountsProperty);
        set => SetValue(UsageLeavesWithCountsProperty, value);
    }

    /// <summary>The room the usage was measured against at the last measure, for tests.</summary>
    internal double UsageRoom { get; private set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2)
        {
            throw new InvalidOperationException("A SummarySlot holds two children: the counts, then the usage.");
        }

        var counts = InternalChildren[0];
        var usage = InternalChildren[1];

        counts.Measure(availableSize);

        var countsLeft = UsageLeavesWithCounts
            && counts.Visibility != Visibility.Collapsed
            && counts is IFittingStripHost { Strip.HasDropped: true };

        UsageRoom = countsLeft ? 0 : Math.Max(0, availableSize.Width - counts.DesiredSize.Width);
        usage.Measure(new Size(UsageRoom, availableSize.Height));

        return new Size(
            counts.DesiredSize.Width + usage.DesiredSize.Width,
            Math.Max(counts.DesiredSize.Height, usage.DesiredSize.Height));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var counts = InternalChildren[0];
        var usage = InternalChildren[1];

        counts.Arrange(new Rect(finalSize));

        // Never narrower than the usage measured: the pixel rounding of the arrange can take a fraction of a
        // DIP off the room, and a FittingStrip is drawn as measured, not judged again.
        var left = Math.Max(Math.Max(0, finalSize.Width - counts.DesiredSize.Width), usage.DesiredSize.Width);

        usage.Arrange(new Rect(0, 0, left, finalSize.Height));

        return finalSize;
    }
}
