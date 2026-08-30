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
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
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
public sealed class MainWindowTests(StaHarness harness)
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly StaHarness _harness = harness;

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
        Action<MainViewModel>? prepare = null)
    {
        return _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => motionAllowed, observeChanges: false);
            using var viewModel = new MainViewModel(registry.Projection, policy, new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());

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

            var window = new MainWindow(viewModel);
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

    [Fact]
    public void The_counts_strip_shows_only_the_bands_that_have_something_in_them()
    {
        var texts = WithWindow(
            registry =>
            {
                registry.Working("blocked", At);
                registry.Blocked("blocked", At.AddMinutes(1));
            },
            (window, _) => StaHarness.FindAll<TextBlock>(window)
                .Where(block => block.IsVisible)
                .Select(TextOf)
                .ToList());

        Assert.Contains(" need you", texts);
        Assert.DoesNotContain(" unread", texts);
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
    public void An_expanded_row_shows_the_whole_exchange_and_a_reserved_terminal_slot()
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

                var terminal = StaHarness.FindAll<Button>(RowFor(window, "finished"))
                    .SelectMany(button => StaHarness.FindAll<TextBlock>(button)
                        .Where(block => TextOf(block) == "Open terminal")
                        .Select(_ => button))
                    .SingleOrDefault();

                return (Texts: texts, TerminalEnabled: terminal?.IsEnabled, HasTerminal: terminal is not null);
            });

        Assert.Contains("write the tests", found.Texts);
        Assert.Contains("Added 23 tests.", found.Texts);
        Assert.Contains("CLAUDE ANSWERED", found.Texts);

        // Reserved for Phase 2 navigation, and inert: a slot that silently did nothing would be a
        // worse lie than one that says it is not ready.
        Assert.True(found.HasTerminal);
        Assert.False(found.TerminalEnabled);
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

            var window = new MainWindow(viewModel);

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
            var window = new MainWindow(viewModel);

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
            var window = new MainWindow(viewModel);
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
            var window = new MainWindow(viewModel);
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
}
