using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// A horizontal strip that shortens its labels while it can, and drops whole counts once it
/// cannot.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the caption needs one.</strong> Design option 2c draws its summary slot in an
/// 820-pixel window and puts two counts in it, "11 sessions · 3 need you". This window opens at
/// 520 (<see cref="WindowPlacement.FallbackWidth"/>) and carries four, because the counts strip
/// moved into the caption rather than being redrawn for it — so the strip wants about 242 device
/// -independent pixels where the slot offers 150.
/// </para>
/// <para>
/// <strong>Two answers, in that order.</strong> First shorten: the strip walks a ladder of label
/// sets and takes the longest one that fits whole. Only when even the shortest will not fit does
/// it start dropping counts from the right. Words are cheaper to lose than numbers, so the words
/// go first.
/// </para>
/// <para>
/// <strong>Whole counts, never half a word.</strong> A <c>StackPanel</c> in a starred column
/// clips wherever the pixels run out, and "8 wor" is worse than nothing: it reads as a rendering
/// fault rather than as a count. This drops a child entirely or shows it entirely.
/// </para>
/// <para>
/// <strong>A PREFIX, WHICH MEANS THE FIRST MISS ENDS IT.</strong> Carrying on to try later,
/// narrower children would fill the gap with whichever ones happened to fit — and every count
/// after the first carries its own leading separator, so the caption would read
/// "<c>· 5 unread</c>", a separator dangling off nothing. Reachable, too: the four counts are
/// within about twelve pixels of each other, so a width that refuses the total refuses it by a
/// margin that the next one along would slip inside.
/// </para>
/// <para>
/// <strong>Choosing, not reacting.</strong> The tier is picked from the available width in one
/// measure pass. The obvious alternative — notice a count was dropped, then shorten and try
/// again — is a layout loop: shortening changes what fits, which can un-drop the count that
/// triggered the shortening, which makes the long labels fit again. This cannot oscillate,
/// because the strip sits in a starred grid column whose width is decided by the fixed columns
/// beside it and never by the strip's own content. The tier is therefore a pure function of a
/// width that the tier cannot change.
/// </para>
/// <para>
/// A child already collapsed — every count is, at zero — measures to nothing, costs no room, and
/// so cannot push a later one out. The counts' own visibility bindings are never written to; the
/// only thing this writes is <see cref="LabelsProperty"/> text and
/// <see cref="HideAtTierProperty"/> visibility, on elements that carry those properties and
/// nothing else.
/// </para>
/// </remarks>
public sealed class FittingStrip : Panel
{
    /// <summary>Separates one tier's label from the next in <see cref="LabelsProperty"/>.</summary>
    private const char TierSeparator = '|';

    /// <summary>
    /// The label this element shows at each tier, longest first, separated by
    /// '<c>|</c>' — for example "<c> need you| need</c>".
    /// </summary>
    /// <remarks>
    /// A tier past the end of the list uses the last entry, so a label that only shortens once
    /// is written once and not repeated.
    /// <para>
    /// <strong>Do not bind the Text of an element that carries this.</strong> The strip assigns
    /// Text locally, and a local value outranks a binding permanently. Anything whose text comes
    /// from the view model uses <see cref="HideAtTierProperty"/> instead, which touches
    /// visibility and leaves the binding alone.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty LabelsProperty =
        DependencyProperty.RegisterAttached(
            "Labels",
            typeof(string),
            typeof(FittingStrip),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Sets <see cref="LabelsProperty"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static void SetLabels(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(LabelsProperty, value);
    }

    /// <summary>Reads <see cref="LabelsProperty"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static string? GetLabels(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (string?)element.GetValue(LabelsProperty);
    }

    /// <summary>
    /// The tier at which this element stops being shown — 1 means "gone from the first
    /// shortening on".
    /// </summary>
    /// <remarks>
    /// For a word whose text is bound, which <see cref="LabelsProperty"/> cannot touch without
    /// destroying the binding. The dropped word costs its whole width, so this is also the
    /// cheapest rung on the ladder.
    /// </remarks>
    public static readonly DependencyProperty HideAtTierProperty =
        DependencyProperty.RegisterAttached(
            "HideAtTier",
            typeof(int),
            typeof(FittingStrip),
            new FrameworkPropertyMetadata(
                int.MaxValue,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Sets <see cref="HideAtTierProperty"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static void SetHideAtTier(DependencyObject element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(HideAtTierProperty, value);
    }

    /// <summary>Reads <see cref="HideAtTierProperty"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static int GetHideAtTier(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (int)element.GetValue(HideAtTierProperty);
    }

    /// <summary>Every tagged element under this strip, found once and kept.</summary>
    /// <remarks>
    /// <para>
    /// Walked once per change rather than once per measure, because measure runs on every frame
    /// of a resize and the subtree is a dozen elements.
    /// </para>
    /// <para>
    /// <strong>REBUILT WHEN THIS PANEL'S OWN CHILDREN CHANGE, WHICH IS NOT THE SAME AS WHEN THE
    /// TAGGED ELEMENTS CHANGE.</strong> <see cref="OnVisualChildrenChanged"/> fires for the
    /// panel's direct visual children only, and the tagged words are two levels down. That is
    /// sound for the caption, whose every child is an inline <c>StackPanel</c> of literal
    /// <c>TextBlock</c>s declared in the markup and never replaced. It would not be sound for a
    /// child that builds its own content later — a templated control, or anything items-generated
    /// — because the words would appear after the walk and the cache would stay stale with
    /// nothing to say so. A strip fed that way needs an invalidation hook this does not have.
    /// </para>
    /// </remarks>
    private List<(FrameworkElement Element, string[] Labels, int HideAt)>? _tagged;

    /// <summary>How many tiers the tagged elements between them describe.</summary>
    private int _tiers = 1;

    /// <summary>The tier in force, for the caption to report and for tests to read.</summary>
    public int Tier { get; private set; }

    /// <inheritdoc/>
    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);

