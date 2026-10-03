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
        RosterStore? rosters = null)
    {
        return _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => motionAllowed, observeChanges: false);
            using var viewModel = new MainViewModel(registry.Projection, policy, new StubAckPublisher(), new FakeClipboard(), rosters ?? new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());

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

    /// <summary>Collects WPF's binding diagnostics while a window is being realized.</summary>
    private sealed class BindingErrorWatch : IDisposable
    {
        private readonly Listener _listener = new();
        private readonly SourceLevels _previous;

        public BindingErrorWatch()
        {
            PresentationTraceSources.Refresh();
            _previous = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            PresentationTraceSources.DataBindingSource.Listeners.Add(_listener);
        }

        public IReadOnlyList<string> Problems => _listener.Problems;

        public void Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = _previous;
            _listener.Dispose();
        }

        private sealed class Listener : TraceListener
        {
            public List<string> Problems { get; } = [];

            public override void Write(string? message) => Record(message);

            public override void WriteLine(string? message) => Record(message);

            private void Record(string? message)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    Problems.Add(message);
                }
            }
        }
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
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());

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
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
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
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
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
                new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
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
                new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
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
                new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
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
                new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
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
    [InlineData("../App.xaml")]
    public void No_other_markup_in_the_application_animates(string file)
    {
        var markup = File.ReadAllText(Path.Combine(UiFolder, file));

        Assert.Equal(0, Occurrences(markup, "Storyboard"));
        Assert.Equal(0, Occurrences(markup, "Animation"));
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

                Assert.Equal(4, Grid.GetColumn(button));
                Assert.Equal(4, ((Grid)button.Parent).ColumnDefinitions.Count - 1);
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
