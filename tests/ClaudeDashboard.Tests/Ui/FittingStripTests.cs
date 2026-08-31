using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The caption's summary strip: which words it spells out, and what it gives up first
/// (design option 2c).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No window and no automation.</strong> The strip is a panel, so it can be measured and
/// arranged on its own. All it needs from the harness is an STA thread, because a
/// <see cref="TextBlock"/> is thread-affine like everything else in WPF.
/// </para>
/// <para>
/// <strong>The tree is built here rather than loaded from the markup</strong>, and that is the
/// one thing in this file to keep honest: it is a copy of what <c>MainWindow.xaml</c> declares
/// and the two can drift. It is built the same way for the same reasons — four segments, each
/// count carrying its own leading separator, the needs-you word carrying two tiers, and the
/// sessions word hidden by tier rather than shortened because in the caption that word is bound
/// and a local write would kill the binding. <c>MainWindowTests</c> is what proves the markup
/// really is this shape.
/// </para>
/// <para>
/// <strong>EVERY TEXT PROPERTY THE CAPTION SETS, THIS FIXTURE SETS. THAT COMPLETENESS IS
/// LOAD-BEARING AND NOT INCIDENTAL</strong> — a fixture one property short does not fail, it
/// quietly describes a different strip, reproduces its own figures on demand, and is wrong about
/// the only thing it exists to state. Three properties decide these widths, and leaving out any
/// one of them moves the tier boundary:
/// </para>
/// <para>
/// <c>FontFamily</c>, the caption's own <c>UiFont</c> rather than the default. •
/// <c>TextOptions.TextFormattingMode="Display"</c>, which <c>MainWindow.xaml</c> puts on the
/// window and every one of these <see cref="TextBlock"/>s inherits; it quantizes glyph advances
/// to whole pixels. • <c>Typography.NumeralAlignment="Tabular"</c>, which the markup puts on the
/// four numbers and on none of the words, and which is wider in this face.
/// </para>
/// <para>
/// Measured on this machine, one variable at a time: the default face in Ideal mode makes the
/// long form 246.1; the caption's face in Display mode makes it 242; the caption's face in
/// Display mode <em>with tabular figures</em> makes it <strong>244</strong>, and 244 is what the
/// window has. Two earlier measurements of this landed on 245.5 and on 242, and both were a
/// fixture short rather than wrong about the strip. These widths are the caption's widths.
/// </para>
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class FittingStripTests(StaHarness harness)
{
    /// <summary>The counts the width budget was measured against: 11 sessions, 3, 5 and 8.</summary>
    private const int Sessions = 11;
    private const int NeedsYou = 3;
    private const int Unread = 5;
    private const int Working = 8;

    /// <summary>What the slot offers at the 520 the window opens at.</summary>
    /// <remarks>
    /// 520 less the five fixed columns beside the strip — icon 48, title 121, divider 29, help
    /// slot 20, window buttons 138 — and less the strip's own 14-pixel left margin, which WPF
    /// takes off before <c>MeasureOverride</c> ever sees the width.
    /// </remarks>
    private const double SlotAtDefaultWindow = 150;

    private readonly StaHarness _harness = harness;

    /// <summary>What the strip did at one width.</summary>
    /// <param name="Tier">The label set it settled on.</param>
    /// <param name="Desired">How wide it asked to be.</param>
    /// <param name="Shown">Whether each of the four counts got a place, in order.</param>
    /// <param name="NeedsYouWord">The needs-you label as it would render.</param>
    private sealed record Result(int Tier, double Desired, IReadOnlyList<bool> Shown, string NeedsYouWord)
    {
        /// <summary>How many counts were given a place.</summary>
        public int Counts => Shown.Count(seen => seen);
    }

    /// <summary>Measures and arranges a strip in a slot <paramref name="slot"/> wide.</summary>
    /// <remarks>
    /// Arranged at what it asked for rather than at the whole slot, which is what the caption
    /// does: the strip is right-aligned in its column, so WPF arranges it at its desired size.
    /// </remarks>
    private Result At(double slot, int unread = Unread, int working = Working, bool longFormOnly = false) =>
        _harness.Invoke(() =>
        {
            var strip = Build(unread, working, longFormOnly);

            strip.Measure(new Size(slot, 48));
            strip.Arrange(new Rect(0, 0, strip.DesiredSize.Width, 48));

            // THE LAYOUT SLOT, NOT RenderSize. The slot is the rect Arrange was handed — the
            // panel's decision itself rather than a consequence of it — and a dropped count gets
            // an empty one. RenderSize is the wrong question and answers it wrongly: WPF keeps a
            // child's ink size when the arrange rect is smaller than its desired size, and hides
            // it with a layout clip instead. So RenderSize is non-zero for every count at every
            // width, dropped or not. That the layout clip is what really hides them is also why
            // the caption's ClipToBounds is a second line of defence rather than the first.
            var shown = strip.Children
                .Cast<FrameworkElement>()
                .Select(child => LayoutInformation.GetLayoutSlot(child).Width > 0)
                .ToList();

            return new Result(strip.Tier, strip.DesiredSize.Width, shown, NeedsYouWordOf(strip));
        });

    // ---- The ladder ----------------------------------------------------------------------------

    /// <summary>
    /// The strip takes the longest label set that fits, and shortens only when it must.
    /// </summary>
    /// <remarks>
    /// 244 and 243 are the boundary either side: the long form measures 244, so a slot of 244
    /// holds it exactly and a slot of 243 does not. Written as the two widths rather than as the
    /// number, because the number is a font metric and the behaviour is the rule — and because a
    /// pair either side pins the width exactly, where an assertion on the number alone would
    /// carry whatever tolerance it was given.
    /// </remarks>
    [Theory]
    [InlineData(530, 0)]
    [InlineData(244, 0)]
    [InlineData(243, 1)]
    [InlineData(200, 1)]
    [InlineData(150, 1)]
    [InlineData(30, 1)]
    public void The_strip_takes_the_longest_tier_that_fits(double slot, int expected)
    {
        Assert.Equal(expected, At(slot).Tier);
    }

    /// <summary>
    /// Tier 0 spells the words out, which nothing else asserts.
    /// </summary>
    /// <remarks>
    /// <c>MainWindowTests</c> checks the needs-you band by its stem, because it realizes its
    /// window at 400 where the caption is legitimately in the short tier. So the long form is
    /// this file's to prove, and without this test nothing anywhere would ever see " need you".
    /// </remarks>
    [Fact]
    public void Tier_zero_spells_the_words_out()
    {
        var wide = At(530);

        Assert.Equal(0, wide.Tier);
        Assert.Equal(" need you", wide.NeedsYouWord);
    }

    /// <summary>And the short tier really is shorter, in the one word that carries two forms.</summary>
    [Fact]
    public void Tier_one_shortens_the_only_word_worth_shortening()
    {
        Assert.Equal(" need", At(200).NeedsYouWord);
    }

    // ---- Words before numbers ------------------------------------------------------------------

    /// <summary>
    /// <strong>The rule the whole class exists for.</strong> A slot too narrow for the long form
    /// but wide enough for the short one keeps all four counts: the words go first, the counts
    /// stay.
    /// </summary>
    [Theory]
    [InlineData(243)]
    [InlineData(200)]
    [InlineData(175)]
    public void It_shortens_before_it_drops(double slot)
    {
        var tight = At(slot);

        Assert.Equal(1, tight.Tier);
        Assert.Equal(4, tight.Counts);
    }

    /// <summary>
    /// And the shortening buys a real count: at the width the window opens at, the long form
    /// shows two and the short one shows three.
    /// </summary>
    /// <remarks>
    /// This is the whole return on the tier ladder, so it is asserted as the comparison rather
    /// than as a number — three is only worth having because the alternative was two. The
    /// long-form strip is the same tree with the ladder taken away, which is what the caption
    /// was before the tiers.
    /// </remarks>
    [Fact]
    public void The_window_it_opens_at_shows_one_more_count_for_the_shortening()
    {
        var shortened = At(SlotAtDefaultWindow);
        var longForm = At(SlotAtDefaultWindow, longFormOnly: true);

        Assert.Equal(1, shortened.Tier);
        Assert.Equal(3, shortened.Counts);

        Assert.Equal(0, longForm.Tier);
        Assert.Equal(2, longForm.Counts);
    }

    // ---- What is dropped, and in what order ----------------------------------------------------

    /// <summary>
    /// What survives is always a prefix: once one count is dropped, everything after it is too.
    /// </summary>
    /// <remarks>
    /// The rule exists because every count but the first carries its own leading separator. A
    /// strip that filled the gap with a later, narrower count would render "<c>· 5 unread</c>",
    /// a separator hanging off nothing. Asserted across the whole ladder rather than at one
    /// width, because the failure is a width that happens to admit a later count.
    /// </remarks>
    [Theory]
    [InlineData(530)]
    [InlineData(244)]
    [InlineData(243)]
    [InlineData(200)]
    [InlineData(175)]
    [InlineData(174)]
    [InlineData(150)]
    [InlineData(120)]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(0)]
    public void What_survives_is_a_prefix(double slot)
    {
        var shown = At(slot).Shown;
        var firstGap = shown.ToList().FindIndex(seen => !seen);

        if (firstGap < 0)
        {
            // Everything is showing, which is a prefix of itself.
            return;
        }

        Assert.All(shown.Skip(firstGap), Assert.False);
    }

    /// <summary>The counts go one at a time, from the right.</summary>
    [Theory]
    [InlineData(530, 4)]
    [InlineData(175, 4)]
    [InlineData(174, 3)]
    [InlineData(150, 3)]
    [InlineData(113, 3)]
    [InlineData(112, 2)]
    [InlineData(110, 2)]
    [InlineData(30, 1)]
    [InlineData(12, 1)]
    [InlineData(11, 0)]
    [InlineData(9, 0)]
    public void It_gives_up_one_count_at_a_time(double slot, int expected)
    {
        Assert.Equal(expected, At(slot).Counts);
    }

    /// <summary>A slot with room for nothing shows nothing, rather than overflowing it.</summary>
    /// <remarks>
    /// The caption puts the strip in a starred grid column, so this is what keeps the counts off
    /// the help slot beside them: the strip never asks for more than it was offered, at any
    /// width, so there is nothing to overrun with.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(30)]
    [InlineData(110)]
    [InlineData(150)]
    [InlineData(243)]
    [InlineData(244)]
    [InlineData(530)]
    public void It_never_asks_for_more_room_than_the_slot(double slot)
    {
        var measured = At(slot);

        Assert.True(
            measured.Desired <= slot,
            $"A strip in a {slot}-wide slot asked for {measured.Desired}.");
    }

    /// <summary>A count of zero collapses, costs nothing, and cannot push a later one out.</summary>
    [Fact]
    public void A_zero_count_takes_its_separator_with_it()
    {
        var withZero = At(242, unread: 0);

        Assert.Equal(0, withZero.Tier);
        Assert.False(withZero.Shown[2]);
        Assert.True(withZero.Shown[3]);
    }

    // ---- The measured widths -------------------------------------------------------------------

    /// <summary>
    /// The widths the caption's remarks and the execution plan quote, asserted so they cannot
    /// drift from the code without something going red.
    /// </summary>
    /// <remarks>
    /// A tolerance rather than equality, because these are font metrics: a Windows build shipping
    /// a different cut of Segoe UI Variable Text would move them slightly, and that should read
    /// as "the numbers moved" rather than as a broken strip.
    /// </remarks>
    [Theory]
    [InlineData(530, 244)]
    [InlineData(244, 244)]
    [InlineData(243, 175)]
    [InlineData(175, 175)]
    [InlineData(174, 113)]
    [InlineData(150, 113)]
    [InlineData(30, 12)]
    public void The_quoted_widths_are_what_it_measures(double slot, double expected)
    {
        Assert.Equal(expected, At(slot).Desired, 1.0);
    }

    // ---- Building the thing MainWindow.xaml declares --------------------------------------------

    /// <summary>
    /// The caption's four segments.
    /// </summary>
    /// <param name="unread">The unread count, so a zero can be exercised.</param>
    /// <param name="working">The working count.</param>
    /// <param name="longFormOnly">
    /// Builds the strip without a ladder — one label set, nothing hidden by tier. What the
    /// caption was before the tiers, and the thing the tiers are worth measuring against.
    /// </param>
    private static FittingStrip Build(int unread, int working, bool longFormOnly)
    {
        var strip = new FittingStrip();

        TextOptions.SetTextFormattingMode(strip, TextFormattingMode.Display);

        var total = new StackPanel { Orientation = Orientation.Horizontal };
        total.Children.Add(Number(Sessions, semiBold: false));

        var sessionsWord = Word(Sessions == 1 ? " session" : " sessions");

        if (!longFormOnly)
        {
            FittingStrip.SetHideAtTier(sessionsWord, 1);
        }

        total.Children.Add(sessionsWord);
        strip.Children.Add(total);

        strip.Children.Add(Count(NeedsYou, longFormOnly ? " need you" : null, longFormOnly ? null : " need you| need"));
        strip.Children.Add(Count(unread, " unread"));
        strip.Children.Add(Count(working, " working"));

        return strip;
    }

    /// <summary>One count: its separator, its number in semibold, and its word.</summary>
    private static StackPanel Count(int value, string? label = null, string? tiers = null)
    {
        var segment = new StackPanel
        {
            Orientation = Orientation.Horizontal,

            // What ZeroToCollapsed does in the caption.
            Visibility = value == 0 ? Visibility.Collapsed : Visibility.Visible,
        };

        segment.Children.Add(Word(" · "));
        segment.Children.Add(Number(value, semiBold: true));

        var word = Word(label ?? string.Empty);

        if (tiers is not null)
        {
            FittingStrip.SetLabels(word, tiers);
        }

        segment.Children.Add(word);

        return segment;
    }

    /// <summary>A run of the caption's summary text, at the face and size the markup sets.</summary>
    private static TextBlock Word(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 12,
    };

    /// <summary>
    /// A count's digits: tabular, which is what the caption sets and what the words are not.
    /// </summary>
    /// <remarks>
    /// <c>MainWindow.xaml</c> puts <c>Typography.NumeralAlignment="Tabular"</c> on exactly four
    /// blocks — the four numbers — and on none of the words or separators. Tabular digits are
    /// wider in this face, by two device-independent pixels across the five digits of
    /// 11/3/5/8, and that is two pixels of tier boundary.
    /// </remarks>
    private static TextBlock Number(int value, bool semiBold)
    {
        var block = Word(value.ToString(CultureInfo.CurrentCulture));

        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);

        if (semiBold)
        {
            block.FontWeight = FontWeights.SemiBold;
        }

        return block;
    }

    /// <summary>The needs-you label, which is the third block of the second segment.</summary>
    private static string NeedsYouWordOf(FittingStrip strip) =>
        ((TextBlock)((StackPanel)strip.Children[1]).Children[2]).Text;
}
