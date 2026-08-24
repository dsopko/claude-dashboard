using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        bool showQuiet = false)
    {
        return _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => motionAllowed, observeChanges: false);
            using var viewModel = new MainViewModel(registry.Projection, policy);

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

        _harness.Pump(DispatcherPriority.Loaded);
        _harness.Pump(DispatcherPriority.Render);
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

    [Fact]
    public void A_row_shows_its_prompt_in_the_mono_face()
    {
        var (text, family) = WithWindow(
            registry => registry.Working("busy", At, prompt: "draft a migration plan"),
            (window, _) =>
            {
                var block = StaHarness.Find<TextBlock>(
                    RowFor(window, "busy"),
                    candidate => candidate.Text == "draft a migration plan");

                return (block?.Text, block?.FontFamily.Source);
            });

        Assert.Equal("draft a migration plan", text);
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
                .Select(block => block.Text)
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
                    .Select(block => block.Text)
                    .ToList();

                var terminal = StaHarness.FindAll<Button>(RowFor(window, "finished"))
                    .SelectMany(button => StaHarness.FindAll<TextBlock>(button)
                        .Where(block => block.Text == "Open terminal")
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
                .Select(block => block.Text)
                .ToList());

        Assert.DoesNotContain("Added 23 tests.", texts);
        Assert.DoesNotContain("CLAUDE ANSWERED", texts);
    }

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
            using var viewModel = new MainViewModel(registry.Projection);
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
                registry.Blocked(id, At.AddMinutes(1), "idle_prompt");
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
