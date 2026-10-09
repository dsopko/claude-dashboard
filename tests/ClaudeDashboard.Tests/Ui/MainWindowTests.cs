using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Configuration;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Architecture;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The window itself: that the templates render what the view model says, and that the two
/// animations are the only ones there are (Design Document §9).
/// </summary>
/// <remarks>
/// <para>
/// These run against a real <see cref="Window"/> on the harness's UI thread, shown off the side
/// of every monitor so that it has a presentation source and therefore a visual tree. A window in
/// a dispatcher harness is awkward, not impossible — and a screenshot is not a test.
/// </para>
/// <para>
/// Assertions are on the visual tree the templates produced, not on names: a name lookup would
/// only find an element the test already assumed was there.
/// </para>
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class MainWindowTests(StaHarness harness, Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly StaHarness _harness = harness;
    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    /// <summary>
    /// Builds a window over <paramref name="arrange"/>'s sessions and lays it out, then hands
    /// both to <paramref name="assert"/> on the UI thread.
    /// </summary>
    private T WithWindow<T>(
        Action<RegistryHarness> arrange,
        Func<MainWindow, MainViewModel, T> assert,
        bool motionAllowed = true,
        bool showQuiet = false,
        bool grouped = true,
        Action<MainViewModel>? prepare = null,
        RosterStore? rosters = null,
        UsageBoard? usage = null)
    {
        return _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => motionAllowed, observeChanges: false);
            using var viewModel = new MainViewModel(registry.Projection, policy, new StubAckPublisher(), new FakeClipboard(), rosters ?? new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), usage ?? new UsageBoard());

            // Set before the window is realized. Toggling it on a live window raises transient
            // binding errors from the group headers being torn down, which BindingErrorWatch
            // rightly reports and which have nothing to do with what any test here asserts.
            viewModel.IsGrouped = grouped;

            arrange(registry);

            if (showQuiet)
            {
                // A quiet session has no row of its own until its group is opened (Design
                // Document §6 rule 2), so a test about how a quiet row looks has to open it.
                foreach (var group in viewModel.Rows.OfType<GroupViewModel>().ToList())
                {
                    group.IsExpanded = true;
                }
            }

            // Anything that changes what a row IS — a prompt appearing above a group, a group's key
            // changing — happens here, BEFORE the window exists. Doing it to a realized window
            // replaces an item whose template differs from its neighbour's, and WPF evaluates the
            // old template's bindings once against the new item on the way past: transient noise of
            // the same class as issue #23, which BindingErrorWatch rightly reports and which says
            // nothing about the markup under test.
            prepare?.Invoke(viewModel);

            var window = new MainWindow(viewModel, TestTrays.For(registry.Projection));
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                var result = assert(window, viewModel);

                // Checked here rather than in each test: a misspelled path fails silently in WPF
                // — the element simply shows nothing — so an assertion about the visual tree can
                // pass while the row is blank. This is the only thing that would say so.
                Assert.Empty(bindings.Problems);
                return result;
            }
            finally
            {
                window.Hide();
            }
        });
    }

    /// <summary>
    /// Shows <paramref name="window"/> off the side of every monitor and lets its layout settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Measuring is not enough.</strong> A <see cref="Window"/> that has never been shown
    /// has no presentation source, so <c>Measure</c> and <c>Arrange</c> build no visual tree at
    /// all: an assertion about what the templates produced would find nothing and — worse — a
    /// count-based one would quietly agree with itself. So the window is really shown, at a
    /// position no monitor covers, unactivated and out of the taskbar, and hidden again
    /// afterwards.
    /// </para>
    /// <para>
    /// The dispatcher is then pumped down to <see cref="DispatcherPriority.Loaded"/>, which is
    /// where the item containers are generated and where a trigger's storyboard actually starts.
    /// </para>
    /// </remarks>
    private void Realize(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();

        _harness.Pump(DispatcherPriority.Background);
    }

    private static IReadOnlyList<ContentPresenter> RowsOf(MainWindow window) =>
        [.. StaHarness.FindAll<ItemsControl>(window)
            .Where(items => items.Name == "RowsHost")
            .SelectMany(items => StaHarness.FindAll<ContentPresenter>(items)
                .Where(presenter => presenter.DataContext is DashboardRow
                    && ReferenceEquals(presenter.TemplatedParent, null)))];

    private static ContentPresenter RowFor(MainWindow window, string sessionId) =>
        RowsOf(window).Single(row =>
            row.DataContext is SessionViewModel session && session.Id.Value == sessionId);

    /// <summary>
    /// <strong>The Grouped/Flat toggle on a live window raises no binding error</strong> (for issue #23). A row of
    /// another kind now takes its place by a remove and an insert, so no container is bound once to a row of the
    /// wrong kind; before, the same toggle wrote 212 binding errors in the T1.71 measurement.
    /// </summary>
    [Fact]
    public void Toggling_grouped_and_flat_on_a_live_window_raises_no_binding_error()
    {
        var problems = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => true, observeChanges: false);
            using var viewModel = new MainViewModel(registry.Projection, policy, new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());

            for (var i = 0; i < 5; i++)
            {
                registry.Working($"s-{i}", At.AddSeconds(i), $@"C:\dev\p{i}", title: $"Task {i}");
            }

            var window = new MainWindow(viewModel, TestTrays.For(registry.Projection));

            try
            {
                Realize(window);

                using var bindings = new BindingErrorWatch();

                viewModel.IsGrouped = false;
                window.UpdateLayout();
                _harness.Pump(DispatcherPriority.Background);
                Assert.Contains(viewModel.Rows, row => row is BandHeaderViewModel);

                viewModel.IsGrouped = true;
                window.UpdateLayout();
                _harness.Pump(DispatcherPriority.Background);
                Assert.Contains(viewModel.Rows, row => row is GroupViewModel);

                return bindings.Problems.ToList();
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.Empty(problems);
    }

    // ---- The caption's own icon (T1.38) --------------------------------------------------------

    /// <summary>
    /// <strong>The caption draws the frame made for its display scale, at that frame's own size,
    /// pixel for pixel.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The display scale is the machine's, not the test's: <see cref="VisualTreeHelper.SetRootDpi"/>
    /// cannot pin it once a window has been realized in the process (FittingStripTests records
    /// the measurement). So this asserts what must hold at the scale it finds, and says in the
    /// output which scale that was. At 100% that is the acceptance as written: the 20 px frame,
    /// 20 device pixels, unscaled. At 150% it is the same claim for the 30 px frame.
    /// </para>
    /// <para>
    /// <strong>The pixel check is the point.</strong> The size and the alignment say the frame
    /// COULD be drawn unscaled; rendering the window and reading the pixels back says it WAS.
    /// Only the frame's fully opaque pixels are compared, because every other pixel is blended
    /// with the caption behind it, and any resampling changes opaque pixels too: each one next
    /// to a different neighbour would take some of that neighbour's colour.
    /// </para>
    /// <para>
    /// <strong>At any other scale the pixel proof does not run, and a green result there proves
    /// nothing about pixels.</strong> The proof runs at 100% and 150% only, the two scales a frame
    /// was made for. At 125%, 175%, 200% or any other, the test checks the frame choice and the
    /// 20 DIP slot, then passes; xUnit 2.9 has no run-time skip, so it cannot report itself as
    /// skipped. The output line begins "PIXEL PROOF DID NOT RUN" in that case. Read it before
    /// counting this test as evidence.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_caption_draws_its_own_icon_frame_pixel_for_pixel()
    {
        var scale = WithWindow(
            _ => { },
            (window, _) =>
            {
                var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                var image = window.CaptionIconImage;
                var frame = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapImage>(image.Source);

                Assert.Equal(CaptionIcon.Pack(CaptionIcon.FrameFor(scale)), frame.UriSource);

                // 20 DIP, whatever the scale: the slot the design gives the icon.
                Assert.Equal(20.0, image.ActualWidth, 6);
                Assert.Equal(20.0, image.ActualHeight, 6);

                var origin = image.TransformToAncestor(window).Transform(default);
                var deviceX = origin.X * scale;
                var deviceY = origin.Y * scale;

                if (scale is not (1.0 or 1.5))
                {
                    // No frame was made for this scale, so there is no native size to assert
                    // and the pixel proof does NOT run. The test still passes — xUnit 2.9 has no
                    // skip that can be decided at run time — so the output line below is the
                    // record, and it says so in so many words.
                    return scale;
                }

                // Native: the drawn size in device pixels IS the frame's pixel size, and the
                // frame starts on a whole device pixel.
                Assert.Equal(frame.PixelWidth, (int)Math.Round(20 * scale));
                Assert.Equal(frame.PixelHeight, (int)Math.Round(20 * scale));
                Assert.Equal(Math.Round(deviceX), deviceX, 6);
                Assert.Equal(Math.Round(deviceY), deviceY, 6);

                var size = frame.PixelWidth;
                var expected = Pixels(frame, 0, 0, size);

                var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(window.ActualWidth * scale),
                    (int)Math.Ceiling(window.ActualHeight * scale),
                    96 * scale,
                    96 * scale,
                    PixelFormats.Pbgra32);
                rendered.Render(window);

                var drawn = Pixels(rendered, (int)Math.Round(deviceX), (int)Math.Round(deviceY), size);

                var opaque = 0;

                for (var i = 0; i < expected.Length; i += 4)
                {
                    if (expected[i + 3] != 255)
                    {
                        continue;
                    }

                    opaque++;

                    Assert.True(
                        expected.AsSpan(i, 4).SequenceEqual(drawn.AsSpan(i, 4)),
                        $"Pixel ({(i / 4) % size}, {(i / 4) / size}) of the {size} px frame is drawn as " +
                        $"BGRA {string.Join(",", drawn[i..(i + 4)])}, not {string.Join(",", expected[i..(i + 4)])}: " +
                        "the frame was resampled.");
                }

                // Most of the tile is opaque; a frame with none would pass vacuously.
                Assert.True(opaque > size * size / 2, $"Only {opaque} opaque pixel(s) were compared.");

                return scale;
            });

        _output.WriteLine(scale is 1.0 or 1.5
            ? $"Display scale {scale:P0}: the {(scale == 1.0 ? 20 : 30)} px frame is drawn at its own size, pixel for pixel."
            : $"PIXEL PROOF DID NOT RUN. Display scale {scale:P0} has no frame made for it, so this pass " +
              "checked only the frame choice and the 20 DIP slot and proves nothing about pixels. " +
              "The pixel proof runs at 100% (the 20 px frame) and 150% (the 30 px frame); run it on a machine at one of those.");
    }

    /// <summary>A square of pixels as premultiplied BGRA, the format the renderer writes.</summary>
    private static byte[] Pixels(System.Windows.Media.Imaging.BitmapSource source, int x, int y, int size)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[size * size * 4];

        converted.CopyPixels(new Int32Rect(x, y, size, size), pixels, size * 4, 0);

        return pixels;
    }

    // ---- It renders what the view model holds -------------------------------------------------

    [Fact]
    public void The_window_renders_a_row_for_every_row_the_view_model_has()
    {
        var counted = WithWindow(
            registry =>
            {
                registry.Working("busy", At);
                registry.Working("blocked", At);
                registry.Blocked("blocked", At.AddMinutes(1));
            },
            (window, viewModel) => (Rendered: RowsOf(window).Count, Expected: viewModel.Rows.Count));

        Assert.Equal(counted.Expected, counted.Rendered);
        Assert.Equal(3, counted.Expected);
    }

    /// <summary>
    /// <strong>The prompt on the COLLAPSED row is drawn in the mono face</strong> (Design §9 —
    /// it *is* terminal text).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This test spent one commit asserting the wrong element, and passed the whole
    /// time.</strong> It located its subject by <c>candidate.Text == …</c>. When T1.24 made the
    /// context line two <c>Run</c>s, that block's <c>Text</c> went empty (see <see cref="TextOf"/>),
    /// the predicate stopped matching it, and it matched the <em>expanded</em> row's prompt block
    /// instead — which is invisible while the row is collapsed. The subject moved from a visible
    /// element to a hidden one and nothing said so: the row's context line could lose its
    /// monospace face entirely and this test still passed.
    /// </para>
    /// <para>
    /// So the selection is now three things rather than one. <see cref="TextOf"/> reads inline
    /// content, <c>IsVisible</c> excludes the expanded row's copy, and <c>Single</c> makes an
    /// ambiguous match a loud failure instead of a silent choice between two candidates.
    /// </para>
    /// <para>
    /// The face is asserted on the <c>Run</c> that actually carries the prompt, not on the block
    /// around it. <c>FontFamily</c> is inherited, so this reads the face the prompt is really
    /// drawn in — and it stays true if the title's own <c>Run</c> is ever restyled, which asserting
    /// the block's family would not.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_row_shows_its_prompt_in_the_mono_face()
    {
        const string Prompt = "draft a migration plan";

        var (text, family) = WithWindow(
            registry => registry.Working("busy", At, prompt: Prompt),
            (window, _) =>
            {
                var block = StaHarness.FindAll<TextBlock>(RowFor(window, "busy"))
                    .Single(candidate => candidate.IsVisible && TextOf(candidate) == Prompt);

                var promptRun = block.Inlines.OfType<Run>()
                    .Single(run => run.Text == Prompt);

                return (TextOf(block), promptRun.FontFamily.Source);
            });

        Assert.Equal(Prompt, text);
        Assert.Contains("Cascadia", family, StringComparison.Ordinal);
    }

    /// <summary>The strip shows a band's segment only when the band has something in it.</summary>
    /// <remarks>
    /// Asserted on the segments, not on the words. This window is realized at 400, where the
    /// strip is at its numbers-only tier since T1.39 and shows no word at all — so a word would
    /// assert the width, not the band. Before T1.39 this test looked for the " need" stem for the
    /// same reason one tier up.
    /// </remarks>
    [Fact]
    public void The_counts_strip_shows_only_the_bands_that_have_something_in_them()
    {
        var shown = WithWindow(
            registry =>
            {
                registry.Working("blocked", At);
                registry.Blocked("blocked", At.AddMinutes(1));
            },
            (window, _) => StripOf(window).Children
                .Cast<UIElement>()
                .Select(segment => segment.Visibility == Visibility.Visible)
                .ToList());

        // Total, needs you, unread, working.
        Assert.Equal([true, true, false, false], shown);
    }

    /// <summary>
    /// <strong>The markup declares the ladder FittingStripTests measures</strong> (T1.39).
    /// </summary>
    /// <remarks>
    /// FittingStripTests builds its strip in code, as a copy of this markup, so it can measure
    /// without a window. This is the half that keeps the copy honest: the sessions word hides at
    /// tier 1, the needs-you word shortens and then hides at tier 2, and so do unread and
    /// working. Read from the realized tree, so a HideAtTier deleted from the markup fails here
    /// while every FittingStripTests test stays green.
    /// </remarks>
    [Fact]
    public void The_strip_markup_hides_every_band_word_at_tier_two()
    {
        var declared = WithWindow(
            registry => registry.Working("busy", At),
            (window, _) => StripOf(window).Children
                .Cast<Panel>()
                .Select(segment => segment.Children.OfType<TextBlock>().Last())
                .Select(word => (FittingStrip.GetLabels(word), FittingStrip.GetHideAtTier(word)))
                .ToList());

        Assert.Equal(
            [
                (null, 1),
                (" need you| need", 2),
                (null, 2),
                (null, 2),
            ],
            declared);
    }

    /// <summary>
    /// <strong>The strip's tooltip is the view model's counts in full, and follows them</strong>
    /// (T1.39).
    /// </summary>
    /// <remarks>
    /// Asserted against the view model's own counts, not a literal: the tooltip is bound, so it
    /// is the property the view model computes from those counts, before and after a change.
    /// </remarks>
    [Fact]
    public void The_strip_tooltip_spells_out_the_counts_and_follows_them()
    {
        var (before, beforeCounts, after, afterCounts) = WithWindow(
            registry =>
            {
                registry.Working("busy", At);
                registry.Working("blocked", At);
                registry.Blocked("blocked", At.AddMinutes(1));
            },
            (window, viewModel) =>
            {
                var strip = StripOf(window);
                var first = (string)strip.ToolTip;
                var firstCounts = (viewModel.SessionCount, viewModel.NeedsYouCount, viewModel.UnreadCount, viewModel.WorkingCount);

                // A count changes under the window: the blocked session finishes.
                viewModel.NeedsYouCount = 0;
                viewModel.UnreadCount = 1;
                _harness.Pump(DispatcherPriority.Background);

                var second = (string)strip.ToolTip;
                var secondCounts = (viewModel.SessionCount, viewModel.NeedsYouCount, viewModel.UnreadCount, viewModel.WorkingCount);

                return (first, firstCounts, second, secondCounts);
            });

        Assert.Equal(Spelled(beforeCounts), before);
        Assert.Equal(Spelled(afterCounts), after);
        Assert.NotEqual(before, after);

        // The counts in full words, zeros left out, the total always — computed here from the
        // counts rather than read back from the view model, so the two are checked against each
        // other.
        static string Spelled((int Sessions, int NeedsYou, int Unread, int Working) counts) =>
            string.Join(" · ", new[]
            {
                $"{counts.Sessions} {(counts.Sessions == 1 ? "session" : "sessions")}",
                counts.NeedsYou > 0 ? $"{counts.NeedsYou} need you" : null,
                counts.Unread > 0 ? $"{counts.Unread} unread" : null,
                counts.Working > 0 ? $"{counts.Working} working" : null,
            }.OfType<string>());
    }

    /// <summary>
    /// <strong>The counts tooltip is on only while the strip is shortened</strong> — the
    /// operator's ruling on issue #53.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through the real markup: the tooltip's text is bound to <c>CountsText</c> and its
    /// <c>ToolTipService.IsEnabled</c> to the strip's own <see cref="FittingStrip.IsShortened"/>,
    /// so this checks the binding and the property together. Widths come from this run: the
    /// window is widened by exactly what the strip's long form lacks, which puts the slot at the
    /// tier 0 boundary on whatever machine runs it.
    /// </para>
    /// <para>
    /// Then a count moves the strip across that boundary without the window changing: a band
    /// appears and the long form no longer fits, so the tooltip comes on; it goes, and the
    /// tooltip goes off again.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_counts_tooltip_is_on_only_while_the_strip_is_shortened()
    {
        var steps = WithWindow(
            registry => registry.Working("busy", At),
            (window, viewModel) =>
            {
                var strip = StripOf(window);
                var seen = new List<(string Step, bool Enabled, int Tier, string? Tip, string Full)>();

                void Record(string step)
                {
                    _harness.Pump(DispatcherPriority.Background);
                    window.UpdateLayout();
                    seen.Add((step, ToolTipService.GetIsEnabled(strip), strip.Tier, strip.ToolTip as string, viewModel.CountsText));
                }

                Record("narrow");

                // What the long form needs, against what the slot gives: the strip's own answer
                // to an unbounded measure, and its layout slot less its left margin.
                var available = LayoutInformation.GetLayoutSlot(strip).Width - strip.Margin.Left;
                strip.Measure(new Size(double.PositiveInfinity, strip.ActualHeight));
                var longForm = strip.DesiredSize.Width;

                // Plus one DIP. At 150% layout rounding snaps the slot to device pixels, which
                // can leave it up to a third of a DIP short of the exact shortfall, and the long
                // form would miss by that third. One DIP covers any rounding at any scale and is
                // far narrower than " · 1 need you", so the band below still crosses the boundary.
                window.Width = window.ActualWidth + Math.Ceiling(longForm - available) + 1;
                Record("widened to the tier 0 boundary");

                viewModel.NeedsYouCount = 1;
                Record("a band appears");

                viewModel.NeedsYouCount = 0;
                Record("the band goes");

                return seen;
            });

        var narrow = steps[0];
        Assert.True(narrow.Enabled, $"At 400 the strip is at tier {narrow.Tier} and the tooltip should be on.");
        Assert.True(narrow.Tier > 0);
        Assert.Equal(narrow.Full, narrow.Tip);

        var wide = steps[1];
        Assert.Equal(0, wide.Tier);
        Assert.False(wide.Enabled, "The long form is on screen whole; the tooltip should be off.");

        var crossed = steps[2];
        Assert.True(crossed.Tier > 0, "A new band should push the strip past tier 0 at the boundary width.");
        Assert.True(crossed.Enabled, "The strip is shortened again; the tooltip should be on.");
        Assert.Equal(crossed.Full, crossed.Tip);
        Assert.Contains("need you", crossed.Tip, StringComparison.Ordinal);

        var back = steps[3];
        Assert.Equal(0, back.Tier);
        Assert.False(back.Enabled, "The band went and the long form fits again; the tooltip should be off.");
    }

    /// <summary>
    /// <strong>A Waiting row, on a real window</strong> (T1.41, issue #52): the collapsed line, "Claude
    /// said so far", the "Waiting on" block with two tasks — and "Claude answered" again once the
    /// work really finishes.
    /// </summary>
    /// <remarks>
    /// Through the real markup, so a misspelled binding path fails here: <c>WithWindow</c> fails
    /// on any binding error it collects. The events after the first look are applied to the same
    /// registry under the realized window, the way the pipeline would.
    /// </remarks>
    [Fact]
    public void A_waiting_row_says_what_it_waits_on_and_answers_once_finished()
    {
        RegistryHarness? registry = null;
        string? promptId = null;

        var seen = WithWindow(
            harness =>
            {
                registry = harness;
                promptId = harness.Working("waiter", At, prompt: "cut the release");
                harness.Apply(new Core.Events.Stop
                {
                    SessionId = new SessionId("waiter"),
                    Timestamp = At.AddMinutes(1),
                    Cwd = RegistryHarness.Workspace,
                    PromptId = promptId,
                    LastAssistantMessage = "Started the build and a review.",
                    BackgroundTasks =
                    [
                        new BackgroundTask("b1", BackgroundTaskKind.Shell, "Run the test suite"),
                        new BackgroundTask("a1", BackgroundTaskKind.Subagent, "Review the diff"),
                    ],
                });
            },
            (window, viewModel) =>
            {
                var row = viewModel.Rows.OfType<SessionViewModel>().Single();
                row.IsExpanded = true;
                window.UpdateLayout();

                var waiting = VisibleTexts(window, "waiter");

                // The work reports back, and the woken turn ends with nothing running.
                registry!.Apply(new Core.Events.UserPromptSubmit
                {
                    SessionId = new SessionId("waiter"),
                    Timestamp = At.AddMinutes(5),
                    Cwd = RegistryHarness.Workspace,
                    PromptId = "p-wake",
                    Prompt = "<task-notification>\n<task-id>b1</task-id>",
                });
                registry.Apply(new Core.Events.Stop
                {
                    SessionId = new SessionId("waiter"),
                    Timestamp = At.AddMinutes(6),
                    Cwd = RegistryHarness.Workspace,
                    PromptId = "p-wake",
                    LastAssistantMessage = "All green.",
                });
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();

                var row2 = viewModel.Rows.OfType<SessionViewModel>().Single();
                row2.IsExpanded = true;
                window.UpdateLayout();

                return (Waiting: waiting, Finished: VisibleTexts(window, "waiter"));
            });

        // Collapsed: the badge, the work's elapsed time, and the first task.
        Assert.Contains("WAITING", seen.Waiting);
        Assert.Contains(" · Run the test suite", seen.Waiting);

        // Expanded: what Claude has said so far, and what it is waiting on, one line each.
        Assert.Contains("CLAUDE SAID SO FAR", seen.Waiting);
        Assert.DoesNotContain("CLAUDE ANSWERED", seen.Waiting);
        Assert.Contains("Started the build and a review.", seen.Waiting);
        Assert.Contains("WAITING ON", seen.Waiting);
        Assert.Contains(seen.Waiting, text => text.StartsWith("Run the test suite · background command · ", StringComparison.Ordinal));
        Assert.Contains(seen.Waiting, text => text.StartsWith("Review the diff · subagent · ", StringComparison.Ordinal));

        // Finished: the answer is an answer again, and the block is gone.
        Assert.Contains("CLAUDE ANSWERED", seen.Finished);
        Assert.Contains("All green.", seen.Finished);
        Assert.DoesNotContain("WAITING ON", seen.Finished);
        Assert.DoesNotContain("CLAUDE SAID SO FAR", seen.Finished);
        Assert.DoesNotContain(seen.Finished, text => text.Contains("Run the test suite", StringComparison.Ordinal));
    }

    /// <summary>
    /// <strong>The operator's counts in the window's own caption</strong> (T1.42, issue #55): total
    /// 3, unread 2, working 1. At every width, a count is left out only when it cannot fit, only at
    /// the last tier, and never while its room stands empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Swept one DIP at a time from where nothing fits to where the long form does, in the real
    /// markup. Every width is what the caption gives the strip in this run — its layout slot less
    /// its margin — and every count's width is what the strip measured it at, so nothing here is
    /// written down.
    /// </para>
    /// <para>
    /// <strong>On a 100% display this passes with or without the fix, and says so.</strong> The
    /// fault needs a fractional scale: there, widths are fractions of a DIP whose float sum can
    /// exceed the pixel-rounded arrange size by a last bit. At 100% every width is whole and every
    /// sum exact. FittingStripTests reproduces the arithmetic at 125%, 150% and 175% and fails
    /// before the fix; this is the same rule, held in the real caption at whatever scale runs it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_operators_counts_are_never_dropped_while_their_room_stands_empty()
    {
        var (scale, steps) = WithWindow(
            registry =>
            {
                var first = registry.Working("done-1", At);
                registry.Finished("done-1", At.AddMinutes(1), first);
                var second = registry.Working("done-2", At);
                registry.Finished("done-2", At.AddMinutes(1), second);
                registry.Working("busy", At);
            },
            (window, viewModel) =>
            {
                Assert.Equal((3, 0, 2, 1), (viewModel.SessionCount, viewModel.NeedsYouCount, viewModel.UnreadCount, viewModel.WorkingCount));

                // The caption's own strip, whichever line is in use: this is about the caption's room.
                var strip = window.CaptionCounts.Strip;
                var seen = new List<(double Room, int Tier, List<(bool Visible, bool Drawn, double Width)> Segments)>();

                for (var width = window.MinWidth; width <= window.MinWidth + 400; width++)
                {
                    window.Width = width;
                    window.UpdateLayout();

                    seen.Add((
                        LayoutInformation.GetLayoutSlot(strip).Width - strip.Margin.Left,
                        strip.Tier,
                        [.. strip.Children.Cast<FrameworkElement>().Select(segment => (
                            segment.Visibility == Visibility.Visible,
                            LayoutInformation.GetLayoutSlot(segment).Width > 0,
                            segment.DesiredSize.Width))]));
                }

                return (VisualTreeHelper.GetDpi(window).DpiScaleX, seen);
            });

        var allThree = false;

        foreach (var (room, tier, segments) in steps)
        {
            var shown = segments.Where(s => s.Visible).ToList();
            var drawnWidth = shown.Where(s => s.Drawn).Sum(s => s.Width);

            // What is drawn is a prefix of what is visible: no count after a dropped one.
            var firstDropped = shown.FindIndex(s => !s.Drawn);

            if (firstDropped < 0)
            {
                allThree = true;
                continue;
            }

            Assert.All(shown.Skip(firstDropped), s => Assert.False(s.Drawn, $"A count was drawn after a dropped one, in {room}."));

            // Dropped only at the last tier, and only because it really does not fit.
            Assert.True(tier == 2, $"A count was dropped at tier {tier}, in {room}: every word goes first.");
            Assert.True(
                drawnWidth + shown[firstDropped].Width > room,
                $"In {room} the strip drew {drawnWidth} and left out a count {shown[firstDropped].Width} wide: it had room for it.");
        }

        // And the sweep reached a width where all three counts are drawn.
        Assert.True(allThree, "No width in the sweep showed all three counts.");

        _output.WriteLine($"Display scale {scale:P0}: the rule held at every width swept."
            + (scale == 1.0 ? " At 100% the fault cannot occur here; FittingStripTests reproduces it at fractional scales." : string.Empty));
    }

    // ---- The counts row (T1.43, issue #54) --------------------------------------------------------

    /// <summary>What the counts looked like at one window width.</summary>
    private sealed record CountsAt(
        double Width,
        bool InCaption,
        bool RowUp,
        bool CaptionDropped,
        double CaptionRoom,
        double RowRoom,
        int RowTier,
        bool RowShortened,
        bool RowTooltip,
        string? RowTip,
        double ArrangedWidth,
        double DesiredWidth,
        double DrawnWidth);

    /// <summary>
    /// Counts long enough that the row itself must shorten near the window's minimum width, so
    /// the row's own ladder and tooltip are exercised. Set on the view model: only their width
    /// matters here.
    /// </summary>
    private static void LongCounts(MainViewModel viewModel)
    {
        viewModel.SessionCount = 11111;
        viewModel.NeedsYouCount = 2222;
        viewModel.UnreadCount = 3333;
        viewModel.WorkingCount = 4444;
    }

    /// <summary>
    /// Sweeps the window one DIP at a time across <paramref name="widths"/>, laying out twice at
    /// each, and records where the counts are.
    /// </summary>
    private List<CountsAt> Sweep(MainWindow window, MainViewModel viewModel, IEnumerable<double> widths)
    {
        var seen = new List<CountsAt>();

        foreach (var width in widths)
        {
            window.Width = width;
            window.UpdateLayout();
            _harness.Pump(DispatcherPriority.Background);
            window.UpdateLayout();

            var inCaption = window.CaptionCounts.Visibility == Visibility.Visible;
            var rowUp = window.CountsRow.Visibility == Visibility.Visible;
            var inUse = rowUp ? window.RowCounts.Strip : window.CaptionCounts.Strip;

            var drawn = inUse.Children.Cast<FrameworkElement>()
                .Where(child => LayoutInformation.GetLayoutSlot(child).Width > 0)
                .Sum(child => child.DesiredSize.Width);

            seen.Add(new CountsAt(
                width,
                inCaption,
                rowUp,
                window.CaptionCounts.Strip.HasDropped,
                LayoutInformation.GetLayoutSlot(window.CaptionCounts).Width - window.CaptionCounts.Margin.Left,
                LayoutInformation.GetLayoutSlot(window.RowCounts).Width,
                window.RowCounts.Strip.Tier,
                window.RowCounts.Strip.IsShortened,
                ToolTipService.GetIsEnabled(window.RowCounts.Strip),
                window.RowCounts.Strip.ToolTip as string,
                inUse.RenderSize.Width,
                inUse.DesiredSize.Width,
                drawn));

            // Laid out again at the same width: the choice must not move on its own.
            window.UpdateLayout();
            _harness.Pump(DispatcherPriority.Background);

            Assert.True(
                (window.CaptionCounts.Visibility == Visibility.Visible) == inCaption
                    && (window.CountsRow.Visibility == Visibility.Visible) == rowUp,
                $"At {width} the counts moved without the width changing.");
        }

        return seen;
    }

    /// <summary>
    /// <strong>The counts are in exactly one place at every width, the choice is a pure function
    /// of the width, and it cannot oscillate</strong> (T1.43, issue #54).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Swept one DIP at a time from the window's minimum width up past the point where the caption
    /// takes the counts back, then down again. At every width: the counts show in the caption or
    /// on the row, never both and never neither; a second layout at the same width changes
    /// nothing; and the width decides the same way whichever direction it was reached from — any
    /// hysteresis, which is what an oscillation needs, would show as a disagreement.
    /// </para>
    /// <para>
    /// The widths are the window's, stepped from its own MinWidth; the threshold is wherever this
    /// run's measurement puts it. Nothing is written down.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_counts_are_in_exactly_one_place_at_every_width()
    {
        var (up, down) = WithWindow(
            registry => registry.Working("busy", At),
            (window, viewModel) =>
            {
                LongCounts(viewModel);

                var widths = Enumerable.Range(0, 401).Select(step => window.MinWidth + step).ToList();

                return (Sweep(window, viewModel, widths), Sweep(window, viewModel, Enumerable.Reverse(widths)));
            });

        foreach (var at in up.Concat(down))
        {
            Assert.True(at.InCaption ^ at.RowUp, $"At {at.Width} the counts were in the caption: {at.InCaption}, on the row: {at.RowUp}.");

            // The rule: on the row exactly when the caption could not fit even numbers-only.
            Assert.Equal(at.CaptionDropped, at.RowUp);
        }

        var byWidth = down.ToDictionary(at => at.Width);

        Assert.All(up, at => Assert.Equal(byWidth[at.Width].RowUp, at.RowUp));

        // One threshold: the row below it, the caption above it, and both are reached.
        var changes = up.Zip(up.Skip(1)).Count(pair => pair.First.RowUp != pair.Second.RowUp);

        Assert.Equal(1, changes);
        Assert.True(up[0].RowUp, "At the window's minimum width the counts should be on the row.");
        Assert.False(up[^1].RowUp, "At the widest width swept the counts should be back in the caption.");
    }

    /// <summary>
    /// <strong>On the row the ladder starts again from full words, and follows the same rules
    /// down to the minimum width</strong>; the tooltip follows #53's rule there; and on either line
    /// the strip is arranged at the width it measured (the T1.42 review).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row's longest tier is measured in this run: the row's own strip, measured unbounded,
    /// asks for the full-words width. Wherever the row has that much room it shows tier 0 with no
    /// tooltip; wherever it has less it is shortened and the tooltip carries the full text. A
    /// count is left out only at the last tier, and what is drawn fits.
    /// </para>
    /// <para>
    /// <strong>Arranged at the measured width.</strong> FittingStrip draws the counts its measure
    /// kept without checking the width again (T1.42), so its parent must arrange it at the width it
    /// measured — its desired width, give or take a pixel of rounding. Asserted on whichever line
    /// is in use at every width.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_row_follows_the_ladder_and_the_tooltip_rule_down_to_the_minimum_width()
    {
        var (seen, fullWords, fullText, minWidth) = WithWindow(
            registry => registry.Working("busy", At),
            (window, viewModel) =>
            {
                LongCounts(viewModel);

                var seen = Sweep(window, viewModel, Enumerable.Range(0, 401).Select(step => window.MinWidth + step));

                // What the row's strip asks for with nothing held back: the full-words width.
                var strip = window.RowCounts.Strip;
                strip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var fullWords = strip.DesiredSize.Width;
                strip.InvalidateMeasure();
                window.UpdateLayout();

                return (seen, fullWords, viewModel.CountsText, window.MinWidth);
            });

        var onRow = seen.Where(at => at.RowUp).ToList();

        Assert.NotEmpty(onRow);
        Assert.Contains(onRow, at => at.Width == minWidth);

        foreach (var at in onRow)
        {
            if (at.RowRoom >= fullWords)
            {
                Assert.True(at.RowTier == 0 && !at.RowShortened, $"At {at.Width} the row had room for full words ({at.RowRoom} of {fullWords}) and showed tier {at.RowTier}.");
                Assert.False(at.RowTooltip, $"At {at.Width} the row showed the full text and still offered the tooltip.");
            }
            else
            {
                Assert.True(at.RowShortened, $"At {at.Width} the row had {at.RowRoom} of the {fullWords} full words need, and was not shortened.");
                Assert.True(at.RowTooltip, $"At {at.Width} the row was shortened and offered no tooltip.");
                Assert.Equal(fullText, at.RowTip);
            }
        }

        // The minimum width shortens the row: the row's ladder is exercised, not just its long form.
        Assert.True(onRow.Single(at => at.Width == minWidth).RowShortened, "Pick longer counts: the row fit full words even at the minimum width.");

        foreach (var at in seen)
        {
            Assert.True(at.DrawnWidth <= at.ArrangedWidth + 0.01, $"At {at.Width} the strip drew {at.DrawnWidth} in an arrangement {at.ArrangedWidth} wide.");
            Assert.True(Math.Abs(at.ArrangedWidth - at.DesiredWidth) <= 1, $"At {at.Width} the strip measured {at.DesiredWidth} and was arranged at {at.ArrangedWidth}.");
        }
    }

    /// <summary>
    /// The counts strip on the line in use: the counts row when it is up, the caption otherwise
    /// (T1.43). Both are the same CountsStrip markup.
    /// </summary>
    private static FittingStrip StripOf(MainWindow window) =>
        window.CountsRow.Visibility == Visibility.Visible ? window.RowCounts.Strip : window.CaptionCounts.Strip;

    // ---- The usage strip (MOD.7, issue #133) -----------------------------------------------------

    /// <summary>The display scale of the last usage sweep.</summary>
    private double _usageScale;

    /// <summary>What the caption and the row showed at one window width, with the usage beside the counts.</summary>
    private sealed record UsageAt(
        double Width,
        int CountsTier,
        bool CountsDropped,
        double CountsDesired,
        bool CountsInCaption,
        bool UsageInCaption,
        int UsageTier,
        double UsageRoom,
        int CaptionPairsDrawn,
        bool RowUp,
        bool RowCounts,
        bool RowUsage,
        int RowPairsDrawn,
        double RowUsageRight,
        double RowCountsLeft,
        bool TipsOn,
        IReadOnlyList<StripFit> UsageFits);

    /// <summary>
    /// How one shown usage strip was measured, arranged and drawn, on the line named by <c>Line</c>: the inner
    /// FittingStrip's widths, and the room its parent gave the <see cref="UsageStrip"/> (its layout slot) beside
    /// the width it asked for, both with its margin.
    /// </summary>
    private sealed record StripFit(
        string Line,
        double ArrangedWidth,
        double DesiredWidth,
        double DrawnWidth,
        double HostSlotWidth,
        double HostDesiredWidth);

    /// <summary>The counts of a busy day, "11 sessions · 3 need you · 5 unread · 8 working", set on the view model.</summary>
    private static void BusyCounts(MainViewModel viewModel)
    {
        viewModel.SessionCount = 11;
        viewModel.NeedsYouCount = 3;
        viewModel.UnreadCount = 5;
        viewModel.WorkingCount = 8;
    }

    /// <summary>
    /// A board with the guide's two limits, 5% and 36%, and with <paramref name="figures"/> 3 a made-up Fable
    /// kind at 60%, each resetting two days after <see cref="At"/>.
    /// </summary>
    private static UsageBoard UsageOf(int figures)
    {
        var board = new UsageBoard();
        var reset = At.AddDays(2);
        var windows = new List<UsageWindow>
        {
            new("five_hour", 5, reset, At, null),
            new("seven_day", 36, reset, At, null),
        };

        if (figures == 3)
        {
            windows.Add(new("seven_day_fable", 60, reset, At, null));
        }

        board.Heard(windows, At);

        return board;
    }

    /// <summary>How many of a usage strip's pairs are shown and drawn: a pair a FittingStrip left out is arranged empty.</summary>
    private static int PairsDrawn(UsageStrip strip) =>
        strip.Pairs.Count(pair => pair.Visibility == Visibility.Visible && LayoutInformation.GetLayoutSlot(pair).Width > 0);

    /// <summary>
    /// Whether every shown pair of <paramref name="strip"/> offers its hover text, it is the view model's, and the
    /// figure answers the mouse in the caption, which is the window's drag area (WindowChrome).
    /// </summary>
    private static bool TipsOn(UsageStrip strip, MainViewModel viewModel)
    {
        UsageFigure[] figures = [viewModel.CurrentUsage, viewModel.WeekUsage, viewModel.FableUsage];

        return strip.Pairs.Zip(figures).Where(pair => pair.First.Visibility == Visibility.Visible).All(pair =>
        {
            var host = (StackPanel)pair.First.Children[1];

            return ToolTipService.GetIsEnabled(host)
                && Equals(host.ToolTip, pair.Second.HoverText)
                && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome((TextBlock)host.Children[1]);
        });
    }

    /// <summary>
    /// The arranged, desired and drawn widths of <paramref name="strip"/>, and its slot: a pair the strip left
    /// out is arranged empty, so what is drawn is the sum of the pairs with room.
    /// </summary>
    private static StripFit FitOf(string line, UsageStrip strip) =>
        new(
            line,
            strip.Strip.RenderSize.Width,
            strip.Strip.DesiredSize.Width,
            strip.Strip.Children.Cast<FrameworkElement>()
                .Where(child => LayoutInformation.GetLayoutSlot(child).Width > 0)
                .Sum(child => child.DesiredSize.Width),
            LayoutInformation.GetLayoutSlot(strip).Width,
            strip.DesiredSize.Width);

    /// <summary>
    /// Sweeps the window one DIP at a time across <paramref name="widths"/> and records where the counts and the
    /// usage are; a second layout at each width must change nothing.
    /// </summary>
    private List<UsageAt> SweepUsage(MainWindow window, MainViewModel viewModel, IEnumerable<double> widths)
    {
        var seen = new List<UsageAt>();

        foreach (var width in widths)
        {
            window.Width = width;
            window.UpdateLayout();
            _harness.Pump(DispatcherPriority.Background);
            window.UpdateLayout();

            var usageInCaption = window.CaptionUsage.Visibility == Visibility.Visible;
            var rowUsage = window.RowUsage.Visibility == Visibility.Visible;

            seen.Add(new UsageAt(
                width,
                window.CaptionCounts.Strip.Tier,
                window.CaptionCounts.Strip.HasDropped,
                window.CaptionCounts.DesiredSize.Width,
                window.CaptionCounts.Visibility == Visibility.Visible,
                usageInCaption,
                window.CaptionUsage.Strip.Tier,
                window.CaptionSlot.UsageRoom,
                PairsDrawn(window.CaptionUsage),
                window.CountsRow.Visibility == Visibility.Visible,
                window.RowCounts.Visibility == Visibility.Visible,
                rowUsage,
                PairsDrawn(window.RowUsage),
                window.RowUsage.TranslatePoint(default, window).X + window.RowUsage.RenderSize.Width,
                window.RowCounts.TranslatePoint(default, window).X,
                TipsOn(usageInCaption ? window.CaptionUsage : window.RowUsage, viewModel),
                [
                    .. usageInCaption ? [FitOf("caption", window.CaptionUsage)] : Array.Empty<StripFit>(),
                    .. rowUsage ? [FitOf("row", window.RowUsage)] : Array.Empty<StripFit>(),
                ]));

            window.UpdateLayout();
            _harness.Pump(DispatcherPriority.Background);

            Assert.True(
                (window.CaptionUsage.Visibility == Visibility.Visible) == usageInCaption
                    && (window.RowUsage.Visibility == Visibility.Visible) == rowUsage,
                $"At {width} the usage moved without the width changing.");
        }

        return seen;
    }

    /// <summary>The window's widths from its minimum to 1,100, one DIP apart: past full words for both strips.</summary>
    private static List<double> UsageWidths(MainWindow window) =>
        [.. Enumerable.Range(0, (int)(1100 - window.MinWidth) + 1).Select(step => window.MinWidth + step)];

    /// <summary>
    /// Sweeps a window whose board holds <paramref name="usage"/>, with the counts of a busy day. The display
    /// scale goes to <see cref="_usageScale"/>, for the output of the measuring test.
    /// </summary>
    private List<UsageAt> SweepWith(UsageBoard usage) =>
        WithWindow(
            registry => registry.Working("busy", At),
            (window, viewModel) =>
            {
                BusyCounts(viewModel);
                _usageScale = VisualTreeHelper.GetDpi(window).DpiScaleX;

                return SweepUsage(window, viewModel, UsageWidths(window));
            },
            prepare: viewModel => viewModel.Tick(At),
            usage: usage);

    /// <summary>
    /// <strong>The counts decide before the usage</strong> (ruling R9): at every width, the counts choose the
    /// same tier, drop the same counts and ask for the same width with three figures beside them as with none.
    /// </summary>
    /// <remarks>
    /// The two windows are swept in the same run, so the widths are this run's measurements and nothing is
    /// written down. The plant that measures the usage first fails here: the counts then lose words to labels.
    /// </remarks>
    [Fact]
    public void The_counts_decide_before_the_usage()
    {
        var alone = SweepWith(new UsageBoard()).ToDictionary(at => at.Width);
        var beside = SweepWith(UsageOf(3));

        Assert.Contains(beside, at => at.UsageInCaption);

        foreach (var at in beside)
        {
            var before = alone[at.Width];

            Assert.True(
                (at.CountsTier, at.CountsDropped, at.CountsDesired) == (before.CountsTier, before.CountsDropped, before.CountsDesired),
                $"At {at.Width} the counts chose tier {at.CountsTier}, dropped: {at.CountsDropped}, {at.CountsDesired} wide, beside the usage; alone, tier {before.CountsTier}, dropped: {before.CountsDropped}, {before.CountsDesired} wide.");
        }
    }

    /// <summary>
    /// <strong>The usage drops its labels before its numbers</strong> (R9), with two figures and with three: as the
    /// room the counts leave shrinks, the usage shows labels, then numbers only, then leaves the caption; each is
    /// reached, and the same room always gives the same answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Judged by the room, not by the window's width,</strong> because the counts decide first: when the
    /// window widens past a step of the counts' ladder, the counts take their words back and the room for the usage
    /// shrinks. The usage can then leave the caption at a wider window than one where it fitted. That is ruling
    /// R9's order, and the output lists the widths where it happens.
    /// </para>
    /// <para>
    /// Also the widths the XAML remark records, measured in this run and written to the test's output: what a
    /// 520 window shows, and where the labels first stand beside the counts' full words.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void The_usage_drops_its_labels_before_its_numbers(int figures)
    {
        var seen = SweepWith(UsageOf(figures));

        // 0 on the row, 1 numbers only in the caption, 2 labels in the caption.
        static int StateOf(UsageAt at) => !at.UsageInCaption ? 0 : at.UsageTier == 1 ? 1 : 2;

        Assert.All(seen, at => Assert.True(!at.UsageInCaption || at.UsageTier is 0 or 1, $"At {at.Width} the usage showed tier {at.UsageTier}."));
        Assert.Equal([0, 1, 2], seen.Select(StateOf).Distinct().Order());

        var byRoom = seen.OrderBy(at => at.UsageRoom).ThenBy(at => at.Width).ToList();

        Assert.All(byRoom.Zip(byRoom.Skip(1)), pair => Assert.True(
            StateOf(pair.Second) >= StateOf(pair.First) && (pair.First.UsageRoom != pair.Second.UsageRoom || StateOf(pair.First) == StateOf(pair.Second)),
            $"With {pair.First.UsageRoom} of room (at {pair.First.Width}) the usage was in state {StateOf(pair.First)}, and with {pair.Second.UsageRoom} (at {pair.Second.Width}) in state {StateOf(pair.Second)}."));

        var leavesWider = seen.Zip(seen.Skip(1))
            .Where(pair => StateOf(pair.Second) < StateOf(pair.First))
            .Select(pair => $"{pair.Second.Width} (counts tier {pair.First.CountsTier} to {pair.Second.CountsTier}, usage state {StateOf(pair.First)} to {StateOf(pair.Second)})");

        var numbersFrom = seen.First(at => StateOf(at) == 1).Width;
        var labelsFrom = seen.First(at => StateOf(at) == 2).Width;
        var beside = seen.First(at => StateOf(at) == 2 && at.CountsTier == 0 && at.CountsInCaption).Width;
        var at520 = seen.Single(at => at.Width == 520);

        _output.WriteLine(
            $"{figures} figures at {_usageScale:P0}: numbers only from {numbersFrom}, labels from {labelsFrom}, "
            + $"labels beside full-word counts from {beside}. At 520: usage in state {StateOf(at520)}, counts tier {at520.CountsTier}, "
            + $"counts in the caption: {at520.CountsInCaption}, usage room {at520.UsageRoom}. "
            + $"Steps back as the window widens: {string.Join("; ", leavesWider)}. "
            + $"By width: {string.Join(", ", Ranges(seen, StateOf))}.");
    }

    /// <summary>The widths, as ranges, at which the usage was on the row, numbers only, or labels.</summary>
    private static IEnumerable<string> Ranges(List<UsageAt> seen, Func<UsageAt, int> stateOf)
    {
        string[] names = ["row", "numbers", "labels"];
        var start = 0;

        for (var i = 1; i <= seen.Count; i++)
        {
            if (i == seen.Count || stateOf(seen[i]) != stateOf(seen[start]))
            {
                yield return $"{seen[start].Width}-{seen[i - 1].Width} {names[stateOf(seen[start])]}";
                start = i;
            }
        }
    }

    /// <summary>
    /// <strong>When the numbers do not fit, all three figures leave the caption</strong> (R9): in the caption the
    /// usage shows all three or none, at every width; when it is not there, all three are on the row.
    /// </summary>
    [Fact]
    public void When_the_numbers_do_not_fit_all_three_leave_the_caption()
    {
        var seen = SweepWith(UsageOf(3));

        foreach (var at in seen)
        {
            if (at.UsageInCaption)
            {
                Assert.True(at.CaptionPairsDrawn == 3, $"At {at.Width} the caption drew {at.CaptionPairsDrawn} of the three figures.");
            }
            else
            {
                Assert.True(at.RowUsage, $"At {at.Width} the usage was in neither place.");
            }
        }

        Assert.Contains(seen, at => at.UsageInCaption);
        Assert.Contains(seen, at => !at.UsageInCaption);

        // On the row, the ladder starts again from labels: where the usage first leaves the caption, the row has
        // the whole window's width and shows all three.
        var firstLeft = seen.Last(at => !at.UsageInCaption);

        Assert.Equal(3, firstLeft.RowPairsDrawn);
    }

    /// <summary>
    /// <strong>The row shows only what left the caption, the usage at the left</strong> (R9, "shared row"): the
    /// counts are on the row exactly when the caption's counts dropped one, the usage exactly when it is not in the
    /// caption; when both are there, the usage ends left of where the counts begin. When the counts leave, the
    /// usage leaves with them.
    /// </summary>
    [Fact]
    public void The_row_shows_only_what_left_the_caption_usage_at_the_left()
    {
        var seen = SweepWith(UsageOf(3));

        Assert.Contains(seen, at => at.RowCounts && at.RowUsage);

        foreach (var at in seen)
        {
            Assert.True(at.RowCounts == at.CountsDropped, $"At {at.Width} the counts were on the row: {at.RowCounts}; the caption's counts dropped one: {at.CountsDropped}.");
            Assert.True(at.RowCounts != at.CountsInCaption, $"At {at.Width} the counts were in the caption: {at.CountsInCaption}, on the row: {at.RowCounts}.");
            Assert.True(at.RowUsage != at.UsageInCaption, $"At {at.Width} the usage was in the caption: {at.UsageInCaption}, on the row: {at.RowUsage}.");
            Assert.True(!at.RowCounts || at.RowUsage, $"At {at.Width} the counts left the caption and the usage stayed.");

            if (at.RowCounts && at.RowUsage)
            {
                Assert.True(at.RowUsageRight <= at.RowCountsLeft + 0.5, $"At {at.Width} the usage on the row ends at {at.RowUsageRight}, right of the counts at {at.RowCountsLeft}.");
            }
        }
    }

    /// <summary>
    /// <strong>The row is up when either strip left the caption, and only then</strong> (R9): there are widths
    /// where the usage alone left, and the row is up for it; and widths where neither did, and it is down.
    /// </summary>
    [Fact]
    public void The_row_shows_when_either_strip_left_the_caption()
    {
        var seen = SweepWith(UsageOf(3));

        Assert.All(seen, at => Assert.True(at.RowUp == (at.RowCounts || at.RowUsage), $"At {at.Width} the row was up: {at.RowUp}, with the counts: {at.RowCounts}, the usage: {at.RowUsage}."));
        Assert.Contains(seen, at => at.RowUp && at.RowUsage && !at.RowCounts);
        Assert.Contains(seen, at => !at.RowUp);
    }

    /// <summary>
    /// <strong>The usage strip is arranged at the width it measured</strong>, in the caption and on the row, at
    /// every width: the counts' check (the T1.42 review) applied to the usage, and one check more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FittingStrip draws the pairs its measure kept without checking the width again (T1.42), so its parent must
    /// arrange it at its desired width. The test finds three faults:
    /// </para>
    /// <list type="bullet">
    /// <item>A strip that draws more than it measured: its pairs with room are wider than its arrangement.</item>
    /// <item>A strip arranged wider than it measured, by more than a pixel of rounding: the usage is then not
    /// beside the counts.</item>
    /// <item>A <see cref="UsageStrip"/> whose slot is narrower than it asked for, margin included. WPF arranges
    /// such an element at its desired width all the same and clips it to the slot, so the inner strip's widths
    /// look right while the first figure is cut ("5%" drawn as "i%"). Only the slot shows it.</item>
    /// </list>
    /// <para>
    /// <see cref="SummarySlot"/> gives the usage the room left of the counts, never less than it measured, and the
    /// strip's own alignment takes its desired width out of that room.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void The_usage_strip_is_arranged_at_the_width_it_measured(int figures)
    {
        var seen = SweepWith(UsageOf(figures));
        var fits = seen.SelectMany(at => at.UsageFits.Select(fit => (at.Width, Fit: fit))).ToList();

        Assert.All(seen, at => Assert.NotEmpty(at.UsageFits));
        Assert.Contains(fits, pair => pair.Fit.Line == "caption");
        Assert.Contains(fits, pair => pair.Fit.Line == "row");

        foreach (var (width, fit) in fits)
        {
            Assert.True(fit.DrawnWidth <= fit.ArrangedWidth + 0.01, $"At {width} the usage strip on the {fit.Line} drew {fit.DrawnWidth} in an arrangement {fit.ArrangedWidth} wide.");
            Assert.True(Math.Abs(fit.ArrangedWidth - fit.DesiredWidth) <= 1, $"At {width} the usage strip on the {fit.Line} measured {fit.DesiredWidth} and was arranged at {fit.ArrangedWidth}.");
            Assert.True(fit.HostSlotWidth >= fit.HostDesiredWidth - 0.01, $"At {width} the usage on the {fit.Line} asked for {fit.HostDesiredWidth} and got a slot {fit.HostSlotWidth} wide: it is clipped.");
        }
    }

    /// <summary>
    /// <strong>The hover text is on always</strong> (R11), unlike the counts' tooltip: with labels, with numbers
    /// only, and on the row, each shown pair offers the view model's text.
    /// </summary>
    [Fact]
    public void The_hover_text_is_on_at_both_tiers_and_on_the_row()
    {
        var seen = SweepWith(UsageOf(3));

        Assert.Contains(seen, at => at.UsageInCaption && at.UsageTier == 0);
        Assert.Contains(seen, at => at.UsageInCaption && at.UsageTier == 1);
        Assert.Contains(seen, at => at.RowUsage);
        Assert.All(seen, at => Assert.True(at.TipsOn, $"At {at.Width} a shown pair offered no hover text, or not the view model's."));
    }

    /// <summary>
    /// <strong>Before the first post the usage strip is collapsed, not hidden</strong>, in the caption and on the row:
    /// the slot is the counts' alone, as before MOD.7.
    /// </summary>
    [Fact]
    public void Before_the_first_post_the_usage_strip_is_collapsed()
    {
        var (caption, row, room) = WithWindow(
            registry => registry.Working("busy", At),
            (window, viewModel) =>
            {
                window.Width = 900;
                window.UpdateLayout();

                return (window.CaptionUsage.Visibility, window.RowUsage.Visibility, window.CaptionUsage.DesiredSize.Width);
            },
            prepare: viewModel => viewModel.Tick(At));

        Assert.Equal((Visibility.Collapsed, Visibility.Collapsed, 0.0), (caption, row, room));
    }

    // ---- Colour comes from the accent ----------------------------------------------------------

    [Theory]
    [InlineData(SessionState.NeedsPermission, "#FFFF6B5E")]
    [InlineData(SessionState.Error, "#FFFFB454")]
    [InlineData(SessionState.Unread, "#FF55C96A")]
    [InlineData(SessionState.Working, "#FF5AA9FF")]
    public void The_led_takes_its_colour_from_the_accent(SessionState state, string expected)
    {
        var colour = WithWindow(
            registry => Reach(registry, "s-1", state),
            (window, _) => (StaHarness.Find<Ellipse>(RowFor(window, "s-1"))?.Fill as SolidColorBrush)
                ?.Color.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected, colour);
    }

    // ---- Motion, as it actually renders --------------------------------------------------------

    /// <summary>
    /// The template really does start the animation — not merely expose the intent. Asserted
    /// through <see cref="UIElement.HasAnimatedProperties"/> on the LED itself.
    /// </summary>
    [Theory]
    [InlineData(SessionState.NeedsPermission, true)]
    [InlineData(SessionState.NeedsQuestion, true)]
    [InlineData(SessionState.Working, true)]
    [InlineData(SessionState.Error, false)]
    [InlineData(SessionState.Unread, false)]
    [InlineData(SessionState.Acked, false)]
    public void Only_red_and_working_animate(SessionState state, bool animated)
    {
        var moving = WithWindow(
            registry => Reach(registry, "s-1", state),
            (window, _) =>
            {
                var led = StaHarness.Find<Ellipse>(RowFor(window, "s-1"));
                return led is not null && led.HasAnimatedProperties;
            },
            showQuiet: true);

        Assert.Equal(animated, moving);
    }

    /// <summary>
    /// …and with reduced motion asked for, the same rows do not animate. The pair is the point:
    /// the first test alone passes for a template that animates everything, this one alone for a
    /// template that animates nothing.
    /// </summary>
    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.Working)]
    public void Reduced_motion_leaves_the_led_still(SessionState state)
    {
        var moving = WithWindow(
            registry => Reach(registry, "s-1", state),
            (window, _) => StaHarness.Find<Ellipse>(RowFor(window, "s-1"))?.HasAnimatedProperties,
            motionAllowed: false);

        Assert.False(moving);
    }

    /// <summary>
    /// Nothing else in the window moves — not a header, not a footer, not the chrome. Asserted
    /// over every animatable element the templates produced rather than over the ones this test
    /// thought to look at.
    /// </summary>
    [Fact]
    public void Nothing_but_the_leds_moves()
    {
        var animated = WithWindow(
            registry =>
            {
                registry.Working("busy", At);
                registry.Working("blocked", At);
                registry.Blocked("blocked", At.AddMinutes(1));
                registry.Quiet("seen", At);
            },
            (window, _) => StaHarness.FindAll<UIElement>(window)

                .Count(element => element.HasAnimatedProperties));

        // Exactly the two LEDs that are entitled to move: the permission and the working row.
        Assert.Equal(2, animated);
    }

    // ---- The expanded row ------------------------------------------------------------------------

    [Fact]
    public void An_expanded_row_shows_the_whole_exchange_and_no_phase_placeholder()
    {
        var found = WithWindow(
            registry =>
            {
                var promptId = registry.Working("finished", At, prompt: "write the tests");
                registry.Finished("finished", At.AddMinutes(1), promptId, answer: "Added 23 tests.");
            },
            (window, viewModel) =>
            {
                var row = viewModel.Rows.OfType<SessionViewModel>().Single();
                row.IsExpanded = true;
                window.UpdateLayout();

                var texts = StaHarness.FindAll<TextBlock>(RowFor(window, "finished"))
                    .Where(block => block.IsVisible)
                    .Select(TextOf)
                    .ToList();

                // By name: a collapsed button is never measured, so its template — and the "Open
                // terminal" text inside it — is never built, and a search by text finds nothing.
                var terminal = StaHarness.FindAll<Button>(RowFor(window, "finished"))
                    .SingleOrDefault(button => button.Name == "OpenTerminalButton");

                return (Texts: texts, TerminalVisible: terminal?.IsVisible, HasTerminal: terminal is not null,
                    ShortId: row.ShortId, Asked: $"YOU ASKED · {row.AskedAtText} · {row.AskedAgoText}");
            });

        Assert.Contains("write the tests", found.Texts);

        // The clock time and the time ago (T1.40), as the row's own view model states them, on
        // one line: the markup is what is under test here, not the clock.
        Assert.Contains(found.Asked, found.Texts);
        Assert.EndsWith(" ago", found.Asked, StringComparison.Ordinal);
        Assert.Contains("Added 23 tests.", found.Texts);
        Assert.Contains("CLAUDE ANSWERED", found.Texts);

        // T1.47, issue #60: the reserved "Open terminal · PHASE 2" button is hidden, because a user
        // does not know the plan's phases and a dead button reads as unfinished. It is kept in the
        // markup, collapsed, for Phase 2 navigation to bring back — so it is found, and not shown.
        // No visible text in the expanded row names a phase, and the short id stays.
        Assert.True(found.HasTerminal, "the terminal slot should stay in the markup for Phase 2");
        Assert.False(found.TerminalVisible);
        Assert.DoesNotContain(found.Texts, text => text.Contains("PHASE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Open terminal", found.Texts);
        Assert.Contains(found.ShortId, found.Texts);
    }

    /// <summary>
    /// With the terminal slot hidden (T1.47, issue #60), the expanded row's action line still lays
    /// out: the session id sits beside the Acknowledge button with the button's own gap, inside
    /// the row, at every width from the window's minimum up — whole widths and the fractional ones
    /// a 150% scale produces.
    /// </summary>
    /// <remarks>
    /// This machine runs at 100%, and the DPI cannot be changed inside a test process (see the
    /// fractional-scale notes on <c>FittingStrip</c>'s tests). The fractional widths are the
    /// part of that hazard that can be reproduced here: widths of k / 1.5 device pixels.
    /// </remarks>
    [Fact]
    public void The_expanded_action_line_lays_out_without_the_terminal_slot()
    {
        var failures = WithWindow(
            registry =>
            {
                var promptId = registry.Working("finished", At, prompt: "write the tests");
                registry.Finished("finished", At.AddMinutes(1), promptId, answer: "Added 23 tests.");
            },
            (window, viewModel) =>
            {
                var row = viewModel.Rows.OfType<SessionViewModel>().Single();
                row.IsExpanded = true;
                window.UpdateLayout();

                var presenter = RowFor(window, "finished");
                var buttons = StaHarness.FindAll<Button>(presenter).ToList();
                var ack = buttons.Single(button => Equals(button.Content, "✓ Acknowledge"));
                var id = buttons.Single(button => button.Command == row.CopyIdCommand);
                var bad = new List<string>();

                var widths = Enumerable.Range(0, 300).Select(step => window.MinWidth + (step / 1.5)).ToList();

                foreach (var width in widths)
                {
                    window.Width = width;
                    window.UpdateLayout();

                    var ackRight = ack.TranslatePoint(new Point(ack.ActualWidth, 0), presenter).X + ack.Margin.Right;
                    var idLeft = id.TranslatePoint(new Point(0, 0), presenter).X;
                    var idRight = id.TranslatePoint(new Point(id.ActualWidth, 0), presenter).X;

                    if (!ack.IsVisible || !id.IsVisible || id.ActualWidth <= 0 ||
                        Math.Abs(idLeft - ackRight) > 0.01 || idRight > presenter.ActualWidth + 0.01)
                    {
                        bad.Add($"{width:F2}: ack ends {ackRight:F2}, id {idLeft:F2}–{idRight:F2}, row {presenter.ActualWidth:F2}");
                    }
                }

                return bad;
            });

        Assert.Empty(failures);
    }

    /// <summary>
    /// <strong>The expanded row shows the id, and the collapsed row does not.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves in one test because they are one rule, and §9 now states it: "The id appears
    /// here only — the session row does not carry it." §9 lists exactly four things on the session
    /// row and an id is not among them.
    /// </para>
    /// <para>
    /// <strong>The negative half is the load-bearing one.</strong> The expanded content lives
    /// inside the same template as the collapsed row, so an id placed a few lines further out
    /// would render on every row in the window and nothing else in the suite would notice — it
    /// would simply look like a slightly busier list.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_id_appears_on_the_expanded_row_and_nowhere_else()
    {
        const string SessionId = "88a85f67-4c21-4f0e-9d3b-a1b2c3d4e5f6";

        var found = WithWindow(
            registry =>
            {
                var promptId = registry.Working(SessionId, At, prompt: "write the tests");
                registry.Finished(SessionId, At.AddMinutes(1), promptId, answer: "Added 23 tests.");
            },
            (window, viewModel) =>
            {
                var row = viewModel.Rows.OfType<SessionViewModel>().Single();

                var collapsed = VisibleTexts(window, SessionId);

                row.IsExpanded = true;
                window.UpdateLayout();

                var expanded = VisibleTexts(window, SessionId);

                return (Collapsed: collapsed, Expanded: expanded);
            });

        // Expanded: the first eight characters, bare — no label, no ellipsis, no prefix.
        Assert.Contains("88a85f67", found.Expanded);

        // …and never the whole thing on the row itself. The full value lives in the tooltip.
        Assert.DoesNotContain(SessionId, found.Expanded);

        // Collapsed: neither form appears at all.
        Assert.DoesNotContain("88a85f67", found.Collapsed);
        Assert.DoesNotContain(SessionId, found.Collapsed);

        // The control: the collapsed row is not simply empty — it renders, so the absence above
        // is the id being withheld rather than the row failing to draw.
        Assert.Contains("write the tests", found.Collapsed);
    }

    /// <summary>
    /// <strong>The title reaches the row, in the grouped view and in the flat one.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is one implicit <c>DataTemplate</c> keyed by type, so both views draw the same
    /// markup and the title reaches both without a second code path. That is a reason not to
    /// write one; it is not evidence, so the flat view is realized and read rather than argued
    /// about.
    /// </para>
    /// <para>
    /// The title and the prompt live in two <c>Run</c>s inside one <c>TextBlock</c>, and the
    /// assertion is on the whole line rather than on the title alone — a title rendered into its
    /// own block, or with the separator lost, would satisfy "the row mentions Director" and would
    /// look wrong on screen.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_title_precedes_the_prompt_on_the_row_in_both_views()
    {
        const string Id = "titled";

        var grouped = WithWindow(
            registry => registry.Working(Id, At, prompt: "run the tests", title: "Director"),
            (window, _) => VisibleTexts(window, Id));

        var flat = WithWindow(
            registry => registry.Working(Id, At, prompt: "run the tests", title: "Director"),
            (window, _) => VisibleTexts(window, Id),
            grouped: false);

        Assert.Contains("Director — run the tests", grouped);
        Assert.Contains("Director — run the tests", flat);

        // The control: the flat view really was flat, and this is not the grouped one twice.
        Assert.Contains("WORKING", flat);
        Assert.DoesNotContain(flat, text => text.Contains("sessions", StringComparison.Ordinal));
    }

    /// <summary>
    /// <strong>An untitled row draws exactly what it drew before, with no separator.</strong>
    /// </summary>
    /// <remarks>
    /// Most sessions in the archive have no title, so this is the common row rather than the edge
    /// case. The assertion is equality with the prompt, not "the row contains the prompt": an
    /// empty prefix that still emitted its separator would leave every one of those rows opening
    /// with a dash, and a containment check would not see it.
    /// </remarks>
    [Fact]
    public void An_untitled_row_shows_the_prompt_alone()
    {
        const string Id = "untitled";

        var texts = WithWindow(
            registry => registry.Working(Id, At, prompt: "run the tests"),
            (window, _) => VisibleTexts(window, Id));

        Assert.Contains("run the tests", texts);
        Assert.DoesNotContain(texts, text => text.Contains('—'));
    }

    /// <summary>
    /// <strong>The drawn row shows a whole emoji at the snippet's boundary</strong> (T1.81, issue #21).
    /// </summary>
    /// <remarks>
    /// The issue says the drawn row was never looked at. The prompt is 139 ASCII characters, a family emoji (eight
    /// code units, one cluster) and more text, so the cut falls right after the emoji. The realized row's line is the
    /// whole emoji then the ellipsis, with no lone surrogate; <c>WithWindow</c> holds <c>BindingErrorWatch</c> clean.
    /// </remarks>
    [Fact]
    public void A_row_shows_a_whole_emoji_at_the_snippets_boundary()
    {
        const string Id = "emoji";
        var family = PromptSnippetTests.Cluster("family");
        var head = new string('a', SessionViewModel.SnippetLength - 1) + family;

        var texts = WithWindow(
            registry => registry.Working(Id, At, prompt: head + " and the rest of the prompt"),
            (window, _) => VisibleTexts(window, Id));

        var line = Assert.Single(texts, text => text.StartsWith("aaa", StringComparison.Ordinal));
        Assert.Equal(head + "…", line);
        Assert.Equal(line, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(line)));
    }

    /// <summary>
    /// <strong>A title latched from a declined event still repaints the row.</strong>
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the Registry's latch test, through a real projection and a real
    /// template: the session is already working, so the tool batch carrying the name changes no
    /// state at all. If the latch did not raise a change, the Registry would hold the title and
    /// the screen would never show it — the failure this feature is most likely to ship, and one
    /// that no view-model test can see.
    /// </remarks>
    [Fact]
    public void A_title_arriving_on_a_declined_event_reaches_the_screen()
    {
        const string Id = "late-title";

        RegistryHarness? live = null;

        var found = WithWindow(
            registry =>
            {
                live = registry;
                registry.Working(Id, At, prompt: "run the tests");
            },
            (window, _) =>
            {
                var before = VisibleTexts(window, Id);

                // A tool batch on a session that is already Working: the transition declines, so
                // only the latch can put this name on the screen.
                live!.Batch(Id, At.AddSeconds(1), title: "Director");
                window.UpdateLayout();

                return (Before: before, After: VisibleTexts(window, Id));
            });

        Assert.Contains("run the tests", found.Before);
        Assert.Contains("Director — run the tests", found.After);
    }


    /// <summary>
    /// <strong>Selection mode and the roster prompt both realize, and raise no binding error.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binding check is <c>WithWindow</c>'s and is the reason this test exists at all: a
    /// misspelled path fails silently in WPF — the element simply shows nothing — so a tick that
    /// never appeared and a tick that appeared correctly look identical to every other assertion
    /// here.
    /// </para>
    /// <para>
    /// The mode is entered on a realized window rather than before it, which is safe: it is the
    /// <em>Grouped/Flat</em> toggle that raises binding errors on a live window (issue #23), and
    /// nothing here touches it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Selection_mode_and_the_prompt_render_without_binding_errors()
    {
        var found = WithWindow(
            registry =>
            {
                registry.Working("s-1", At, prompt: "run the tests", title: "Director");
                registry.Working("s-2", At, prompt: "review it", title: "Coder");
            },
            (window, _) =>
            {
                var texts = StaHarness.FindAll<TextBlock>(window)
                    .Where(block => block.IsVisible)
                    .Select(TextOf)
                    .ToList();

                return (Texts: texts, Boxes: StaHarness.FindAll<TextBox>(window).Count);
            },
            prepare: viewModel =>
            {
                viewModel.IsSelecting = true;

                foreach (var row in viewModel.Rows.OfType<SessionViewModel>())
                {
                    row.IsSelected = true;
                }

                viewModel.GroupSelectedCommand.Execute(null);
                viewModel.IsSelecting = true;
            });

        Assert.Contains("Selecting · 0 chosen", found.Texts);
        Assert.Contains(RosterPromptViewModel.Question, found.Texts);

        // The roster's own name is the only text input the window ever shows.
        Assert.Equal(1, found.Boxes);
    }

    /// <summary>
    /// <strong>An untitled row shows why it cannot be ticked.</strong>
    /// </summary>
    /// <remarks>
    /// Asserted on the realized row rather than on the view model, because the refusal exists to be
    /// seen: a row that does nothing when clicked and says nothing is indistinguishable from a bug,
    /// and a property nobody rendered would say nothing.
    /// </remarks>
    [Fact]
    public void An_untitled_row_shows_why_it_cannot_be_ticked()
    {
        var texts = WithWindow(
            registry => registry.Working("s-1", At, prompt: "run the tests"),
            (window, _) =>
            {
                return VisibleTexts(window, "s-1");
            },
            prepare: viewModel => viewModel.IsSelecting = true);

        Assert.Contains("no name to remember", texts);
    }
    /// <summary>Every visible TextBlock in one session's row.</summary>

    /// <summary>
    /// <strong>A ROW BOUND TO AN INTERRUPTED SESSION ACTUALLY DRAWS (issue #28).</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this closes that no unit test can.</strong> <c>SilenceVisualsTests</c> asserts
    /// that <see cref="RowVisuals.BadgeOf"/> returns <c>INTERRUPTED</c> and that
    /// <see cref="RowVisuals.AccentOf"/> returns grey. Neither of those calls the markup. This is
    /// the first time anything binds a row to the new state, so it is the first time the badge
    /// template is asked for a state no <c>DataTrigger</c> names, the accent brush is resolved for
    /// one, and every converter on that path is handed one.
    /// </para>
    /// <para>
    /// <strong>"We changed no markup" is a different claim from "the new state draws."</strong> A
    /// state-keyed trigger with no matching case leaves a badge blank; a brush lookup that misses
    /// falls back to transparent; and both render perfectly happily, which is exactly the failure
    /// WPF is best at hiding. <see cref="BindingErrorWatch"/> in the fixture catches the third
    /// case — a misspelled path shows nothing and says nothing — and it is asserted for every
    /// window this class realizes.
    /// </para>
    /// <para>
    /// <strong>Quiet, so the group has to be opened.</strong> An interrupted session is in the
    /// Quiet band, and a quiet session has no row of its own until its group is expanded (Design
    /// §6 rule 2) — which is itself worth pinning: it is the band placement showing up on screen
    /// rather than in a rank table.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_interrupted_row_renders_its_badge_and_its_accent()
    {
        const string Id = "gone-quiet";

        var (texts, accent, badge) = WithWindow(
            registry => registry.Silent(Id, At, title: "Coder"),
            (window, _) =>
            {
                var row = RowFor(window, Id);
                var session = (SessionViewModel)row.DataContext;

                return (VisibleTexts(window, Id), session.Accent, session.BadgeText);
            },
            showQuiet: true);

        // The word the operator asked for, on screen rather than merely returned by a method.
        Assert.Contains("INTERRUPTED", texts);

        // Grey, and reached through the row rather than through RowVisuals directly.
        Assert.Equal(Accent.Grey, accent);
        Assert.Equal("INTERRUPTED", badge);

        // The age reads "ago", not a bare duration: this row is not claiming to be busy.
        Assert.Contains(texts, text => text.EndsWith(" ago", StringComparison.Ordinal));
    }
    /// <summary>
    /// <strong>An Error row says which error it was, beside its badge</strong> (T1.53, issue #67).
    /// </summary>
    /// <remarks>
    /// The failure enters as the wire sends it, through the real mapper, rather than as a built
    /// <see cref="StopFailure"/>: the defect was in the field name, and a built event has no field
    /// names. The kind must be in the badge's own line, so that it reads as the badge's detail.
    /// </remarks>
    [Fact]
    public void An_error_row_shows_its_kind_beside_the_badge()
    {
        const string Id = "failed";

        var (badgeLine, texts) = WithWindow(
            registry =>
            {
                registry.Working(Id, At);

                var mapper = new ClaudeDashboard.App.Ingress.HookEventMapper(new FakeClock(At.AddMinutes(1)));
                var payload = System.Text.Json.JsonSerializer.Deserialize<ClaudeDashboard.App.Ingress.HookPayload>(
                    """{"hook_event_name":"StopFailure","session_id":"failed","error":"rate_limit"}""");
                registry.Apply(mapper.Map(payload!).Event!);
            },
            (window, _) =>
            {
                var row = RowFor(window, Id);
                var badge = StaHarness.FindAll<TextBlock>(row).Single(block => block.IsVisible && TextOf(block) == "ERROR");
                var line = (Panel)VisualTreeHelper.GetParent((Border)VisualTreeHelper.GetParent(badge));

                return (
                    StaHarness.FindAll<TextBlock>(line).Where(block => block.IsVisible).Select(TextOf).ToList(),
                    VisibleTexts(window, Id));
            });

        // The kind comes right after the badge. The age and the workspace follow it on the line.
        Assert.Equal(["ERROR", "rate_limit"], badgeLine.Take(2));
        Assert.Single(texts, text => text == "rate_limit");
    }

    private static List<string> VisibleTexts(MainWindow window, string sessionId) =>
        [.. StaHarness.FindAll<TextBlock>(RowFor(window, sessionId))
            .Where(block => block.IsVisible)
            .Select(TextOf)];

    /// <summary>
    /// What a <see cref="TextBlock"/> actually holds, <strong>inline runs included</strong>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>TextBlock.Text</c> reads back only what was set through <c>Text</c>.</strong>
    /// Content authored as inlines reads back empty — <em>whatever the count, one <c>Run</c>
    /// included</em>. Measured on this harness:
    /// </para>
    /// <list type="table">
    /// <item><description><c>Text = "plain"</c> → <c>Inlines.Count</c> 1, <c>Text</c> "plain"</description></item>
    /// <item><description>one explicit <c>Run</c> → <c>Inlines.Count</c> 1, <c>Text</c> ""</description></item>
    /// <item><description>two <c>Run</c>s → <c>Inlines.Count</c> 2, <c>Text</c> ""</description></item>
    /// <item><description>four <c>Run</c>s → <c>Inlines.Count</c> 4, <c>Text</c> ""</description></item>
    /// </list>
    /// <para>
    /// <strong>An earlier version of this remark said "two or more inlines", and that error cost a
    /// test.</strong> The count cannot tell the two cases apart at all — the readable block and the
    /// blind one both report <c>Inlines.Count</c> of 1 — so an audit asking "which blocks have two
    /// or more inlines?" clears a single-<c>Run</c> block that is equally invisible. Writing the
    /// threshold down slightly wrong was worse than not writing it down, because the next reader
    /// trusts it: it is why
    /// <see cref="A_row_shows_its_prompt_in_the_mono_face"/> was left asserting a hidden element
    /// through the very commit that fixed this problem everywhere else.
    /// </para>
    /// <para>
    /// A <see cref="TextRange"/> over the block's own content start and end returns what is there
    /// in every shape. That makes an assertion of the form "the row does not show X" mean it,
    /// rather than passing because the reader could not see X at all — which is the direction a
    /// blind spot here fails in, and the reason this is worth a helper and a note.
    /// </para>
    /// </remarks>
    private static string TextOf(TextBlock block) =>
        new TextRange(block.ContentStart, block.ContentEnd).Text;

    [Fact]
    public void A_collapsed_row_shows_no_exchange()
    {
        var texts = WithWindow(
            registry =>
            {
                var promptId = registry.Working("finished", At, prompt: "write the tests");
                registry.Finished("finished", At.AddMinutes(1), promptId, answer: "Added 23 tests.");
            },
            (window, _) => StaHarness.FindAll<TextBlock>(RowFor(window, "finished"))
                .Where(block => block.IsVisible)
                .Select(TextOf)
                .ToList());

        Assert.DoesNotContain("Added 23 tests.", texts);
        Assert.DoesNotContain("CLAUDE ANSWERED", texts);
    }

    /// <summary>
    /// <strong>The Ack control is live: enabled, and invoking the command.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted on the control and on the effect, not on the view model. A disabled affordance
    /// looks identical whether it is deliberately not yet wired — which is what T1.11 shipped —
    /// or wired and disabled by a mistaken <c>CanExecute</c>, so a test that only checked "a
    /// command exists" would pass over exactly the regression that matters.
    /// </para>
    /// <para>
    /// Invoked through the button's automation peer, which is what a click goes through, rather
    /// than by calling the command: that path also proves the <c>Command</c> binding resolved.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_ack_button_is_enabled_and_invokes_the_command()
    {
        var sink = new RecordingEventSink();

        var enabled = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection,
                policy,
                new AckPublisher(sink, new FakeClock(), Serilog.Core.Logger.None),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());

            var promptId = registry.Working("finished", FakeClock.DefaultStart);
            registry.Finished("finished", FakeClock.DefaultStart.AddMinutes(1), promptId);

            var window = new MainWindow(viewModel, TestTrays.For(registry.Projection));

            try
            {
                Realize(window);

                var ack = StaHarness.FindAll<Button>(RowFor(window, "finished"))
                    .Single(button => button.Content as string == "✓ Ack");

                var wasEnabled = ack.IsEnabled;

                var peer = new ButtonAutomationPeer(ack);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();

                // The peer posts the click at Input priority; without draining, nothing happens.
                _harness.Pump(DispatcherPriority.Background);

                return wasEnabled;
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.True(enabled, "the acknowledge button must be enabled on a row that can be acknowledged");

        // And invoking it did what it says: one ack, for that session, on the channel.
        var published = Assert.Single(sink.Published);
        var ack = Assert.IsType<ClaudeDashboard.Core.Events.Ack>(published);
        Assert.Equal(new SessionId("finished"), ack.SessionId);
    }

    /// <summary>
    /// The hook-route notice is on screen (the operator's ruling of 2026-10-01): hidden while there
    /// is nothing to say, then shown in the realized window with its whole text when a start finds
    /// the plugin turned off — with no binding errors — and leading the tray's tooltip as well.
    /// </summary>
    [Fact]
    public void The_hook_notice_shows_in_the_window_and_leads_the_tray_tooltip()
    {
        var notice = new ClaudeDashboard.App.Setup.HookNotice();

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var tray = TestTrays.For(registry.Projection, notice: notice);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                var hiddenBefore = !window.NoticeRow.IsVisible;

                notice.ShowPluginDisabled();
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Empty(bindings.Problems);

                return (
                    HiddenBefore: hiddenBefore,
                    VisibleAfter: window.NoticeRow.IsVisible,
                    Height: window.NoticeRow.ActualHeight,
                    Lines: NoticeLines(window),
                    Tooltip: tray.Tooltip);
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.True(seen.HiddenBefore, "the notice row must be hidden while there is nothing to say");
        Assert.True(seen.VisibleAfter, "the notice row must be visible once a start finds the plugin turned off");
        Assert.True(seen.Height > 0, "the visible notice row must take room in the window");
        Assert.Equal([ClaudeDashboard.App.Setup.HookNotice.PluginDisabledText], seen.Lines);
        Assert.StartsWith(ClaudeDashboard.App.Setup.HookNotice.PluginDisabledShort, seen.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>The visible lines of the realized notice row, top to bottom.</summary>
    private static List<string> NoticeLines(MainWindow window) =>
        [.. StaHarness.FindAll<TextBlock>(window.NoticeLines)
            .Where(block => block.IsVisible)
            .Select(TextOf)];

    /// <summary>
    /// <strong>Two notices at once both show, in order, both lead the tooltip, and each clears by
    /// its own rule</strong> (T1.54, issue #71).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plugin notice comes first and the history notice second, because that is the board's
    /// order, not the order they were shown in: the history notice is shown first here.
    /// </para>
    /// <para>
    /// Then each rule, against the other notice still showing. An event does not clear a plugin that
    /// is turned off while Claude Code's settings still say off. A write that succeeds clears the
    /// history notice and leaves the plugin notice. A failing store shows it again. An event, once
    /// the settings say on, clears the plugin notice and leaves the history notice.
    /// </para>
    /// </remarks>
    [Fact]
    public void Two_notices_show_in_order_lead_the_tooltip_and_clear_by_their_own_rules()
    {
        var clock = new FakeClock();
        var pluginOn = false;
        var failing = true;

        var hook = new ClaudeDashboard.App.Setup.HookNotice();
        hook.ConfirmPluginWith(() => pluginOn, clock);
        var history = new ClaudeDashboard.App.Storage.HistoryNotice(() => failing);

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var board = new NoticeBoard(hook, history);
            using var tray = TestTrays.For(registry.Projection, clock: clock, notices: board);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            (List<string> Lines, string Tooltip) Look()
            {
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();
                return (window.NoticeRow.IsVisible ? NoticeLines(window) : [], tray.Tooltip);
            }

            try
            {
                Realize(window);

                // The history first, on the tick; then the plugin.
                tray.Tick(clock.Now);
                hook.ShowPluginDisabled();
                var both = Look();

                // An event while the settings still say off: both stay.
                hook.EventArrived();
                var afterEventOff = Look();

                // A write succeeds: the history clears on the next tick, the plugin stays.
                failing = false;
                clock.Advance(TimeSpan.FromSeconds(15));
                tray.Tick(clock.Now);
                var afterWrite = Look();

                // The store fails again, and the settings now say on: an event clears the plugin.
                failing = true;
                clock.Advance(TimeSpan.FromSeconds(15));
                tray.Tick(clock.Now);
                pluginOn = true;
                clock.Advance(ClaudeDashboard.App.Setup.HookNotice.RecheckInterval);
                hook.EventArrived();
                var afterEventOn = Look();

                Assert.Empty(bindings.Problems);

                return (both, afterEventOff, afterWrite, afterEventOn);
            }
            finally
            {
                window.Hide();
            }
        });

        const string Plugin = ClaudeDashboard.App.Setup.HookNotice.PluginDisabledShort;
        const string History = ClaudeDashboard.App.Storage.HistoryNotice.TrayShort;
        var pluginText = ClaudeDashboard.App.Setup.HookNotice.PluginDisabledText;
        const string HistoryText = ClaudeDashboard.App.Storage.HistoryNotice.WindowText;

        Assert.Equal([pluginText, HistoryText], seen.both.Lines);
        Assert.StartsWith($"{Plugin} · {History}", seen.both.Tooltip, StringComparison.Ordinal);

        Assert.Equal([pluginText, HistoryText], seen.afterEventOff.Lines);

        Assert.Equal([pluginText], seen.afterWrite.Lines);
        Assert.StartsWith(Plugin, seen.afterWrite.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(History, seen.afterWrite.Tooltip, StringComparison.Ordinal);

        Assert.Equal([HistoryText], seen.afterEventOn.Lines);
        Assert.StartsWith(History, seen.afterEventOn.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(Plugin, seen.afterEventOn.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The history notice and the sound notice show together, in the board's order</strong>
    /// (T1.55, issue #72), lead the tooltip in that order, and the sound notice clears at the tick
    /// after a device returns while the history notice stays.
    /// </summary>
    [Fact]
    public void The_history_and_sound_notices_show_together_in_order()
    {
        var clock = new FakeClock();
        var output = new SettableOutput { HasOutput = false };

        var hook = new ClaudeDashboard.App.Setup.HookNotice();
        var history = new ClaudeDashboard.App.Storage.HistoryNotice(() => true);
        var sound = new ClaudeDashboard.App.Adapters.SoundDeviceNotice(output);

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var board = new NoticeBoard(hook, history, sound);
            using var tray = TestTrays.For(registry.Projection, clock: clock, notices: board);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            (List<string> Lines, string Tooltip) Look()
            {
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();
                return (window.NoticeRow.IsVisible ? NoticeLines(window) : [], tray.Tooltip);
            }

            try
            {
                Realize(window);

                tray.Tick(clock.Now);
                var both = Look();

                output.HasOutput = true;
                clock.Advance(TimeSpan.FromSeconds(15));
                tray.Tick(clock.Now);
                var afterDevice = Look();

                Assert.Empty(bindings.Problems);

                return (both, afterDevice);
            }
            finally
            {
                window.Hide();
            }
        });

        const string History = ClaudeDashboard.App.Storage.HistoryNotice.TrayShort;
        const string Sound = ClaudeDashboard.App.Adapters.SoundDeviceNotice.TrayShort;

        Assert.Equal(
            [ClaudeDashboard.App.Storage.HistoryNotice.WindowText, ClaudeDashboard.App.Adapters.SoundDeviceNotice.WindowText],
            seen.both.Lines);
        Assert.StartsWith($"{History} · {Sound}", seen.both.Tooltip, StringComparison.Ordinal);

        Assert.Equal([ClaudeDashboard.App.Storage.HistoryNotice.WindowText], seen.afterDevice.Lines);
        Assert.DoesNotContain(Sound, seen.afterDevice.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The settings notice shows with the others, last, in the board's order</strong>
    /// (T1.56, issue #73), and leads the tooltip after them. <c>BindingErrorWatch</c> is clean.
    /// </summary>
    [Fact]
    public void The_settings_notice_shows_last_with_the_others()
    {
        var clock = new FakeClock();
        var paths = new ClaudeDashboard.App.Configuration.DashboardPaths(@"C:\data\ClaudeDashboard");
        var start = new ClaudeDashboard.App.Configuration.SettingsAtStart(
            new ClaudeDashboard.App.Configuration.SettingsLoadResult(
                new ClaudeDashboard.App.Configuration.DashboardSettings(),
                ClaudeDashboard.App.Configuration.SettingsLoadOutcome.Unreadable),
            BackupFile: @"C:\data\ClaudeDashboard\settings.error-20261003-140509.json");

        var hook = new ClaudeDashboard.App.Setup.HookNotice();
        hook.ShowClaudeCodeNotInstalled();
        var history = new ClaudeDashboard.App.Storage.HistoryNotice(() => true);
        var sound = new ClaudeDashboard.App.Adapters.SoundDeviceNotice(new SettableOutput { HasOutput = false });
        var settings = new ClaudeDashboard.App.Configuration.SettingsNotice(start, paths);

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var board = new NoticeBoard(hook, history, sound, settings);
            using var tray = TestTrays.For(registry.Projection, clock: clock, notices: board);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                tray.Tick(clock.Now);
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Empty(bindings.Problems);

                return (Lines: NoticeLines(window), tray.Tooltip);
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.Equal(
            [
                ClaudeDashboard.App.Setup.HookNotice.ClaudeCodeNotInstalledText,
                ClaudeDashboard.App.Storage.HistoryNotice.WindowText,
                ClaudeDashboard.App.Adapters.SoundDeviceNotice.WindowText,
                ClaudeDashboard.App.Configuration.SettingsNotice.KeptAsideText("settings.error-20261003-140509.json", paths.Root),
            ],
            seen.Lines);
        Assert.StartsWith(
            $"{ClaudeDashboard.App.Setup.HookNotice.NoClaudeCodeShort} · {ClaudeDashboard.App.Storage.HistoryNotice.TrayShort} · " +
            $"{ClaudeDashboard.App.Adapters.SoundDeviceNotice.TrayShort} · {ClaudeDashboard.App.Configuration.SettingsNotice.TrayShort}",
            seen.Tooltip,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The port notice is the first line of the notice row</strong> (T1.57, issue #14),
    /// before a plugin notice, and leads the tooltip once. <c>BindingErrorWatch</c> is clean.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_port_notice_leads_the_row_and_the_tooltip(bool pinned)
    {
        var clock = new FakeClock();
        var port = pinned
            ? ClaudeDashboard.App.Hosting.IngressStatus.PinnedPortTaken(52961)
            : ClaudeDashboard.App.Hosting.IngressStatus.Unavailable(52888, 52888, 52920, @"C:\data\ClaudeDashboard\settings.json");

        var hook = new ClaudeDashboard.App.Setup.HookNotice();
        hook.ShowPluginDisabled();

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var board = new NoticeBoard(port, hook);
            using var tray = TestTrays.For(registry.Projection, clock: clock, notices: board);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Empty(bindings.Problems);

                return (Lines: NoticeLines(window), tray.Tooltip);
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.Equal([port.Text!, ClaudeDashboard.App.Setup.HookNotice.PluginDisabledText], seen.Lines);
        Assert.Contains(pinned ? "52961" : "52888 to 52920", seen.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith($"{port.Fault} · {ClaudeDashboard.App.Setup.HookNotice.PluginDisabledShort}", seen.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The queue notices show after the others, fell behind before events lost</strong>
    /// (T1.58, issue #3), and lead the tooltip in that order after them. <c>BindingErrorWatch</c> is
    /// clean.
    /// </summary>
    [Fact]
    public void The_queue_notices_show_after_the_others()
    {
        var clock = new FakeClock();
        var hook = new ClaudeDashboard.App.Setup.HookNotice();
        hook.ShowPluginDisabled();
        var behind = new ClaudeDashboard.App.Pipeline.FellBehindNotice(() => clock.Now);
        var lost = new ClaudeDashboard.App.Pipeline.EventsLostNotice(() => 3);

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var board = new NoticeBoard(hook, behind, lost);
            using var tray = TestTrays.For(registry.Projection, clock: clock, notices: board);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                tray.Tick(clock.Now);
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Empty(bindings.Problems);

                return (Lines: NoticeLines(window), tray.Tooltip);
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.Equal(
            [
                ClaudeDashboard.App.Setup.HookNotice.PluginDisabledText,
                ClaudeDashboard.App.Pipeline.FellBehindNotice.WindowText,
                ClaudeDashboard.App.Pipeline.EventsLostNotice.WindowText,
            ],
            seen.Lines);
        Assert.StartsWith(
            $"{ClaudeDashboard.App.Setup.HookNotice.PluginDisabledShort} · {ClaudeDashboard.App.Pipeline.FellBehindNotice.TrayShort} · " +
            ClaudeDashboard.App.Pipeline.EventsLostNotice.TrayShort,
            seen.Tooltip,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The self-test and refused notices come directly after the plugin notice</strong>
    /// (T1.61, issue #74), before the later sources, and lead the tooltip in that order. "Last heard"
    /// is left out whole when the four would pass Windows' limit. <c>BindingErrorWatch</c> is clean.
    /// </summary>
    [Fact]
    public void The_path_notices_come_right_after_the_plugin_notice()
    {
        var clock = new FakeClock();
        var hook = new ClaudeDashboard.App.Setup.HookNotice();
        hook.ShowPluginDisabled();

        var health = new ClaudeDashboard.App.Ingress.HookHealth();
        var failed = new ClaudeDashboard.App.Ingress.SelfTestResult(
            false, null, clock.Now, ClaudeDashboard.App.Ingress.SelfTestCause.NothingArrived);
        health.Finished(failed);

        for (var i = 0; i < ClaudeDashboard.App.Ingress.HookHealth.RefusalsToShow; i++)
        {
            health.Refused(clock.Now);
        }

        var selfTest = new ClaudeDashboard.App.Ingress.SelfTestNotice(health);
        var refused = new ClaudeDashboard.App.Ingress.RefusedNotice(health);
        var behind = new ClaudeDashboard.App.Pipeline.FellBehindNotice(() => clock.Now);

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var board = new NoticeBoard(hook, selfTest, refused, behind);
            using var tray = new TrayViewModel(
                registry.Projection,
                new SettableSoundModes(),
                new RecordingEventSink(),
                clock,
                ClaudeDashboard.App.Hosting.IngressStatus.Healthy(DashboardSettings.IngressPortBase),
                Serilog.Core.Logger.None,
                notices: board,
                health: health);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                tray.Tick(clock.Now);
                _harness.Pump(DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Empty(bindings.Problems);

                return (Lines: NoticeLines(window), tray.Tooltip);
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.Equal(
            [
                ClaudeDashboard.App.Setup.HookNotice.PluginDisabledText,
                ClaudeDashboard.App.Ingress.SelfTestNotice.Describe(failed),
                ClaudeDashboard.App.Ingress.RefusedNotice.WindowText,
                ClaudeDashboard.App.Pipeline.FellBehindNotice.WindowText,
            ],
            seen.Lines);
        // With four notices, "last heard" would take the text past Windows' 127 characters, so it is
        // left out whole, and nothing else is cut.
        Assert.Equal(
            $"{ClaudeDashboard.App.Setup.HookNotice.PluginDisabledShort} · {ClaudeDashboard.App.Ingress.SelfTestNotice.TrayShort} · " +
            $"{ClaudeDashboard.App.Ingress.RefusedNotice.TrayShort} · {ClaudeDashboard.App.Pipeline.FellBehindNotice.TrayShort} · " +
            "all quiet",
            seen.Tooltip);
        Assert.True(
            seen.Tooltip.Length + " · not heard from Claude Code since start".Length > TrayTooltip.MaxLength,
            "The case is meant to have no room for last heard.");
    }

    /// <summary>
    /// <strong>Test connection</strong> (T1.61) runs the self-test and shows the result beside the
    /// button, in the notice's words. Here the scratch folder has no script, so the cause is that one.
    /// <c>BindingErrorWatch</c> is clean.
    /// </summary>
    [Fact]
    public void The_test_connection_button_shows_the_result_beside_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new DashboardPaths(root);

        var selfTest = new ClaudeDashboard.App.Setup.HookSelfTest(
            paths, new ClaudeDashboard.App.Ingress.HookHealth(), new FakeClock(), Serilog.Core.Logger.None);
        var startup = new ClaudeDashboard.App.Setup.StartWithWindows(new FakeStartupRegistry(), null, Serilog.Core.Logger.None);
        var viewModel = new SettingsViewModel(startup, new SettingsStore(paths), Serilog.Core.Logger.None, selfTest);

        var seen = _harness.Invoke(() =>
        {
            var window = new SettingsWindow(viewModel);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);

                var label = window.TestConnectionButton.Content;
                var shownBefore = window.TestResultText.IsVisible;
                var invoke = (IInvokeProvider)new ButtonAutomationPeer(window.TestConnectionButton).GetPattern(PatternInterface.Invoke);

                invoke.Invoke();

                // The test runs on a pool thread and the result comes back to this one.
                var waited = Stopwatch.StartNew();

                while (!window.TestConnectionButton.IsEnabled || viewModel.TestResult is null or "Testing…")
                {
                    Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "The test connection result never came back.");
                    _harness.Pump(DispatcherPriority.Background);
                }

                window.UpdateLayout();
                Assert.Empty(bindings.Problems);

                return (label, shownBefore, Result: window.TestResultText.Text, ShownAfter: window.TestResultText.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(SettingsViewModel.TestConnectionLabel, seen.label);
        Assert.False(seen.shownBefore);
        Assert.True(seen.ShownAfter);
        Assert.Equal(
            $"{ClaudeDashboard.App.Ingress.SelfTestNotice.WindowLead} The script that forwards them is missing.",
            seen.Result);
    }

    /// <summary>An output state the test sets, in place of a player.</summary>
    private sealed class SettableOutput : ClaudeDashboard.App.Adapters.ISoundOutput
    {
        public bool HasOutput { get; set; }
    }

    /// <summary>
    /// The header's Mute all is the tray's switch (T1.47, the ruling of 2026-09-29): it publishes
    /// the tray's command, and its label follows the one muted state the tray menu reads.
    /// </summary>
    /// <remarks>
    /// "Muting from either place shows on both" is asserted as one source: the header's label is
    /// read beside the tray's own <see cref="TrayViewModel.MuteAllLabel"/> after each change of the
    /// mode, and the two must be equal each time — muted by the header, then unmuted as if from the
    /// tray. The mode is set on the fake reader the way the engine sets it when a command lands.
    /// </remarks>
    [Fact]
    public void The_header_mute_all_is_the_trays_switch_and_reads_its_state()
    {
        var sink = new RecordingEventSink();
        var modes = new SettableSoundModes();
        var clock = new FakeClock();

        var seen = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            using var viewModel = new MainViewModel(
                registry.Projection, policy, new StubAckPublisher(),
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            using var tray = TestTrays.For(registry.Projection, modes, sink, clock);

            var window = new MainWindow(viewModel, tray);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);

                var header = window.MuteAllButton;
                var labels = new List<(string Header, string Tray)> { ((string)header.Content, tray.MuteAllLabel) };
                var enabled = header.IsEnabled;

                ((IInvokeProvider)new ButtonAutomationPeer(header).GetPattern(PatternInterface.Invoke)).Invoke();
                _harness.Pump(DispatcherPriority.Background);

                // The command landed: the engine would now report the mute.
                modes.AllMutedUntil = DateTimeOffset.MaxValue;
                tray.Tick(clock.Now);
                _harness.Pump(DispatcherPriority.Background);
                labels.Add(((string)header.Content, tray.MuteAllLabel));

                // Unmuted from the tray's menu: the same command, and the header follows.
                tray.MuteAllCommand.Execute(null);
                modes.AllMutedUntil = null;
                tray.Tick(clock.Now);
                _harness.Pump(DispatcherPriority.Background);
                labels.Add(((string)header.Content, tray.MuteAllLabel));

                Assert.Empty(bindings.Problems);

                return (labels, enabled, SameCommand: ReferenceEquals(header.Command, tray.MuteAllCommand), Tooltip: header.ToolTip as string);
            }
            finally
            {
                window.Hide();
            }
        });

        Assert.True(seen.enabled, "the header's Mute all must be a working control");
        Assert.True(seen.SameCommand, "the header must publish the tray's own command");
        Assert.Equal(
            [("Mute all", "Mute all"), ("Unmute all", "Unmute all"), ("Mute all", "Mute all")],
            seen.labels);
        Assert.DoesNotContain("T1.13", seen.Tooltip ?? string.Empty, StringComparison.Ordinal);

        Assert.Equal(
            [SoundCommandKind.MuteAll, SoundCommandKind.UnmuteAll],
            sink.Published.OfType<SoundCommand>().Select(command => command.Kind));
    }

    /// <summary>
    /// …and a row with nothing to acknowledge has no button at all, so "enabled" above means the
    /// command answered rather than that every button is always live.
    /// </summary>
    /// <remarks>
    /// This used to assert a second negative — a button present but inert — by building the window
    /// over a view model with no publisher. That construction no longer exists:
    /// <see cref="MainViewModel"/> requires one, precisely so a shipped window can never be given
    /// nowhere to send an ack. The visible-but-disabled affordance is still real and still
    /// asserted, one level down where a row genuinely can be built standalone — see <c>AckTests</c>.
    /// </remarks>
    [Fact]
    public void The_ack_button_is_absent_where_there_is_nothing_to_acknowledge()
    {
        var states = WithWindow(
            registry =>
            {
                var promptId = registry.Working("finished", At);
                registry.Finished("finished", At.AddMinutes(1), promptId);
                registry.Working("busy", At);
            },
            (window, _) => new Dictionary<string, bool?>(StringComparer.Ordinal)
            {
                ["finished"] = AckButton(window, "finished")?.IsEnabled,
                ["busy"] = AckButton(window, "busy")?.IsEnabled,
            });

        // Nothing to acknowledge on a working row, so there is no button there to enable.
        Assert.Null(states["busy"]);

        // And the finished row has one, live: the window the container builds always has somewhere
        // to send an ack.
        Assert.True(states["finished"]);
    }

    private static Button? AckButton(MainWindow window, string sessionId) =>
        StaHarness.FindAll<Button>(RowFor(window, sessionId))
            .FirstOrDefault(button =>
                button.Content as string == "✓ Ack" && button.Visibility == Visibility.Visible);

    /// <summary>The ack affordance §9 puts on rows that have something to acknowledge, and only those.</summary>
    [Fact]
    public void The_ack_affordance_is_on_the_rows_that_can_be_acknowledged()
    {
        var acks = WithWindow(
            registry =>
            {
                var promptId = registry.Working("finished", At);
                registry.Finished("finished", At.AddMinutes(1), promptId);
                registry.Working("busy", At);
            },
            (window, _) => new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["finished"] = HasVisibleAck(RowFor(window, "finished")),
                ["busy"] = HasVisibleAck(RowFor(window, "busy")),
            });

        Assert.True(acks["finished"]);
        Assert.False(acks["busy"]);
    }

    // ---- The window's own behaviour ----------------------------------------------------------------

    /// <summary>Impl §5.1: closing hides the window; the process exits only via the tray's Quit.</summary>
    [Fact]
    public void Closing_the_window_hides_it_rather_than_closing_it()
    {
        var state = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var viewModel = new MainViewModel(
                registry.Projection,
                new MotionPolicy(() => false, observeChanges: false),
                new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            var window = new MainWindow(viewModel, TestTrays.For(registry.Projection));

            window.Close();
            var afterClose = window.IsVisible;

            // Still a live window: it can be shown again, which a closed one cannot.
            window.ShowDashboard();
            var afterShow = window.IsVisible;
            window.Hide();

            return (AfterClose: afterClose, AfterShow: afterShow);
        });

        Assert.False(state.AfterClose);
        Assert.True(state.AfterShow);
    }

    /// <summary>
    /// Impl §5.2: a left-click on the tray toggles the dashboard rather than only showing it.
    /// </summary>
    /// <remarks>
    /// Asserted as a round trip — hidden, shown, hidden again — because "toggles" is exactly the
    /// property a <c>Show()</c> would satisfy on the first click and fail on the second. A
    /// one-click test would pass against a tray that could open the window and never close it.
    /// </remarks>
    [Fact]
    public void A_left_click_toggles_the_dashboard()
    {
        var state = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var viewModel = new MainViewModel(
                registry.Projection,
                new MotionPolicy(() => false, observeChanges: false),
                new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            var window = new MainWindow(viewModel, TestTrays.For(registry.Projection));
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;

            var atStart = window.IsVisible;

            window.ToggleDashboard();
            var afterFirst = window.IsVisible;

            window.ToggleDashboard();
            var afterSecond = window.IsVisible;

            window.Hide();

            return (AtStart: atStart, AfterFirst: afterFirst, AfterSecond: afterSecond);
        });

        Assert.False(state.AtStart);
        Assert.True(state.AfterFirst);
        Assert.False(state.AfterSecond);
    }

    /// <summary>
    /// A minimised window counts as hidden, so the click that was meant to reveal it does.
    /// </summary>
    /// <remarks>
    /// Without this, an operator who minimised the dashboard and then clicked the tray to get it
    /// back would minimise it again — the window is technically visible, so a naive toggle hides
    /// it. Restoring is what the click meant.
    /// </remarks>
    [Fact]
    public void Toggling_a_minimised_window_restores_it()
    {
        var state = _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var viewModel = new MainViewModel(
                registry.Projection,
                new MotionPolicy(() => false, observeChanges: false),
                new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
            var window = new MainWindow(viewModel, TestTrays.For(registry.Projection));
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;

            window.ShowDashboard();
            window.WindowState = WindowState.Minimized;

            window.ToggleDashboard();
            var result = (window.IsVisible, window.WindowState);

            window.Hide();

            return result;
        });

        Assert.True(state.IsVisible);
        Assert.Equal(WindowState.Normal, state.WindowState);
    }

    // ---- The templates, as markup ------------------------------------------------------------------

    /// <summary>
    /// There are exactly two animations in the dashboard's own templates, and each is reached
    /// only through a data trigger on <see cref="SessionViewModel.Motion"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rendering tests above say what moves today. This says what <em>can</em> move: a third
    /// storyboard, or one attached to a mouse-over or a state, fails here the moment it is
    /// written — before anyone has to notice the window twitching.
    /// </para>
    /// <para>
    /// Read from the markup rather than walked as objects, because a style declared inline inside
    /// a <c>DataTemplate</c> is not reachable from the template object at all, and that is
    /// exactly where an animation would be easiest to add unnoticed.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_only_animations_in_the_templates_are_the_two_motion_ones()
    {
        var markup = File.ReadAllText(TemplatesFile);

        Assert.Equal(2, Occurrences(markup, "<Storyboard "));
        Assert.Equal(2, Occurrences(markup, "<BeginStoryboard "));
        Assert.Equal(2, Occurrences(markup, "<StopStoryboard "));

        // Every trigger that starts one is a DataTrigger on Motion.
        Assert.Equal(2, Occurrences(markup, "<DataTrigger Binding=\"{Binding Motion}\""));
        Assert.Contains("<DataTrigger Binding=\"{Binding Motion}\" Value=\"Blink\">", markup, StringComparison.Ordinal);
        Assert.Contains("<DataTrigger Binding=\"{Binding Motion}\" Value=\"Breathe\">", markup, StringComparison.Ordinal);

        // And nothing animates by any other route.
        Assert.Equal(0, Occurrences(markup, "<EventTrigger"));
        Assert.Equal(0, Occurrences(markup, "ColorAnimation"));
        Assert.Equal(0, Occurrences(markup, "ThicknessAnimation"));
        Assert.Equal(0, Occurrences(markup, "VisualTransition"));
    }

    /// <summary>
    /// Every other piece of markup in the application animates nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>App.xaml</c> is scanned as well as the window's own markup, and that is not padding: an
    /// implicit style there applies to every element of its type in the process, so an animation
    /// declared in it would move things the row templates never mention. The reviewer proved the
    /// gap by putting a forever-repeating opacity animation on an implicit <c>TextBlock</c> style
    /// in <c>App.xaml</c> — <see cref="The_only_animations_in_the_templates_are_the_two_motion_ones"/>
    /// did not see it, because it does not read that file.
    /// </para>
    /// <para>
    /// The rendering tests remain the other half. Something that animates only under a condition
    /// no test exercises — a hover, a drag — would satisfy them and be caught only here.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("ActivityWindow.xaml")]
    [InlineData("../App.xaml")]
    public void No_other_markup_in_the_application_animates(string file)
    {
        var markup = File.ReadAllText(Path.Combine(UiFolder, file));

        Assert.Equal(0, Occurrences(markup, "Storyboard"));
        Assert.Equal(0, Occurrences(markup, "Animation"));
    }

    // ---- The speaker sign (T1.67, issue #99) ----------------------------------------------------

    /// <summary>
    /// <strong>The sign is in the row, between the badge and the age, and it does not move</strong>,
    /// with animations on or off (T1.67, issue #99). The row without a sound has none. The hover and
    /// the screen-reader name are as the block says, and <c>BindingErrorWatch</c> is clean.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_sound_sign_sits_between_the_badge_and_the_age_and_never_moves(bool motionAllowed)
    {
        var seen = WithWindow(
            registry =>
            {
                registry.Working("blocked", At);
                registry.Blocked("blocked", At.AddMinutes(1));
                registry.Working("busy", At);
            },
            (window, _) =>
            {
                var row = RowFor(window, "blocked");
                var line = StaHarness.FindAll<MetaLine>(row).Single();
                var children = line.Children.Cast<FrameworkElement>().ToList();
                var sign = (Image)children.Single(child => child.Name == "SoundSign");
                var badge = children.FindIndex(child => child is Border border && StaHarness.FindAll<TextBlock>(border).Any(block => TextOf(block) == "PERMISSION"));
                var age = children.FindIndex(child => child is TextBlock block && TextOf(block) == ((SessionViewModel)row.DataContext).AgeText);
                var peer = UIElementAutomationPeer.CreatePeerForElement(sign);
                var quietSign = StaHarness.FindAll<Image>(RowFor(window, "busy")).Single(image => image.Name == "SoundSign");

                return (
                    Badge: badge,
                    Sign: children.IndexOf(sign),
                    Age: age,
                    sign.IsVisible,
                    sign.ActualWidth,
                    Name: peer.GetName(),
                    Tip: sign.ToolTip as string,
                    Moving: sign.HasAnimatedProperties,
                    QuietVisible: quietSign.IsVisible);
            },
            motionAllowed: motionAllowed,
            prepare: viewModel => viewModel.SoundPlayed(new SessionId("blocked"), ClaudeDashboard.Core.Ports.SoundId.Permission, At.AddMinutes(1)));

        Assert.True(seen.Badge >= 0 && seen.Age >= 0, $"Badge at {seen.Badge}, age at {seen.Age}.");
        Assert.True(seen.Badge < seen.Sign && seen.Sign < seen.Age, $"Badge {seen.Badge}, sign {seen.Sign}, age {seen.Age}.");
        Assert.True(seen.IsVisible);
        Assert.True(seen.ActualWidth > 0);
        Assert.Equal("sound played", seen.Name);
        Assert.Equal("played: permission, 0s ago", seen.Tip);
        Assert.False(seen.Moving);
        Assert.False(seen.QuietVisible);
    }

    /// <summary>
    /// No storyboard, trigger or setter targets the sign by name, and the template still has only
    /// the two motion storyboards (<see cref="The_only_animations_in_the_templates_are_the_two_motion_ones"/>).
    /// </summary>
    [Fact]
    public void Nothing_in_the_templates_targets_the_sound_sign()
    {
        var markup = File.ReadAllText(TemplatesFile);

        Assert.Equal(1, Occurrences(markup, "x:Name=\"SoundSign\""));
        Assert.Equal(0, Occurrences(markup, "TargetName=\"SoundSign\""));
    }

    /// <summary>
    /// <strong>A narrow row: the sign is the first thing to go, and the age still shows.</strong> The
    /// window is swept from its minimum width up. Wherever the whole meta line fits, the sign shows.
    /// Wherever it does not, the sign takes no room, and the age keeps its place, whole, inside the row.
    /// </summary>
    [Fact]
    public void A_narrow_row_loses_the_sign_before_the_age()
    {
        const string LongWorkspace = @"C:\dev\a-workspace-folder-with-a-rather-long-name-for-the-narrow-row-test";

        var seen = WithWindow(
            registry =>
            {
                registry.Working("blocked", At, cwd: LongWorkspace);
                registry.Blocked("blocked", At.AddMinutes(1), cwd: LongWorkspace);
            },
            (window, _) =>
            {
                // The sign's layout slot, not its RenderSize: a child arranged in an empty slot keeps the size
                // it wants and is clipped to nothing, as FittingStrip's dropped counts are.
                var results = new List<(double Width, bool GaveWay, double SignWidth, bool AgeWhole, bool LineFits)>();

                foreach (var width in Enumerable.Range(0, 81).Select(step => window.MinWidth + (step * 10)))
                {
                    window.Width = width;
                    window.UpdateLayout();
                    _harness.Pump(DispatcherPriority.Background);
                    window.UpdateLayout();

                    var row = RowFor(window, "blocked");
                    var line = StaHarness.FindAll<MetaLine>(row).Single();
                    var room = ((FrameworkElement)VisualTreeHelper.GetParent(line)).ActualWidth;
                    var sign = (Image)line.Children.Cast<FrameworkElement>().Single(child => child.Name == "SoundSign");
                    var age = line.Children.OfType<TextBlock>().Single(block => TextOf(block) == ((SessionViewModel)row.DataContext).AgeText);
                    var ageRight = age.TranslatePoint(new Point(age.ActualWidth, 0), line).X;
                    var wanted = line.Children.Cast<UIElement>().Sum(child => child.DesiredSize.Width);

                    results.Add((width, line.GaveWay, LayoutInformation.GetLayoutSlot(sign).Width, ageRight <= room + 0.01, wanted <= room + 0.01));
                }

                return results;
            },
            grouped: false,
            prepare: viewModel => viewModel.SoundPlayed(new SessionId("blocked"), ClaudeDashboard.Core.Ports.SoundId.Permission, At.AddMinutes(1)));

        Assert.Contains(seen, at => at.GaveWay);
        Assert.Contains(seen, at => !at.GaveWay);

        foreach (var at in seen)
        {
            if (at.GaveWay)
            {
                Assert.True(at.SignWidth == 0, $"At {at.Width} the line gave way and the sign was still {at.SignWidth} wide.");
                Assert.True(at.AgeWhole, $"At {at.Width} the sign gave way and the age was still cut.");
            }
            else
            {
                Assert.True(at.SignWidth > 0, $"At {at.Width} the line fit and the sign was not drawn.");
                Assert.True(at.LineFits, $"At {at.Width} the sign was drawn on a line that did not fit.");
            }
        }
    }

    private static string UiFolder =>
        Path.Combine(RepoLayout.Project(RepoLayout.App).Directory!.FullName, "Ui");

    private static string TemplatesFile => Path.Combine(UiFolder, "RowTemplates.xaml");

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static bool HasVisibleAck(DependencyObject row) =>
        StaHarness.FindAll<Button>(row).Any(button =>
            button.Content as string == "✓ Ack" && button.Visibility == Visibility.Visible);

    /// <summary>Drives a session to <paramref name="state"/> through the real pipeline.</summary>
    // ---- The orchestration's one Ack, as it actually renders (issue #47) -----------------------

    private static readonly string[] AlphaOnly = ["alpha"];

    /// <summary>
    /// <strong>A roster member row renders no Ack — collapsed and expanded — while its header
    /// renders the one; a cwd row in the same window keeps its own.</strong>
    /// </summary>
    [Fact]
    public void A_roster_member_renders_no_ack_and_the_header_renders_one()
    {
        var rosters = new RosterStore(
            new RecordingEventSink(),
            RosterBook.From([("orchestration", AlphaOnly)]));

        WithWindow(
            registry =>
            {
                registry.Finished("member", At.AddMinutes(1), registry.Working("member", At, title: "alpha"), title: "alpha");
                registry.Finished("loose", At.AddMinutes(1), registry.Working("loose", At, title: "beta"), title: "beta");
            },
            (window, viewModel) =>
            {
                var member = RowFor(window, "member");
                var loose = RowFor(window, "loose");

                Assert.Equal(
                    Visibility.Collapsed,
                    StaHarness.Find<Button>(member, b => Equals(b.Content, "✓ Ack"))!.Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    StaHarness.Find<Button>(member, b => Equals(b.Content, "✓ Acknowledge"))!.Visibility);

                // The cwd twin: same window, same state, its own Ack — on the row and in the
                // expanded exchange both.
                Assert.Equal(
                    Visibility.Visible,
                    StaHarness.Find<Button>(loose, b => Equals(b.Content, "✓ Ack"))!.Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    StaHarness.Find<Button>(loose, b => Equals(b.Content, "✓ Acknowledge"))!.Visibility);

                // The header carries the orchestration's one Ack; the cwd header carries none.
                var rosterHeader = RowsOf(window).Single(row =>
                    row.DataContext is GroupViewModel { Kind: GroupKeyKind.Roster });
                var cwdHeader = RowsOf(window).Single(row =>
                    row.DataContext is GroupViewModel group && group.Kind != GroupKeyKind.Roster);

                Assert.Equal(
                    Visibility.Visible,
                    StaHarness.Find<Button>(rosterHeader, b => Equals(b.Content, "✓ Ack"))!.Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    StaHarness.Find<Button>(cwdHeader, b => Equals(b.Content, "✓ Ack"))!.Visibility);

                return true;
            },
            prepare: viewModel =>
            {
                foreach (var row in viewModel.Rows.OfType<SessionViewModel>())
                {
                    row.IsExpanded = true;
                }
            },
            rosters: rosters);
    }

    /// <summary>The same session in Flat view renders its own Ack — the roster does not follow it.</summary>
    [Fact]
    public void The_same_session_in_flat_renders_its_own_ack()
    {
        var rosters = new RosterStore(
            new RecordingEventSink(),
            RosterBook.From([("orchestration", AlphaOnly)]));

        WithWindow(
            registry => registry.Finished("member", At.AddMinutes(1), registry.Working("member", At, title: "alpha"), title: "alpha"),
            (window, _) =>
            {
                Assert.Equal(
                    Visibility.Visible,
                    StaHarness.Find<Button>(RowFor(window, "member"), b => Equals(b.Content, "✓ Ack"))!.Visibility);

                return true;
            },
            grouped: false,
            rosters: rosters);
    }

    // ---- Selection, as it actually renders (issue #44) -----------------------------------------

    private static ToggleButton ToggleOf(MainWindow window, string sessionId) =>
        StaHarness.Find<ToggleButton>(RowFor(window, sessionId), toggle => toggle.Name == "RowToggle")!;

    private static Border SurfaceOf(MainWindow window, string sessionId) =>
        StaHarness.Find<Border>(RowFor(window, sessionId), border => border.Name == "SelectionSurface")!;

    private static SessionViewModel VmOf(MainWindow window, string sessionId) =>
        (SessionViewModel)RowFor(window, sessionId).DataContext;

    /// <summary>
    /// <strong>Issue #44's bug, from the failing side: two chosen rows look chosen at once,
    /// whichever has focus.</strong>
    /// </summary>
    /// <remarks>
    /// The old shade was <c>RowToggleStyle</c>'s <c>IsKeyboardFocused</c> trigger impersonating
    /// selection, so clicking a second row stripped the first row's only visual while its state
    /// stayed true. Focus is moved to a THIRD row here — the arrangement where the old display
    /// showed zero selections while the header counted two.
    /// </remarks>
    [Fact]
    public void Two_selected_rows_keep_their_marks_when_focus_moves()
    {
        WithWindow(
            registry =>
            {
                registry.Working("s-1", At, title: "one");
                registry.Working("s-2", At, title: "two");
                registry.Working("s-3", At, title: "three");
            },
            (window, viewModel) =>
            {
                VmOf(window, "s-1").IsSelected = true;
                VmOf(window, "s-2").IsSelected = true;

                var third = ToggleOf(window, "s-3");
                third.Focus();

                Assert.True(third.IsKeyboardFocused, "The harness could not move keyboard focus at all.");

                Assert.Same(window.FindResource("SelectedRowBrush"), SurfaceOf(window, "s-1").Background);
                Assert.Same(window.FindResource("SelectedRowBrush"), SurfaceOf(window, "s-2").Background);

                return true;
            },
            prepare: viewModel => viewModel.IsSelecting = true);
    }

    /// <summary>
    /// <strong>Focused, selected, and both are three distinct brush instances on one row.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinguishability is the requirement, and instances are how it is asserted without
    /// restating colours: the focus shade is <c>RowToggleStyle</c>'s <c>RaisedBrush</c> on its
    /// template's Surround, the selection shade is <c>SelectedRowBrush</c> on the row's own
    /// surface, and both together swap in <c>SelectedFocusedRowBrush</c> — so focus arriving on
    /// a selected row changes the shade rather than erasing it.
    /// </para>
    /// <para>
    /// "On one row" means one row paints TWO surfaces: focus lives in the shared
    /// <c>RowToggleStyle</c> and colours its Surround, selection lives in the session template
    /// and colours its SelectionSurface — the placement issue #44's fix forced, since the shared
    /// style serves headers with no <c>IsSelected</c> to bind. The three brushes are therefore
    /// read off two elements, and the NotSame assertions compare across them on purpose.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_three_selection_looks_are_three_distinct_brushes()
    {
        WithWindow(
            registry => registry.Working("s-1", At, title: "one"),
            (window, viewModel) =>
            {
                var toggle = ToggleOf(window, "s-1");
                var surface = SurfaceOf(window, "s-1");
                var surround = StaHarness.Find<Border>(toggle, border => border.Name == "Surround")!;

                toggle.Focus();
                Assert.True(toggle.IsKeyboardFocused, "The harness could not move keyboard focus at all.");

                var focused = surround.Background;
                Assert.Same(window.FindResource("RaisedBrush"), focused);

                VmOf(window, "s-1").IsSelected = true;
                var both = surface.Background;
                Assert.Same(window.FindResource("SelectedFocusedRowBrush"), both);

                Keyboard.ClearFocus();
                var selected = surface.Background;
                Assert.Same(window.FindResource("SelectedRowBrush"), selected);

                Assert.NotSame(focused, selected);
                Assert.NotSame(focused, both);
                Assert.NotSame(selected, both);

                return true;
            },
            prepare: viewModel => viewModel.IsSelecting = true);
    }

    /// <summary>The check takes the LED's slot while selected, and gives it back.</summary>
    [Fact]
    public void The_check_takes_the_led_slot_while_selected()
    {
        WithWindow(
            registry => registry.Working("s-1", At, title: "one"),
            (window, viewModel) =>
            {
                var row = RowFor(window, "s-1");
                var led = StaHarness.Find<Ellipse>(row)!;
                var check = StaHarness.Find<TextBlock>(row, text => text.Name == "SelectedCheck")!;

                Assert.Equal(Visibility.Visible, led.Visibility);
                Assert.Equal(Visibility.Collapsed, check.Visibility);

                VmOf(window, "s-1").IsSelected = true;

                Assert.Equal(Visibility.Collapsed, led.Visibility);
                Assert.Equal(Visibility.Visible, check.Visibility);
                Assert.Same(window.FindResource("BlueBrush"), check.Foreground);

                return true;
            },
            prepare: viewModel => viewModel.IsSelecting = true);
    }

    /// <summary>
    /// The mark rides the view model, so it survives what a row survives — being expanded, a
    /// state change arriving, an explicit rebuild.
    /// </summary>
    /// <remarks>
    /// The row is expanded BEFORE the mode is entered, and that is not a convenience: in
    /// selection mode the expansion gesture IS selection (T1.26 rule 1 — one gesture, one
    /// meaning), so <c>IsExpanded</c> cannot move while selecting, and the first draft of this
    /// test proved it by toggling its own selection off. What "survives expansion" can honestly
    /// mean is that an expanded row carries the mark like any other.
    /// </remarks>
    [Fact]
    public void The_mark_survives_an_expanded_row_a_state_change_and_a_refresh()
    {
        RegistryHarness? live = null;

        WithWindow(
            registry =>
            {
                live = registry;
                registry.Working("s-1", At, title: "one");
            },
            (window, viewModel) =>
            {
                VmOf(window, "s-1").IsSelected = true;

                Assert.True(VmOf(window, "s-1").IsExpanded);
                Assert.Same(window.FindResource("SelectedRowBrush"), SurfaceOf(window, "s-1").Background);

                // A state change arrives through the pipeline; the row is reused and keeps the mark.
                live!.Batch("s-1", At.AddSeconds(5), title: "one");
                Assert.True(VmOf(window, "s-1").IsSelected);
                Assert.Same(window.FindResource("SelectedRowBrush"), SurfaceOf(window, "s-1").Background);

                // An explicit rebuild reuses the row where it can (MainViewModel.Refresh), so the
                // mark rides the view model rather than a visual that gets torn down.
                viewModel.Refresh();
                Assert.Same(window.FindResource("SelectedRowBrush"), SurfaceOf(window, "s-1").Background);

                return true;
            },
            prepare: viewModel =>
            {
                // Expanded first, then the mode: the order is the design's, not the test's.
                foreach (var row in viewModel.Rows.OfType<SessionViewModel>())
                {
                    row.IsExpanded = true;
                }

                viewModel.IsSelecting = true;
            });
    }

    /// <summary>
    /// An untitled row in selection mode is dimmed and says why; a titled one is not; outside
    /// the mode neither is.
    /// </summary>
    [Fact]
    public void An_untitled_row_dims_and_says_why_in_selection_mode()
    {
        WithWindow(
            registry =>
            {
                registry.Working("named", At, title: "one");
                registry.Working("nameless", At);
            },
            (window, viewModel) =>
            {
                var nameless = SurfaceOf(window, "nameless");
                var named = SurfaceOf(window, "named");

                Assert.Equal(0.55, nameless.Opacity);
                Assert.Equal(
                    "This session has no title, so it cannot be picked. Name it with --name or /rename.",
                    ToggleOf(window, "nameless").ToolTip);

                Assert.Equal(1.0, named.Opacity);
                Assert.Null(ToggleOf(window, "named").ToolTip);

                viewModel.IsSelecting = false;

                Assert.Equal(1.0, nameless.Opacity);
                Assert.Null(ToggleOf(window, "nameless").ToolTip);

                return true;
            },
            prepare: viewModel => viewModel.IsSelecting = true);
    }

    /// <summary>
    /// The mark leaves with the mode: <c>IsSelecting=false</c> clears the selection and the row
    /// goes back to transparent.
    /// </summary>
    [Fact]
    public void The_mark_leaves_with_the_mode()
    {
        WithWindow(
            registry => registry.Working("s-1", At, title: "one"),
            (window, viewModel) =>
            {
                VmOf(window, "s-1").IsSelected = true;
                Assert.Same(window.FindResource("SelectedRowBrush"), SurfaceOf(window, "s-1").Background);

                viewModel.IsSelecting = false;

                Assert.False(VmOf(window, "s-1").IsSelected);
                Assert.Equal(System.Windows.Media.Brushes.Transparent, SurfaceOf(window, "s-1").Background);

                return true;
            },
            prepare: viewModel => viewModel.IsSelecting = true);
    }

    /// <summary>
    /// <strong>Group these lights at two, exactly as Ack all lights when something waits
    /// (issue #45)</strong> — the same shared style, asserted against the same brushes.
    /// </summary>
    [Fact]
    public void Group_these_lights_at_two_and_rests_below()
    {
        WithWindow(
            registry =>
            {
                registry.Working("s-1", At, title: "one");
                registry.Working("s-2", At, title: "two");
                registry.Working("s-3", At, title: "three");
            },
            (window, viewModel) =>
            {
                var button = StaHarness.Find<Button>(window, b => b.Name == "GroupTheseButton")!;
                var chip = StaHarness.Find<Border>(button, border => border.Name == "Chip")!;
                var header = (Style)window.FindResource("HeaderButtonStyle");

                Assert.False(button.IsEnabled);
                Assert.Equal(StyleValue(header, Control.BackgroundProperty), chip.Background);
                Assert.Equal(1.0, chip.Opacity);

                VmOf(window, "s-1").IsSelected = true;
                Assert.False(button.IsEnabled);

                VmOf(window, "s-2").IsSelected = true;
                Assert.True(button.IsEnabled);
                Assert.Same(window.FindResource("RaisedBrush"), chip.Background);
                Assert.Same(window.FindResource("InkBrush"), button.Foreground);

                // "Two or more", not "exactly two": a third keeps it lit.
                VmOf(window, "s-3").IsSelected = true;
                Assert.True(button.IsEnabled);
                Assert.Same(window.FindResource("RaisedBrush"), chip.Background);

                VmOf(window, "s-3").IsSelected = false;
                VmOf(window, "s-2").IsSelected = false;
                Assert.False(button.IsEnabled);
                Assert.Equal(StyleValue(header, Control.BackgroundProperty), chip.Background);
                Assert.Equal(1.0, chip.Opacity);

                // Leaving the mode resets everything: the exit clears the selection, the count
                // goes to zero, and the button rests — pinned rather than read off the plumbing.
                VmOf(window, "s-1").IsSelected = true;
                VmOf(window, "s-2").IsSelected = true;
                Assert.True(button.IsEnabled);

                viewModel.IsSelecting = false;

                Assert.False(button.IsEnabled);
                Assert.Equal(StyleValue(header, Control.BackgroundProperty), chip.Background);

                return true;
            },
            prepare: viewModel => viewModel.IsSelecting = true);
    }

    // ---- Ack all, as it actually renders (issue #43) -------------------------------------------

    /// <summary>
    /// <strong>Lit is the checked Grouped segment's look, and lit is enabled.</strong> Asserted
    /// against the brushes the styles themselves resolve from the resource dictionary — a
    /// literal here would be a second copy of the palette, free to stay green while the theme
    /// moved.
    /// </summary>
    [Fact]
    public void The_ack_all_button_lights_when_something_waits()
    {
        WithWindow(
            registry => Reach(registry, "s-1", SessionState.Unread),
            (window, viewModel) =>
            {
                var button = StaHarness.Find<Button>(window, b => b.Name == "AckAllButton");

                Assert.NotNull(button);
                Assert.True(viewModel.AnythingToAcknowledge);
                Assert.True(button.IsEnabled);

                var chip = StaHarness.Find<Border>(button, b => b.Name == "Chip");

                Assert.NotNull(chip);
                Assert.Same(window.FindResource("RaisedBrush"), chip.Background);
                Assert.Same(window.FindResource("InkBrush"), button.Foreground);

                // Rightmost: column 5 since the Activity button joined the toolbar (T1.70).
                Assert.Equal(5, Grid.GetColumn(button));
                Assert.Equal(5, ((Grid)button.Parent).ColumnDefinitions.Count - 1);
                Assert.Equal("Acknowledge every session that is waiting on you.", button.ToolTip);

                // Selection mode must not hide the one action that clears the board.
                viewModel.IsSelecting = true;
                Assert.True(button.IsVisible);

                return true;
            });
    }

    /// <summary>
    /// <strong>Unlit is the plain header chip — Select's look — and NOT the dimmed disabled
    /// look.</strong> The quiet board is the dashboard's ordinary state, and the base style's
    /// 0.75 opacity would draw the ordinary state as a broken control.
    /// </summary>
    [Fact]
    public void The_ack_all_button_rests_at_the_plain_header_look()
    {
        WithWindow(
            registry => Reach(registry, "s-1", SessionState.Working),
            (window, viewModel) =>
            {
                var button = StaHarness.Find<Button>(window, b => b.Name == "AckAllButton");

                Assert.NotNull(button);
                Assert.False(viewModel.AnythingToAcknowledge);
                Assert.False(button.IsEnabled);

                var header = (Style)window.FindResource("HeaderButtonStyle");
                var chip = StaHarness.Find<Border>(button, b => b.Name == "Chip");

                Assert.NotNull(chip);
                Assert.Equal(StyleValue(header, Control.BackgroundProperty), chip.Background);
                Assert.Same(StyleValue(header, Control.ForegroundProperty), button.Foreground);

                // The whole reason the style restates its template: disabled must not dim.
                Assert.Equal(1.0, chip.Opacity);

                return true;
            });
    }

    /// <summary>What <paramref name="style"/> itself sets <paramref name="property"/> to.</summary>
    /// <remarks>
    /// Read from the style's own setters so the expected value moves with the style — the same
    /// discipline as resolving a brush from the dictionary, applied to a style whose look is the
    /// contract.
    /// </remarks>
    private static object? StyleValue(Style style, DependencyProperty property) =>
        style.Setters.OfType<Setter>().Single(setter => setter.Property == property).Value;

    private static void Reach(RegistryHarness registry, string id, SessionState state)
    {
        switch (state)
        {
            case SessionState.Acked:
                registry.Quiet(id, At);
                break;

            case SessionState.Ended:
                registry.Started(id, At);
                registry.Ended(id, At.AddMinutes(1));
                break;

            case SessionState.NeedsPermission:
                registry.Working(id, At);
                registry.Blocked(id, At.AddMinutes(1), "permission_prompt");
                break;

            case SessionState.NeedsQuestion:
                registry.Working(id, At);
                registry.Blocked(id, At.AddMinutes(1), "agent_needs_input");
                break;

            case SessionState.Error:
                registry.Failed(id, At.AddMinutes(1), registry.Working(id, At));
                break;

            case SessionState.Unread:
                registry.Finished(id, At.AddMinutes(1), registry.Working(id, At));
                break;

            case SessionState.Working:
                registry.Working(id, At);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "No pipeline path to this state.");
        }
    }

    // ---- The Settings window (issue #36) ------------------------------------------------------------

    private const string SettingsExe = @"C:\Users\someone\AppData\Local\dsopko.ClaudeDashboard\current\ClaudeDashboard.App.exe";

    /// <summary>A Settings view model over a fake registry and a settings file in a temp folder.</summary>
    private static (SettingsViewModel ViewModel, FakeStartupRegistry Registry, ClaudeDashboard.App.Configuration.SettingsStore Store) SettingsOver(
        string? exe, Action<FakeStartupRegistry>? arrange = null)
    {
        var registry = new FakeStartupRegistry();
        arrange?.Invoke(registry);

        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ClaudeDashboard.App.Configuration.SettingsStore(new ClaudeDashboard.App.Configuration.DashboardPaths(root));

        var startup = new ClaudeDashboard.App.Setup.StartWithWindows(registry, exe, Serilog.Core.Logger.None);

        return (new SettingsViewModel(startup, store, Serilog.Core.Logger.None), registry, store);
    }

    /// <summary>
    /// The checkbox shows what Windows has, and a click applies at once: ticking writes the Run value
    /// and the setting, unticking removes the value and records the setting off. No binding errors.
    /// </summary>
    [Fact]
    public void The_settings_checkbox_applies_at_once_both_ways()
    {
        var (viewModel, registry, store) = SettingsOver(SettingsExe);
        const string Name = ClaudeDashboard.App.Setup.StartWithWindows.ValueName;

        var seen = _harness.Invoke(() =>
        {
            var window = new SettingsWindow(viewModel);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);

                var box = window.StartWithWindowsBox;
                var before = box.IsChecked;
                var toggle = (IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle);

                toggle.Toggle();
                _harness.Pump(DispatcherPriority.Background);
                var tickChecked = box.IsChecked;
                var tickValue = registry.Run.GetValueOrDefault(Name);
                var tickSetting = store.Load().Settings.StartWithWindows;

                toggle.Toggle();
                _harness.Pump(DispatcherPriority.Background);
                var untickChecked = box.IsChecked;
                var untickPresent = registry.Run.ContainsKey(Name);
                var untickSetting = store.Load().Settings.StartWithWindows;

                Assert.Empty(bindings.Problems);

                return (before, Label: window.StartWithWindowsLabel.Text, Enabled: box.IsEnabled, NoteShown: window.NoteText.IsVisible,
                    tickChecked, tickValue, tickSetting, untickChecked, untickPresent, untickSetting);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(SettingsViewModel.StartWithWindowsLabel, seen.Label);
        Assert.True(seen.Enabled);
        Assert.False(seen.NoteShown);
        Assert.False(seen.before);

        Assert.True(seen.tickChecked);
        Assert.Equal($"\"{SettingsExe}\"", seen.tickValue);
        Assert.True(seen.tickSetting);

        Assert.False(seen.untickChecked);
        Assert.False(seen.untickPresent);
        Assert.False(seen.untickSetting);
    }

    /// <summary>
    /// With the Windows off switch set, the checkbox shows unticked with a line saying so; ticking it
    /// clears the Windows mark.
    /// </summary>
    [Fact]
    public void The_settings_window_shows_the_Windows_off_switch_and_ticking_clears_it()
    {
        const string Name = ClaudeDashboard.App.Setup.StartWithWindows.ValueName;
        var (viewModel, registry, _) = SettingsOver(SettingsExe, registry =>
        {
            registry.Run[Name] = $"\"{SettingsExe}\"";
            registry.Approval[Name] = [0x01, 0, 0, 0, 0x50, 0xBF, 0x70, 0xE2, 0x68, 0x51, 0xDD, 0x01];
        });

        var seen = _harness.Invoke(() =>
        {
            var window = new SettingsWindow(viewModel);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);

                var beforeChecked = window.StartWithWindowsBox.IsChecked;
                var beforeNote = window.NoteText.Text;
                var beforeShown = window.NoteText.IsVisible;

                ((IToggleProvider)new CheckBoxAutomationPeer(window.StartWithWindowsBox).GetPattern(PatternInterface.Toggle)).Toggle();
                _harness.Pump(DispatcherPriority.Background);

                Assert.Empty(bindings.Problems);

                return (beforeChecked, beforeNote, beforeShown, AfterChecked: window.StartWithWindowsBox.IsChecked, AfterShown: window.NoteText.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.False(seen.beforeChecked);
        Assert.True(seen.beforeShown);
        Assert.Equal(SettingsViewModel.WindowsDisabledNote, seen.beforeNote);

        Assert.True(seen.AfterChecked);
        Assert.False(seen.AfterShown);
        Assert.False(registry.Approval.ContainsKey(Name));
    }

    /// <summary>A copy that is not installed shows the checkbox disabled, says why, and touches nothing.</summary>
    [Fact]
    public void The_settings_window_for_a_copy_that_is_not_installed_says_why_and_changes_nothing()
    {
        var (viewModel, registry, _) = SettingsOver(exe: null);

        var seen = _harness.Invoke(() =>
        {
            var window = new SettingsWindow(viewModel);
            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                Assert.Empty(bindings.Problems);

                return (Enabled: window.StartWithWindowsBox.IsEnabled, Checked: window.StartWithWindowsBox.IsChecked, Note: window.NoteText.Text, NoteShown: window.NoteText.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.False(seen.Enabled);
        Assert.False(seen.Checked);
        Assert.True(seen.NoteShown);
        Assert.Equal(SettingsViewModel.NotInstalledNote, seen.Note);
        Assert.Empty(registry.Calls);
    }

    /// <summary>
    /// The window lays out at every width from narrow to its own, in whole and fractional steps: the
    /// label and the note wrap inside it rather than running past its edge.
    /// </summary>
    [Fact]
    public void The_settings_window_lays_out_at_whole_and_fractional_widths()
    {
        var (viewModel, _, _) = SettingsOver(exe: null);

        var bad = _harness.Invoke(() =>
        {
            var window = new SettingsWindow(viewModel);
            var failures = new List<string>();

            try
            {
                Realize(window);

                for (var step = 0; step <= 180; step++)
                {
                    window.Width = 300 + (step / 1.5);
                    window.UpdateLayout();

                    var content = (FrameworkElement)window.Content;

                    foreach (var element in new FrameworkElement[] { window.StartWithWindowsBox, window.NoteText })
                    {
                        var right = element.TranslatePoint(new Point(element.ActualWidth, 0), content).X;

                        if (element.ActualWidth <= 0 || right > content.ActualWidth + 0.01)
                        {
                            failures.Add($"{window.Width:F2}: {element.Name} ends at {right:F2} of {content.ActualWidth:F2}");
                        }
                    }
                }

                return failures;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Empty(bad);
    }

    /// <summary>A second Settings request brings the open window forward and never opens another.</summary>
    [Fact]
    public void A_second_settings_request_brings_the_same_window_forward()
    {
        var (viewModel, _, _) = SettingsOver(exe: null);

        var same = _harness.Invoke(() =>
        {
            var made = new List<SettingsWindow>();
            var host = new SettingsWindowHost(viewModel, model =>
            {
                var window = new SettingsWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };

                made.Add(window);

                return window;
            });

            try
            {
                var first = host.Show();
                var second = host.Show();

                return (Same: ReferenceEquals(first, second), Made: made.Count);
            }
            finally
            {
                foreach (var window in made)
                {
                    window.Close();
                }
            }
        });

        Assert.True(same.Same);
        Assert.Equal(1, same.Made);
    }
}
