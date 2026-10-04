using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Architecture;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Pipeline;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// From an Activity line to its row, and from a row to its lines (T1.71, issue #97). Both windows are realized
/// together, off the side of every monitor, and connected as the product connects them
/// (<see cref="ActivityLinks.Connect"/>); <see cref="BindingErrorWatch"/> must stay clean in both.
/// </summary>
/// <remarks>
/// A click in the Activity window changes nothing but what the main window shows: every test that clicks also
/// checks that the Ack publisher, the roster store's sink and the tray's sink saw nothing, and that the
/// Registry's sessions are the same records as before.
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class ActivityLinkTests(StaHarness harness)
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly StaHarness _harness = harness;

    /// <summary>
    /// <strong>A click on a line</strong> of a session whose row is in the main window: the main window comes to
    /// the front, the row is in view and open, and no Ack, mute or event follows.
    /// </summary>
    [Fact]
    public void A_click_on_a_line_brings_the_main_window_up_and_opens_its_row_in_view()
    {
        WithBoth(Many, both =>
        {
            var target = Bottom(both);
            Assert.False(InView(both.Window, target), "the row starts below the fold");

            both.Window.Hide();
            var line = Line(both, target.Id.Value);
            var before = Before(both);

            Click(both.ActivityWindow, line);
            Settle(both);

            Assert.True(both.Window.IsVisible);
            Assert.True(target.IsExpanded);
            Assert.True(InView(both.Window, target));
            NothingElse(both, before);
        });
    }

    /// <summary>
    /// <strong>Enter on a selected line</strong> does what a click does: the same command, through the list's key.
    /// </summary>
    [Fact]
    public void Enter_on_a_selected_line_does_what_a_click_does()
    {
        WithBoth(Many, both =>
        {
            var target = Bottom(both);
            both.Window.Hide();
            var line = Line(both, target.Id.Value);
            var before = Before(both);

            var item = Item(both.ActivityWindow, line);
            List(both.ActivityWindow).SelectedItem = line;
            item.Focus();
            item.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(item), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
            Settle(both);

            Assert.True(both.Window.IsVisible);
            Assert.True(target.IsExpanded);
            Assert.True(InView(both.Window, target));
            NothingElse(both, before);
        });
    }

    /// <summary>
    /// <strong>In selection mode</strong> the click brings the row into view but does not open it, and the
    /// selection does not change: setting <c>IsExpanded</c> there would toggle it.
    /// </summary>
    [Fact]
    public void In_selection_mode_the_click_brings_the_row_into_view_and_leaves_it_closed_and_the_selection_as_it_was()
    {
        WithBoth(Many, both =>
        {
            var target = Bottom(both);
            var chosen = both.Main.Rows.OfType<SessionViewModel>().First();

            both.Main.IsSelecting = true;
            chosen.IsExpanded = true;
            Assert.True(chosen.IsSelected, "a click on a row in selection mode selects it");

            var line = Line(both, target.Id.Value);
            var before = Before(both);

            line.ShowCommand.Execute(null);
            Settle(both);

            Assert.True(InView(both.Window, target));
            Assert.False(target.IsExpanded);
            Assert.False(target.IsSelected);
            Assert.True(chosen.IsSelected);
            Assert.Equal(1, both.Main.SelectedCount);
            NothingElse(both, before);
        });
    }

    /// <summary>
    /// <strong>A row behind its group's "+ 1 quiet"</strong>: the click unfolds the group, as a click on that line
    /// does, then scrolls to the row and opens it.
    /// </summary>
    [Fact]
    public void A_row_behind_its_groups_quiet_line_is_unfolded_scrolled_to_and_opened()
    {
        WithBoth(
            registry =>
            {
                Many(registry);
                registry.Working("s-live", At, Shared, title: "Live");
                Quiet(registry, "s-quiet", Shared);
            },
            both =>
            {
                var heading = both.Main.Rows.OfType<GroupViewModel>().Single(group => group.Workspace == Shared);
                Assert.DoesNotContain(both.Main.Rows, row => row is SessionViewModel { Id.Value: "s-quiet" });
                Assert.Contains(both.Main.Rows, row => row is QuietFooterViewModel footer && ReferenceEquals(footer.Owner, heading));

                var line = Line(both, "s-quiet");
                var before = Before(both, rows: false);

                line.ShowCommand.Execute(null);
                Settle(both);

                var row = both.Main.Rows.OfType<SessionViewModel>().Single(candidate => candidate.Id.Value == "s-quiet");
                Assert.True(heading.IsExpanded);
                Assert.True(row.IsExpanded);
                Assert.True(InView(both.Window, row));
                NothingElse(both, before);
            });
    }

    /// <summary>
    /// <strong>A row inside a collapsed group</strong> (every member quiet for long enough, one stale line): the
    /// click unfolds the group, then scrolls to the row and opens it.
    /// </summary>
    [Fact]
    public void A_row_inside_a_collapsed_group_is_unfolded_scrolled_to_and_opened()
    {
        WithBoth(
            registry =>
            {
                Many(registry);
                Quiet(registry, "s-stale", Shared);
            },
            both =>
            {
                var heading = both.Main.Rows.OfType<GroupViewModel>().Single(group => group.Workspace == Shared);
                Assert.True(heading.IsStale);
                Assert.DoesNotContain(both.Main.Rows, row => row is SessionViewModel { Id.Value: "s-stale" });

                var line = Line(both, "s-stale");
                var before = Before(both, rows: false);

                line.ShowCommand.Execute(null);
                Settle(both);

                var row = both.Main.Rows.OfType<SessionViewModel>().Single(candidate => candidate.Id.Value == "s-stale");
                Assert.True(heading.IsExpanded);
                Assert.True(row.IsExpanded);
                Assert.True(InView(both.Window, row));
                NothingElse(both, before);
            },
            prepare: main => main.Tick(At.AddHours(1)));
    }

    /// <summary>
    /// <strong>A row in the flat view's Ended line</strong>: the click opens the band, as a click on its line does,
    /// then scrolls to the row and opens it.
    /// </summary>
    [Fact]
    public void A_row_in_the_flat_views_ended_line_is_unfolded_scrolled_to_and_opened()
    {
        WithBoth(
            registry =>
            {
                Many(registry);
                registry.Working("s-ended", At, Shared, title: "Ended one");
                registry.Ended("s-ended", At.AddSeconds(1), Shared);
            },
            both =>
            {
                var band = both.Main.Rows.OfType<BandHeaderViewModel>().Single(header => header.Band == AttentionBand.Ended);
                Assert.False(band.IsExpanded);
                Assert.DoesNotContain(both.Main.Rows, row => row is SessionViewModel { Id.Value: "s-ended" });

                var line = Line(both, "s-ended");
                var before = Before(both, rows: false);

                line.ShowCommand.Execute(null);
                Settle(both);

                var row = both.Main.Rows.OfType<SessionViewModel>().Single(candidate => candidate.Id.Value == "s-ended");
                Assert.True(band.IsExpanded);
                Assert.True(row.IsExpanded);
                Assert.True(InView(both.Window, row));
                NothingElse(both, before);
            },
            grouped: false);
    }

    /// <summary>
    /// <strong>A line of a session that is not in the window</strong>: the click brings the main window to the
    /// front and nothing else changes. The line's hover says why, and so does its help text for a screen reader.
    /// </summary>
    [Fact]
    public void A_line_of_a_session_not_in_the_window_only_brings_the_window_up_and_says_why()
    {
        WithBoth(Many, both =>
        {
            both.Window.Hide();
            var line = Line(both, "s-gone");
            var before = Before(both);

            line.ShowCommand.Execute(null);
            Settle(both);

            Assert.True(both.Window.IsVisible);
            Assert.DoesNotContain(both.Main.Rows.OfType<SessionViewModel>(), row => row.IsExpanded);
            NothingElse(both, before);

            Assert.True(line.IsGone);
            Assert.Equal(ActivityLineViewModel.SessionGoneText, line.GoneText);
            Assert.Equal(line.Sentence + "\n" + ActivityLineViewModel.SessionGoneText, line.Hover);

            var item = Item(both.ActivityWindow, line);
            Assert.Equal(line.Hover, item.ToolTip);
            Assert.Equal(ActivityLineViewModel.SessionGoneText, AutomationProperties.GetHelpText(item));
        });
    }

    /// <summary>
    /// <strong>The hover follows the window:</strong> a line that arrives before its session's row says the session
    /// is not there, and stops saying so when the row arrives. A line of a session that is there never says it.
    /// </summary>
    [Fact]
    public void The_hover_follows_the_window()
    {
        WithBoth(Many, both =>
        {
            var present = Line(both, "s-00");
            var early = Line(both, "s-new");
            Assert.False(present.IsGone);
            Assert.Equal(present.Sentence, present.Hover);
            Assert.True(early.IsGone);

            both.Registry.Working("s-new", At.AddMinutes(1), Shared, title: "New");
            Settle(both);

            Assert.False(early.IsGone);
            Assert.Equal(early.Sentence, early.Hover);
            Assert.Equal(string.Empty, AutomationProperties.GetHelpText(Item(both.ActivityWindow, early)));
        });
    }

    /// <summary>
    /// <strong>A group's sound</strong>: in Grouped view the click scrolls to the group's heading; in Flat view,
    /// where no group has a heading, it only brings the window to the front, and the hover says why.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_groups_sound_scrolls_to_its_heading_when_the_group_is_there(bool grouped)
    {
        WithBoth(
            Many,
            both =>
            {
                var key = GroupKeys.ForWorkspace(Workspace(19));
                both.Log.Decided([new Decision(At, null, DecisionKind.GroupNoticePlayed, Reason: "notice", Detail: $"group={key.Value} members=s-19")]);
                both.Dispatcher.Pump();
                var line = both.Log.Lines[0];
                Assert.Equal(key, line.Line.Group);

                both.Window.Hide();
                var before = Before(both);

                line.ShowCommand.Execute(null);
                Settle(both);

                Assert.True(both.Window.IsVisible);
                NothingElse(both, before);

                if (grouped)
                {
                    var heading = both.Main.Rows.OfType<GroupViewModel>().Single(group => group.Key == key);
                    Assert.True(InView(both.Window, heading));
                    Assert.False(line.IsGone);
                }
                else
                {
                    Assert.True(line.IsGone);
                    Assert.Equal(ActivityLineViewModel.GroupGoneText, line.GoneText);
                }

                Assert.DoesNotContain(both.Main.Rows.OfType<SessionViewModel>(), row => row.IsExpanded);
            },
            grouped: grouped);
    }

    /// <summary>
    /// <strong>"Show activity" on an open row</strong> lists only that session's lines, with the bar; a new line
    /// for that session arrives in the filtered list, a line for another session or for a group does not;
    /// <b>Show all</b> brings every line back; and "Show activity" on another row changes the filter to it.
    /// </summary>
    [Fact]
    public void Show_activity_lists_only_that_sessions_lines_and_new_ones_arrive()
    {
        WithBoth(
            registry =>
            {
                registry.Working("s-1", At, Workspace(1), title: "Director");
                registry.Working("s-2", At.AddSeconds(1), Workspace(2));
            },
            both =>
            {
                Line(both, "s-1");
                Line(both, "s-2");
                both.Log.Decided([new Decision(At, null, DecisionKind.GroupNoticePlayed, Reason: "notice", Detail: "group=roster:Team members=s-1")]);
                both.Dispatcher.Pump();

                var director = Row(both, "s-1");
                director.IsExpanded = true;
                Settle(both);
                var before = Before(both);

                Invoke(Find<Button>(Container(both.Window, director), "ShowActivityButton"));
                Settle(both);

                Assert.Equal(1, both.ActivityShown);
                Assert.True(both.Activity.IsFiltered);
                Assert.Equal("Only Director", both.Activity.OnlyText);
                Assert.True(Find<DockPanel>(both.ActivityWindow, "FilterBar").IsVisible);
                Assert.Equal("Only Director", Find<TextBlock>(both.ActivityWindow, "FilterText").Text);
                Assert.Equal(["s-1"], Shown(both));
                Assert.All(StaHarness.FindAll<ListBoxItem>(both.ActivityWindow), item => Assert.Equal("s-1", ((ActivityLineViewModel)item.DataContext).Line.SessionId));
                NothingElse(both, before);

                // A new line for that session arrives; one for another session does not.
                Line(both, "s-1");
                Line(both, "s-2");
                Settle(both);
                Assert.Equal(["s-1", "s-1"], Shown(both));
                Assert.Equal(2, StaHarness.FindAll<ListBoxItem>(both.ActivityWindow).Count);

                // Show all: every line again, and the bar goes.
                Invoke(Find<Button>(both.ActivityWindow, "ShowAllButton"));
                Settle(both);
                Assert.False(both.Activity.IsFiltered);
                Assert.False(Find<DockPanel>(both.ActivityWindow, "FilterBar").IsVisible);
                Assert.Equal(both.Log.Lines.Count, both.Activity.Shown.Cast<object>().Count());

                // "Show activity" on another row changes the filter to that row; a row with no name shows its short id.
                Invoke(Find<Button>(Container(both.Window, director), "ShowActivityButton"));
                Settle(both);
                var other = Row(both, "s-2");
                other.IsExpanded = true;
                Settle(both);
                Invoke(Find<Button>(Container(both.Window, other), "ShowActivityButton"));
                Settle(both);

                Assert.Equal("Only s-2", both.Activity.OnlyText);
                Assert.Equal(["s-2", "s-2"], Shown(both));
                Assert.Equal(3, both.ActivityShown);
            });
    }

    /// <summary>
    /// <strong>The product connects the two windows once,</strong> on the UI thread, after the Activity window is
    /// made and the main window exists, by the code path the tests above use.
    /// </summary>
    [Fact]
    public void Program_connects_the_two_windows_once_after_the_activity_window_is_made()
    {
        var code = GuardScan.CodeOnly(File.ReadAllText(
            Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Program.cs")));

        Assert.Equal(1, GuardScan.Occurrences(code, "ActivityLinks.Connect("));
        Assert.True(
            code.IndexOf("activityWindows.Create()", StringComparison.Ordinal)
                < code.IndexOf("ActivityLinks.Connect(", StringComparison.Ordinal),
            "Program must make the Activity window before it connects the two windows.");
    }

    /// <summary>
    /// <strong>No right-click menu</strong> on a row or on a line (T1.71's guardrail): "Show activity" is an action
    /// in the open row, and a line is a click.
    /// </summary>
    [Theory]
    [InlineData("RowTemplates.xaml")]
    [InlineData("MainWindow.xaml")]
    [InlineData("ActivityWindow.xaml")]
    public void No_row_and_no_line_has_a_right_click_menu(string file)
    {
        var markup = File.ReadAllText(Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Ui", file));

        Assert.DoesNotContain("ContextMenu", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>No line is selected until the operator selects one</strong> (the T1.71 review): not on first show
    /// with lines already there, not after new lines, not after "Show activity" and not after <b>Show all</b>. So
    /// Enter does nothing until the operator selects a line. The list does not follow the view's current item.
    /// </summary>
    [Fact]
    public void No_line_is_selected_until_the_operator_selects_one()
    {
        WithBoth(
            registry =>
            {
                registry.Working("s-1", At, Workspace(1), title: "Director");
                registry.Working("s-2", At.AddSeconds(1), Workspace(2));
            },
            both =>
            {
                var list = List(both.ActivityWindow);
                Assert.Equal(2, list.Items.Count);
                Assert.Null(list.SelectedItem);

                Line(both, "s-1");
                Line(both, "s-2");
                Assert.Null(list.SelectedItem);

                both.Activity.ShowOnly(new SessionId("s-1"), "Director");
                Settle(both);
                Assert.Null(list.SelectedItem);

                both.Activity.ShowAllCommand.Execute(null);
                Settle(both);
                Assert.Null(list.SelectedItem);
                Assert.Equal(-1, list.SelectedIndex);

                // Enter with nothing selected: the main window stays hidden.
                both.Window.Hide();
                var before = Before(both);
                list.Focus();
                list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
                Settle(both);

                Assert.False(both.Window.IsVisible);
                NothingElse(both, before);
            },
            seed: (log, dispatcher) =>
            {
                log.Decided([new Decision(At, "s-1", DecisionKind.StateMoved, "Working", "Unread")]);
                log.Decided([new Decision(At, "s-2", DecisionKind.StateMoved, "Working", "Unread")]);
                dispatcher.Pump();
            });
    }

    // ---- The fixture ---------------------------------------------------------------------------

    private const string Shared = @"C:\dev\shared";

    private static string Workspace(int i) => $@"C:\dev\p{i:00}";

    /// <summary>Twenty working sessions, each in its own folder: more rows than the window shows.</summary>
    private static void Many(RegistryHarness registry)
    {
        for (var i = 0; i < 20; i++)
        {
            registry.Working($"s-{i:00}", At.AddSeconds(i), Workspace(i), title: $"Task {i}");
        }
    }

    /// <summary>A session that finished and was seen: the Quiet band.</summary>
    private static void Quiet(RegistryHarness registry, string id, string cwd)
    {
        var prompt = registry.Working(id, At, cwd, title: "Quiet one");
        registry.Finished(id, At.AddSeconds(1), prompt, cwd: cwd);
        registry.Acked(id, At.AddSeconds(2), cwd);
    }

    /// <summary>Both windows and what a test reads.</summary>
    private sealed class Both
    {
        public required RegistryHarness Registry { get; init; }

        public required MainViewModel Main { get; init; }

        public required MainWindow Window { get; init; }

        public required ActivityLog Log { get; init; }

        public required QueueingDispatcher Dispatcher { get; init; }

        public required ActivityViewModel Activity { get; init; }

        public required ActivityWindow ActivityWindow { get; init; }

        public required StubAckPublisher Ack { get; init; }

        public required RecordingEventSink RosterSink { get; init; }

        public required RecordingEventSink TraySink { get; init; }

        public int ActivityShown { get; set; }
    }

    /// <summary>What must not change: the rows (when the test does not unfold) and the Registry's sessions.</summary>
    private sealed record Snapshot(IReadOnlyList<DashboardRow>? Rows, IReadOnlyList<Session> Sessions);

    private void WithBoth(
        Action<RegistryHarness> arrange,
        Action<Both> assert,
        bool grouped = true,
        Action<MainViewModel>? prepare = null,
        Action<ActivityLog, QueueingDispatcher>? seed = null)
    {
        _harness.Invoke(() =>
        {
            using var registry = new RegistryHarness();
            using var policy = new MotionPolicy(() => false, observeChanges: false);
            var ack = new StubAckPublisher();
            var rosterSink = new RecordingEventSink();
            var traySink = new RecordingEventSink();
            using var main = new MainViewModel(registry.Projection, policy, ack, new FakeClipboard(), new RosterStore(rosterSink), new RecordingRosterPersistence());

            // Before the window is realized, as MainWindowTests does: toggling it on a live window raises
            // transient binding errors that say nothing about the markup.
            main.IsGrouped = grouped;
            arrange(registry);
            prepare?.Invoke(main);

            var window = new MainWindow(main, TestTrays.For(registry.Projection, sink: traySink));
            var dispatcher = new QueueingDispatcher();
            var log = new ActivityLog(dispatcher, new FakeClock(At));

            // Lines from before the Activity window's view model exists, as at a start with events already in.
            seed?.Invoke(log, dispatcher);
            var activity = new ActivityViewModel(log, new FakeClock(At), health: null);
            var activityWindow = new ActivityWindow(activity) { Width = 820 };

            var both = new Both
            {
                Registry = registry,
                Main = main,
                Window = window,
                Log = log,
                Dispatcher = dispatcher,
                Activity = activity,
                ActivityWindow = activityWindow,
                Ack = ack,
                RosterSink = rosterSink,
                TraySink = traySink,
            };

            ActivityLinks.Connect(window, main, activity, () => both.ActivityShown++);

            using var bindings = new BindingErrorWatch();

            try
            {
                Realize(window);
                Realize(activityWindow);
                assert(both);

                Assert.Empty(bindings.Problems);
            }
            finally
            {
                window.Hide();
                activityWindow.Close();
            }
        });
    }

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

    private void Settle(Both both)
    {
        both.Dispatcher.Pump();
        both.Window.UpdateLayout();
        both.ActivityWindow.UpdateLayout();
        _harness.Pump(DispatcherPriority.Background);
        both.Window.UpdateLayout();
    }

    /// <summary>A line for <paramref name="session"/>, through the log as the recorder tells it.</summary>
    private ActivityLineViewModel Line(Both both, string session)
    {
        both.Log.Decided([new Decision(At, session, DecisionKind.StateMoved, "Working", "Unread")]);
        Settle(both);
        return both.Log.Lines[0];
    }

    private static SessionViewModel Bottom(Both both) => both.Main.Rows.OfType<SessionViewModel>().Last();

    private static SessionViewModel Row(Both both, string id) =>
        both.Main.Rows.OfType<SessionViewModel>().Single(row => row.Id.Value == id);

    private static List<string?> Shown(Both both) =>
        [.. both.Activity.Shown.Cast<ActivityLineViewModel>().Select(line => line.Line.SessionId)];

    private static Snapshot Before(Both both, bool rows = true) =>
        new(rows ? [.. both.Main.Rows] : null, [.. both.Registry.Projection.Sessions]);

    /// <summary>No Ack, no mute, no event, the Registry's sessions as they were, and the rows too when asked.</summary>
    private static void NothingElse(Both both, Snapshot before)
    {
        Assert.Empty(both.Ack.Asked);
        Assert.Empty(both.RosterSink.Published);
        Assert.Empty(both.TraySink.Published);
        Assert.Equal(before.Sessions, both.Registry.Projection.Sessions);

        if (before.Rows is { } rows)
        {
            Assert.Equal(rows, both.Main.Rows);
        }
    }

    private static ListBox List(ActivityWindow window) => (ListBox)window.FindName("ActivityList");

    private static ListBoxItem Item(ActivityWindow window, ActivityLineViewModel line) =>
        (ListBoxItem)List(window).ItemContainerGenerator.ContainerFromItem(line);

    /// <summary>A left click on the line's surface, where the line's mouse binding is.</summary>
    private static void Click(ActivityWindow window, ActivityLineViewModel line)
    {
        var surface = Find<Border>(Item(window, line), "LineSurface");

        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent,
            Source = surface,
        });
    }

    private static void Invoke(Button button)
    {
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke();
    }

    private static FrameworkElement Container(MainWindow window, DashboardRow row) =>
        (FrameworkElement)((ItemsControl)window.FindName("RowsHost")).ItemContainerGenerator.ContainerFromItem(row);

    private static T Find<T>(DependencyObject root, string name)
        where T : FrameworkElement =>
        StaHarness.Find<T>(root, element => element.Name == name)
            ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    /// <summary>Whether the row's container sits inside the main window's scrolling viewport.</summary>
    private static bool InView(MainWindow window, DashboardRow row)
    {
        var container = Container(window, row);
        DependencyObject current = container;

        while (current is not ScrollViewer)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        var viewer = (ScrollViewer)current;
        var top = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        var height = Math.Min(container.ActualHeight, viewer.ViewportHeight);

        return top >= -1 && top + height <= viewer.ViewportHeight + 1;
    }
}