        _tagged = null;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        Collect();

        var height = 0.0;
        var chosen = 0;

        // Longest first. The first tier whose children all fit is the one worth showing; if none
        // fits, the last one tried is the shortest there is, and the prefix below takes over.
        for (var tier = 0; tier < _tiers; tier++)
        {
            chosen = tier;
            Apply(tier);

            var wanted = 0.0;

            foreach (UIElement child in InternalChildren)
            {
                // Measured against infinity: what a child wants has to be known before it can be
                // judged against what is left, and a child measured against the remainder would
                // shrink to fit instead of being counted as too wide.
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                wanted += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }

            if (wanted <= availableSize.Width)
            {
                break;
            }
        }

        Tier = chosen;

        var used = 0.0;
        var full = true;

        foreach (UIElement child in InternalChildren)
        {
            var wanted = child.DesiredSize;

            // The strip is as tall as its tallest child whether or not that child is shown, so
            // the caption's line does not jump as counts come and go.
            height = Math.Max(height, wanted.Height);

            if (!full)
            {
                continue;
            }

            if (used + wanted.Width <= availableSize.Width)
            {
                used += wanted.Width;
            }
            else
            {
                // The first child that will not fit ends the strip. See the prefix remark above.
                full = false;
            }
        }

        return new Size(used, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var full = true;

        foreach (UIElement child in InternalChildren)
        {
            var wanted = child.DesiredSize;

            if (full && x + wanted.Width <= finalSize.Width)
            {
                child.Arrange(new Rect(x, 0, wanted.Width, finalSize.Height));
                x += wanted.Width;

                continue;
            }

            // No room, and none for anything after it either. Arranged empty rather than
            // collapsed, so the count's own visibility binding is left alone and the child comes
            // back the moment the window widens.
            full = false;
            child.Arrange(default);
        }

        return finalSize;
    }

    /// <summary>Puts tier <paramref name="tier"/>'s words on the tagged elements.</summary>
    /// <remarks>
    /// <para>
    /// <strong>THE INVALIDATION IS EXPLICIT, AND IT HAS TO BE.</strong> Assigning
    /// <see cref="TextBlock.Text"/> normally dirties the element and every ancestor, and a later
    /// <c>Measure</c> re-runs them. That does not hold from inside this panel's own measure
    /// pass: the ancestor walk does not reliably mark the segment between the changed word and
    /// this panel, so <c>Measure</c> on the segment returns the width it had at the previous
    /// tier. The strip then reserves room for "<c> need you</c>" while drawing "<c> need</c>",
    /// and the caption shows a gap where the missing word was. Measured rather than reasoned:
    /// the shortening saved 46 pixels where the two words it drops are worth 66.
    /// </para>
    /// <para>
    /// Assigning a value equal to the one already there changes nothing and invalidates nothing,
    /// which is what keeps a settled width to one measure pass.
    /// </para>
    /// </remarks>
    private void Apply(int tier)
    {
        if (_tagged is null)
        {
            return;
        }

        foreach (var (element, labels, hideAt) in _tagged)
        {
            var changed = false;

            if (labels.Length > 0 && element is TextBlock block)
            {
                var text = labels[Math.Min(tier, labels.Length - 1)];

                if (!string.Equals(block.Text, text, StringComparison.Ordinal))
                {
                    block.Text = text;
                    changed = true;
                }
            }

            if (hideAt != int.MaxValue)
            {
                var wanted = tier >= hideAt ? Visibility.Collapsed : Visibility.Visible;

                if (element.Visibility != wanted)
                {
                    element.Visibility = wanted;
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            // Dirty the whole chain from the word up to this panel, so the segment really does
            // re-measure rather than hand back the previous tier's width.
            element.InvalidateMeasure();

            for (var parent = VisualTreeHelper.GetParent(element);
                 parent is not null && !ReferenceEquals(parent, this);
                 parent = VisualTreeHelper.GetParent(parent))
            {
                (parent as UIElement)?.InvalidateMeasure();
            }
        }
    }

    /// <summary>Finds the tagged elements, and how many tiers they describe between them.</summary>
    private void Collect()
    {
        if (_tagged is not null)
        {
            return;
        }

        _tagged = [];
        _tiers = 1;

        foreach (UIElement child in InternalChildren)
        {
            Walk(child);
        }

        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement element)
            {
                var labels = GetLabels(element);
                var hideAt = GetHideAtTier(element);

                if (labels is not null || hideAt != int.MaxValue)
                {
                    var parts = labels?.Split(TierSeparator) ?? [];

                    _tagged!.Add((element, parts, hideAt));
                    _tiers = Math.Max(_tiers, parts.Length);

                    if (hideAt != int.MaxValue)
                    {
                        _tiers = Math.Max(_tiers, hideAt + 1);
                    }
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(node);

            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
