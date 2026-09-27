using Xunit.Abstractions;
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
/// <strong>ALMOST NOTHING HERE QUOTES A PIXEL, AND THAT IS THE POINT.</strong> Four inputs decide
/// what this strip measures: the <c>UiFont</c> family, <c>TextFormattingMode.Display</c>,
/// <c>Typography.NumeralAlignment.Tabular</c> on the four numbers — and the display scale, which
/// is not a property on anything and cannot be owned from inside a test. Display mode quantizes
/// glyph advances to whole DEVICE pixels, so every absolute width is a function of the monitor:
/// "11" is 12 at 100% and 13.333 at 150%. That is a quantum, not a rendering mode —
/// <em>Ideal</em> measures 12.94, so neither figure is Ideal and the mode was never what moved.
/// </para>
/// <para>
/// <see cref="VisualTreeHelper.SetRootDpi"/> looks like the answer and is not. Measured, not
/// assumed: it moves what <c>GetDpi</c> reports but, once anything in the process has realized a
/// window, stops moving what the text stack measures against — buying a fixture that reports
/// 100%, measures at 150%, and passes its own DPI assertion while every width fails. It also
/// latches, so the first scale set on the thread is the only one a run can have.
/// </para>
/// <para>
/// So the rules are stated against widths the strip is <em>asked</em> for, and hold at any scale
/// on any machine. Only <see cref="The_recorded_ladder"/> quotes the numbers the caption's
/// remarks and the execution plan carry. It checks them three ways so that being unable to
/// verify them exactly is never the same as not checking them: the ladder's <em>shape</em> and
/// its <em>range</em> are asserted at every scale, the five exact widths only where the scale
/// they were recorded at still holds, and which of those happened is written to the test's
/// output rather than left to a comment. A recorded figure edited to something impossible fails
/// on any machine; that was verified by planting one.
/// </para>
/// <para>
/// Five measurements of this strip have now been wrong. Four were a missing input — the face,
/// the numerals, the scale, and then the belief that the scale could be pinned. The fifth
/// missed nothing and measured correctly: 242 was the pre-tabular width at 100%, written down
/// as a 150% one. A right number under a wrong label reads exactly like a measurement, survives
/// every check that asks whether the arithmetic holds, and is caught only by someone
/// re-measuring the thing it claims to be about.
/// </para>
/// <para>
/// The lesson is none of the five: a fixture quoting absolute pixels either owns every input to
/// them or does not quote them — and a remark quoting one says what it is of, because that is
/// the half a number cannot carry by itself.
/// </para>
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class FittingStripTests(StaHarness harness, ITestOutputHelper output)
{
    /// <summary>The counts the width budget was measured against: 11 sessions, 3, 5 and 8.</summary>
    private const int Sessions = 11;
    private const int NeedsYou = 3;
    private const int Unread = 5;
    private const int Working = 8;

    /// <summary>Wider than the strip can ever want, so its own answer comes back.</summary>
    private const double Unbounded = double.PositiveInfinity;

    /// <summary>The long form, at 100% display scaling.</summary>
    private const double RecordedTierZero = 244;

    /// <summary>The short form, at 100% display scaling.</summary>
    private const double RecordedTierOne = 175;

    /// <summary>Numbers only, all four: "11 · 3 · 5 · 8", at 100% display scaling (T1.39).</summary>
    private const double RecordedTierTwo = 60;

    /// <summary>
    /// Total, needs-you and unread as numbers only, at 100% display scaling. It was 113 before
    /// T1.39, when the third rung still carried "need" and "unread"; tier 2 comes first now.
    /// </summary>
    private const double RecordedThreeCounts = 44;

    /// <summary>"11" alone, at 100% scaling — and the probe for whether that is still the scale.</summary>
    private const double RecordedOneCount = 12;

    private readonly StaHarness _harness = harness;

    /// <summary>What the strip did at one width.</summary>
    /// <param name="Tier">The label set it settled on.</param>
    /// <param name="Desired">How wide it asked to be.</param>
    /// <param name="Shown">Whether each of the four counts got a place, in order.</param>
    /// <param name="NeedsYouWord">The needs-you label as it would render.</param>
    /// <param name="Text">
    /// What a reader sees: the visible runs of every count given a place, joined — so
    /// "11 · 3 · 5 · 8" at tier 2. Text, not pixels, which is what lets the order of the ladder
    /// be asserted at any display scale.
    /// </param>
    private sealed record Result(int Tier, double Desired, IReadOnlyList<bool> Shown, string NeedsYouWord, string Text, bool IsShortened)
    {
        /// <summary>How many counts were given a place.</summary>
        public int Counts => Shown.Count(seen => seen);
    }

    /// <summary>Which rungs of the ladder the strip is built with.</summary>
    private enum Rungs
    {
        /// <summary>What <c>MainWindow.xaml</c> declares: tiers 0, 1 and 2.</summary>
        Full,

        /// <summary>The ladder before T1.39: tier 2 taken away, so counts drop after tier 1.</summary>
        UpToTierOne,

        /// <summary>No ladder at all: the long form, and nothing hidden by tier.</summary>
        LongFormOnly,
    }

    /// <summary>Measures and arranges a strip in a slot <paramref name="slot"/> wide.</summary>
    /// <remarks>
    /// Arranged at what it asked for rather than at the whole slot, which is what the caption
    /// does: the strip is right-aligned in its column, so WPF arranges it at its desired size.
    /// </remarks>
    private Result At(double slot, int unread = Unread, int working = Working, Rungs rungs = Rungs.Full) =>
        _harness.Invoke(() =>
        {
            var strip = Build(unread, working, rungs);

            strip.Measure(new Size(slot, 48));
            strip.Arrange(new Rect(0, 0, strip.DesiredSize.Width, 48));

            // THE LAYOUT SLOT, NOT RenderSize. The slot is the rect Arrange was handed — the
            // panel's decision itself rather than a consequence of it — and a dropped count gets
            // an empty one. RenderSize is the wrong question and answers it wrongly: WPF keeps a
            // child's ink size when the arrange rect is smaller than its desired size and hides
            // it with a layout clip instead, so RenderSize is non-zero for every count at every
            // width, dropped or not. That the layout clip is what really hides them is also why
            // the caption's ClipToBounds is a second line of defence rather than the first.
            var shown = strip.Children
                .Cast<FrameworkElement>()
                .Select(child => LayoutInformation.GetLayoutSlot(child).Width > 0)
                .ToList();

            var text = string.Concat(strip.Children
                .Cast<StackPanel>()
                .Where((segment, i) => shown[i] && segment.Visibility == Visibility.Visible)
                .SelectMany(segment => segment.Children.Cast<TextBlock>())
                .Where(run => run.Visibility == Visibility.Visible)
                .Select(run => run.Text));

            return new Result(strip.Tier, strip.DesiredSize.Width, shown, NeedsYouWordOf(strip), text, strip.IsShortened);
        });

    /// <summary>The widths the strip steps down through, widest first.</summary>
    /// <remarks>
    /// Asked for rather than written down: each is what the strip says it wants once the one
    /// above it will not fit. This is the ladder every rule below is stated against, and it is
    /// what makes them true at any display scale. Tier 2, numbers only, sits between tier 1 and
    /// the first dropped count (T1.39).
    /// </remarks>
    private (double TierZero, double TierOne, double TierTwo, double ThreeCounts, double OneCount) Ladder()
    {
        var tierZero = At(Unbounded).Desired;
        var tierOne = At(tierZero - 1).Desired;
        var tierTwo = At(tierOne - 1).Desired;
        var threeCounts = At(tierTwo - 1).Desired;
        var twoCounts = At(threeCounts - 1).Desired;
        var oneCount = At(twoCounts - 1).Desired;

        return (tierZero, tierOne, tierTwo, threeCounts, oneCount);
    }

    // ---- The tier ladder -----------------------------------------------------------------------

    /// <summary>
    /// The strip takes the longest label set that fits, and shortens only when it must.
    /// </summary>
    /// <remarks>
    /// Stated either side of the strip's own tier-0 width rather than at a number: a slot of
    /// exactly that width holds the long form and one pixel less does not, which pins the
    /// boundary exactly without quoting where it falls.
    /// </remarks>
    [Fact]
    public void The_strip_takes_the_longest_tier_that_fits()
    {
        var tierZero = At(Unbounded).Desired;

        Assert.Equal(0, At(Unbounded).Tier);
        Assert.Equal(0, At(tierZero).Tier);
        Assert.Equal(1, At(tierZero - 1).Tier);
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
        var wide = At(Unbounded);

        Assert.Equal(0, wide.Tier);
        Assert.Equal(" need you", wide.NeedsYouWord);
    }

    /// <summary>And the short tier really is shorter, in the one word that carries two forms.</summary>
    [Fact]
    public void Tier_one_shortens_the_only_word_worth_shortening()
    {
        var tierZero = At(Unbounded).Desired;

        Assert.Equal(" need", At(tierZero - 1).NeedsYouWord);
    }

    // ---- Words before numbers ------------------------------------------------------------------

    /// <summary>
    /// <strong>The rule the whole class exists for.</strong> A slot too narrow for the long form
    /// but wide enough for the short one keeps all four counts: the words go first, the counts
    /// stay.
    /// </summary>
    [Fact]
    public void It_shortens_before_it_drops()
    {
        var (tierZero, tierOne, _, _, _) = Ladder();

        foreach (var slot in new[] { tierZero - 1, tierOne, (tierZero + tierOne) / 2 })
        {
            var tight = At(slot);

            Assert.Equal(1, tight.Tier);
            Assert.Equal(4, tight.Counts);
        }
    }

    /// <summary>
    /// And the shortening buys a real count: at a width the long form cannot hold, the shortened
    /// one still shows everything.
    /// </summary>
    /// <remarks>
    /// This is the whole return on the tier ladder, so it is asserted as the comparison rather
    /// than as a number. The long-form strip is the same tree with the ladder taken away, which
    /// is what the caption was before the tiers.
    /// </remarks>
    [Fact]
    public void Shortening_buys_a_count_the_long_form_would_have_dropped()
    {
        var (tierZero, tierOne, _, _, _) = Ladder();

        // Between the two tier widths: too narrow for the long form, wide enough for the short.
        var slot = (tierZero + tierOne) / 2;

        Assert.Equal(4, At(slot).Counts);
        Assert.True(
            At(slot, rungs: Rungs.LongFormOnly).Counts < 4,
            $"Without the ladder the strip should already have dropped a count in a {slot}-wide slot.");
    }

    // ---- Numbers before no numbers (T1.39, issue #53) ------------------------------------------

    /// <summary>The long form, as a reader sees it.</summary>
    private static readonly string TierZeroText =
        $"{Sessions} sessions · {NeedsYou} need you · {Unread} unread · {Working} working";

    /// <summary>The short form.</summary>
    private static readonly string TierOneText =
        $"{Sessions} · {NeedsYou} need · {Unread} unread · {Working} working";

    /// <summary>Numbers only: the colours name the counts.</summary>
    private static readonly string TierTwoText = $"{Sessions} · {NeedsYou} · {Unread} · {Working}";

    /// <summary>
    /// <strong>Tier 2 is numbers only, and it keeps all four.</strong>
    /// </summary>
    /// <remarks>
    /// One pixel short of tier 1 is where the ladder before T1.39 dropped the working count
    /// while "unread" was still showing — the operator's report, <c>3 · 1 unread</c> going
    /// straight to <c>3</c>. Now every word goes and every number stays.
    /// </remarks>
    [Fact]
    public void Tier_two_is_numbers_only_and_keeps_every_count()
    {
        var (_, tierOne, tierTwo, _, _) = Ladder();

        foreach (var slot in new[] { tierOne - 1, tierTwo, (tierOne + tierTwo) / 2 })
        {
            var narrow = At(slot);

            Assert.Equal(2, narrow.Tier);
            Assert.Equal(4, narrow.Counts);
            Assert.Equal(TierTwoText, narrow.Text);
        }
    }

    /// <summary>
    /// <strong>T1.29's rule, now kept all the way down: every word is gone before any count is
    /// dropped.</strong>
    /// </summary>
    /// <remarks>
    /// Swept one pixel at a time across the whole range, because the failure is a width. Any
    /// slot that has lost a count must be at the last tier and show no letter at all.
    /// </remarks>
    [Fact]
    public void Every_word_is_gone_before_any_count_is_dropped()
    {
        var tierZero = At(Unbounded).Desired;
        var droppedSomewhere = false;

        for (var slot = Math.Ceiling(tierZero) + 4; slot >= 0; slot--)
        {
            var measured = At(slot);

            if (measured.Counts == 4)
            {
                continue;
            }

            droppedSomewhere = true;

            Assert.Equal(2, measured.Tier);
            Assert.False(
                measured.Text.Any(char.IsLetter),
                $"A count was dropped while a word still showed, in a {slot}-wide slot: \"{measured.Text}\".");
        }

        Assert.True(droppedSomewhere, "The sweep never reached a slot narrow enough to drop a count.");
    }

    /// <summary>
    /// <strong>The whole ladder, as a reader reads it, narrowing one pixel at a time.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seven captions and no others, in this order: both word tiers, numbers only, then whole
    /// counts from the right. Stated as text rather than widths, so it holds at any display
    /// scale — and it states three rules at once. Words before counts, because every caption
    /// with a word comes first. The prefix rule, because each caption after tier 2 is the one
    /// before it with its last count removed. And no dangling separator, asserted outright:
    /// every count but the first carries its own leading " · ", so a separator at either end
    /// would mean a count was dropped and its separator was not.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_captions_narrow_in_order_and_no_separator_dangles()
    {
        var tierZero = At(Unbounded).Desired;
        var captions = new List<string>();

        for (var slot = Math.Ceiling(tierZero) + 4; slot >= 0; slot--)
        {
            var text = At(slot).Text;

            if (captions.Count == 0 || text != captions[^1])
            {
                captions.Add(text);
            }
        }

        Assert.Equal(
            [
                TierZeroText,
                TierOneText,
                TierTwoText,
                $"{Sessions} · {NeedsYou} · {Unread}",
                $"{Sessions} · {NeedsYou}",
                $"{Sessions}",
                string.Empty,
            ],
            captions);

        Assert.All(captions, caption =>
        {
            Assert.False(caption.StartsWith(" ·", StringComparison.Ordinal), $"\"{caption}\" starts with a separator.");
            Assert.False(caption.EndsWith("· ", StringComparison.Ordinal), $"\"{caption}\" ends with a separator.");
        });
    }

    /// <summary>
    /// And numbers only buys real counts: at a width tier 1 cannot hold, the ladder before T1.39
    /// had already dropped one.
    /// </summary>
    /// <remarks>
    /// The return on tier 2, asserted as the comparison the issue was filed about — the same tree
    /// with tier 2 taken away — rather than as a number.
    /// </remarks>
    [Fact]
    public void Numbers_only_keeps_a_count_the_old_ladder_dropped()
    {
        var (_, tierOne, tierTwo, _, _) = Ladder();

        var slot = (tierOne + tierTwo) / 2;

        Assert.Equal(4, At(slot).Counts);
        Assert.True(
            At(slot, rungs: Rungs.UpToTierOne).Counts < 4,
            $"Without tier 2 the strip should already have dropped a count in a {slot}-wide slot.");
    }

    /// <summary>
    /// A zero count at tier 2 is still absent, separator and all: the non-zero counts, as
    /// numbers only.
    /// </summary>
    [Fact]
    public void Tier_two_leaves_out_a_zero_count_with_its_separator()
    {
        var tierZero = At(Unbounded, unread: 0).Desired;
        var tierOne = At(tierZero - 1, unread: 0).Desired;

        var narrow = At(tierOne - 1, unread: 0);

        Assert.Equal(2, narrow.Tier);
        Assert.Equal($"{Sessions} · {NeedsYou} · {Working}", narrow.Text);
    }

    // ---- Shortened: when the counts tooltip is on (issue #53's ruling) -------------------------

    /// <summary>
    /// <strong>The strip reports itself shortened exactly when what it shows is not the long
    /// form</strong> — at every width.
    /// </summary>
    /// <remarks>
    /// The operator's ruling: the counts tooltip shows only when the visible text is shorter than
    /// the full text. The caption enables the tooltip from <see cref="FittingStrip.IsShortened"/>,
    /// so this is the ruling stated as the strip's property, swept one pixel at a time: shortened
    /// if and only if the rendered caption differs from the long form.
    /// </remarks>
    [Fact]
    public void Shortened_exactly_when_the_rendered_text_is_not_the_long_form()
    {
        var tierZero = At(Unbounded).Desired;

        for (var slot = Math.Ceiling(tierZero) + 4; slot >= 0; slot--)
        {
            var measured = At(slot);

            Assert.Equal(measured.Text != TierZeroText, measured.IsShortened);
        }
    }

    /// <summary>Not shortened where tier 0 fits whole; shortened at tier 1, tier 2, and with a
    /// count dropped.</summary>
    /// <remarks>Each at a width measured in this run, one rung of the ladder apiece.</remarks>
    [Fact]
    public void Shortened_at_every_rung_below_the_long_form()
    {
        var (tierZero, tierOne, tierTwo, threeCounts, _) = Ladder();

        Assert.False(At(Unbounded).IsShortened);
        Assert.False(At(tierZero).IsShortened);

        var one = At(tierOne);
        Assert.Equal(1, one.Tier);
        Assert.True(one.IsShortened);

        var two = At(tierTwo);
        Assert.Equal(2, two.Tier);
        Assert.Equal(4, two.Counts);
        Assert.True(two.IsShortened);

        var dropped = At(threeCounts);
        Assert.Equal(3, dropped.Counts);
        Assert.True(dropped.IsShortened);
    }

    // ---- What is dropped, and in what order ----------------------------------------------------

    /// <summary>
    /// What survives is always a prefix: once one count is dropped, everything after it is too.
    /// </summary>
    /// <remarks>
    /// The rule exists because every count but the first carries its own leading separator. A
    /// strip that filled the gap with a later, narrower count would render "<c>· 5 unread</c>",
    /// a separator hanging off nothing. Swept across the whole range rather than sampled at a few
    /// widths, because the failure is a width that happens to admit a later count.
    /// </remarks>
    [Fact]
    public void What_survives_is_a_prefix()
    {
        var tierZero = At(Unbounded).Desired;

        for (var slot = Math.Ceiling(tierZero) + 4; slot >= 0; slot--)
        {
            var shown = At(slot).Shown;
            var firstGap = shown.ToList().FindIndex(seen => !seen);

            if (firstGap < 0)
            {
                continue;
            }

            Assert.All(shown.Skip(firstGap), seen =>
                Assert.False(seen, $"A count reappeared after a dropped one, in a {slot}-wide slot."));
        }
    }

    /// <summary>
    /// The counts go one at a time, from the right, and never come back on the way down.
    /// </summary>
    [Fact]
    public void It_gives_up_one_count_at_a_time()
    {
        var tierZero = At(Unbounded).Desired;
        var steps = new List<int>();

        for (var slot = Math.Ceiling(tierZero) + 4; slot >= 0; slot--)
        {
            var counts = At(slot).Counts;

            if (steps.Count == 0 || counts != steps[^1])
            {
                steps.Add(counts);
            }
        }

        Assert.Equal([4, 3, 2, 1, 0], steps);
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
        var withZero = At(Unbounded, unread: 0);

        Assert.Equal(0, withZero.Tier);
        Assert.False(withZero.Shown[2]);
        Assert.True(withZero.Shown[3]);

        // And it really is cheaper without it, which is what "costs nothing" has to mean.
        Assert.True(withZero.Desired < At(Unbounded).Desired);
    }

    // ---- The numbers the documentation quotes --------------------------------------------------

    /// <summary>
    /// The widths the caption's remarks and the execution plan quote — verified where the scale
    /// they were recorded at still holds, and checked for shape everywhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The only test here that knows a number, and the only one that is allowed to stop
    /// knowing it.</strong> The recorded ladder is a fact about 100% display scaling: 244 for the
    /// long form, 175 for the short one, 60 for numbers only, 44 for three counts, 12 for one. On
    /// a machine at another scale those are simply different, because Display mode quantizes to
    /// device pixels, so asserting them there would be asserting the monitor.
    /// </para>
    /// <para>
    /// It does not go quiet in that case. The ladder's shape — five rungs, strictly descending,
    /// none degenerate — is true at every scale and is asserted either way, so this test always
    /// states something and never states something false.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_recorded_ladder()
    {
        var (tierZero, tierOne, tierTwo, threeCounts, oneCount) = Ladder();

        output.WriteLine($"Measured here: {tierZero} / {tierOne} / {tierTwo} / {threeCounts} / {oneCount}.");

        // True at any scale: five distinct rungs, each narrower than the one above it.
        Assert.True(tierZero > tierOne, $"tier 0 ({tierZero}) should exceed tier 1 ({tierOne}).");
        Assert.True(tierOne > tierTwo, $"tier 1 ({tierOne}) should exceed tier 2 ({tierTwo}).");
        Assert.True(tierTwo > threeCounts, $"tier 2 ({tierTwo}) should exceed three counts ({threeCounts}).");
        Assert.True(threeCounts > oneCount, $"three counts ({threeCounts}) should exceed one count ({oneCount}).");
        Assert.True(oneCount > 0, $"one count ({oneCount}) should have a width.");

        // ALSO TRUE AT ANY SCALE, AND THIS IS THE ONE THAT CATCHES A TYPO. Changing the display
        // scale moves these widths by a few per cent — the largest gap seen between 100% and
        // 150% is the smallest rung, at eleven — because quantizing to a device pixel can only
        // move a glyph run by less than one of them. It cannot move a number by half. So a
        // recorded figure that is nowhere near what this machine measures is a transcription
        // error rather than a different monitor, and it fails here whatever the scale.
        WithinAQuarter(RecordedTierZero, tierZero, "tier 0");
        WithinAQuarter(RecordedTierOne, tierOne, "tier 1");
        WithinAQuarter(RecordedTierTwo, tierTwo, "tier 2");
        WithinAQuarter(RecordedThreeCounts, threeCounts, "three counts");
        WithinAQuarter(RecordedOneCount, oneCount, "one count");

        if (oneCount != RecordedOneCount)
        {
            // Another display scale. The recorded numbers are not wrong, they are elsewhere —
            // SAID OUT LOUD, because a bound that lives only in a comment does not reach anyone
            // reading a green result. This suite has no dynamic skip to report it as one.
            output.WriteLine(
                $"NOT CHECKED EXACTLY: the recorded ladder is 100% figures and this machine "
                    + $"measures the smallest rung at {oneCount} rather than {RecordedOneCount}. "
                    + $"Measured here: {tierZero} / {tierOne} / {tierTwo} / {threeCounts} / {oneCount}. "
                    + "Shape and range were checked; the five exact widths were not.");

            return;
        }

        output.WriteLine("Checked exactly: this machine is at the scale the ladder was recorded at.");

        Assert.Equal(RecordedTierZero, tierZero);
        Assert.Equal(RecordedTierOne, tierOne);
        Assert.Equal(RecordedTierTwo, tierTwo);
        Assert.Equal(RecordedThreeCounts, threeCounts);
    }

    /// <summary>
    /// Fails when <paramref name="recorded"/> is nowhere near <paramref name="measured"/>.
    /// </summary>
    /// <remarks>
    /// A quarter is far wider than any display scale moves these — it is not a tolerance on the
    /// measurement, it is a guard on the constant. Its job is that a recorded figure edited to
    /// something impossible fails on every machine, not only on one at 100%.
    /// </remarks>
    private static void WithinAQuarter(double recorded, double measured, string rung)
    {
        Assert.True(
            Math.Abs(recorded - measured) <= measured * 0.25,
            $"The recorded {rung} width is {recorded}, and this machine measures {measured}. "
                + "Display scaling moves these by a few per cent; that is not scaling.");
    }

    // ---- Building the thing MainWindow.xaml declares --------------------------------------------

    /// <summary>
    /// The caption's four segments.
    /// </summary>
    /// <param name="unread">The unread count, so a zero can be exercised.</param>
    /// <param name="working">The working count.</param>
    /// <param name="rungs">
    /// Which rungs to build. <see cref="Rungs.Full"/> is the markup. The other two take rungs
    /// away — the ladder before T1.39, and no ladder at all — which is what each rung is worth
    /// measuring against.
    /// </param>
    private static FittingStrip Build(int unread, int working, Rungs rungs)
    {
        var strip = new FittingStrip();
        var longFormOnly = rungs == Rungs.LongFormOnly;

        // The tier the three band words hide at: tier 2, numbers only, in the markup.
        int? wordsHideAt = rungs == Rungs.Full ? 2 : null;

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

        strip.Children.Add(Count(
            NeedsYou,
            longFormOnly ? " need you" : null,
            longFormOnly ? null : " need you| need",
            wordsHideAt));
        strip.Children.Add(Count(unread, " unread", hideAt: wordsHideAt));
        strip.Children.Add(Count(working, " working", hideAt: wordsHideAt));

        return strip;
    }

    /// <summary>One count: its separator, its number in semibold, and its word.</summary>
    private static StackPanel Count(int value, string? label = null, string? tiers = null, int? hideAt = null)
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

        if (hideAt is { } tier)
        {
            FittingStrip.SetHideAtTier(word, tier);
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
    /// wider in this face, and that width is two pixels of tier boundary at 100%.
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
