using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Pipeline;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The Activity window, realized (T1.70, issue #97): the wide form, the narrow forms and the order in which
/// the detail and then the project go; the top line; one window made at start, only hidden when closed;
/// lines added while it is hidden; and a virtualized list. Every window is shown off the side of every
/// monitor, and <c>BindingErrorWatch</c> must stay clean.
/// </summary>
[Collection(WpfApplicationSuite.Name)]
public sealed class ActivityWindowTests(StaHarness harness)
{
    private const string Folder = @"C:\Projects\Claude\claude-dashboard";
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly StaHarness _harness = harness;

    /// <summary>What a line shows at one width.</summary>
    private sealed record Seen(
        ActivityLayout Layout,
        bool Time,
        bool Sign,
        bool What,
        bool Name,
        bool WideProject,
        bool WideDetail,
        bool SecondLine,
        bool NarrowProject,
        bool NarrowDetail,
        string? ProjectTip,
        string? SpokenName);

    /// <summary>
    /// <strong>Wide:</strong> one line in columns, with the project (its full path on hover) and the detail.
    /// <strong>Narrow:</strong> two lines, the project and the detail on the second. Narrower: the detail goes
    /// first, then the project. The time, the sign, what occurred and the name never go. A screen reader
    /// reads the line as one sentence.
    /// </summary>
    [Fact]
    public void The_line_follows_the_width_and_the_detail_goes_before_the_project()
    {
        var seen = WithWindow(window => new[] { 820.0, 500, 360, 280 }.Select(width => Look(window, width)).ToList());

        var wide = seen[0];
        Assert.Equal(ActivityLayout.Wide, wide.Layout);
        Assert.True(wide.WideProject && wide.WideDetail && !wide.SecondLine, $"{wide}");
        Assert.Equal(Folder, wide.ProjectTip);

        var two = seen[1];
        Assert.Equal(ActivityLayout.TwoLines, two.Layout);
        Assert.True(!two.WideProject && !two.WideDetail && two.SecondLine && two.NarrowProject && two.NarrowDetail, $"{two}");

        var noDetail = seen[2];
        Assert.Equal(ActivityLayout.NoDetail, noDetail.Layout);
        Assert.True(noDetail.SecondLine && noDetail.NarrowProject && !noDetail.NarrowDetail, $"{noDetail}");

        var noProject = seen[3];
        Assert.Equal(ActivityLayout.NoProject, noProject.Layout);
        Assert.True(!noProject.SecondLine && !noProject.WideProject && !noProject.WideDetail, $"{noProject}");

        Assert.All(seen, at => Assert.True(at.Time && at.Sign && at.What && at.Name, $"{at}"));
        Assert.Contains("sound played, permission, Reviewer, project claude-dashboard, reminder, waiting 7 min.", seen[0].SpokenName, StringComparison.Ordinal);
    }

    /// <summary>The top of the window: "last heard from Claude Code …", in the tray tooltip's own words.</summary>
    [Fact]
    public void The_top_says_when_claude_code_was_last_heard()
    {
        var lastHeard = WithWindow(window => StaHarness.Find<TextBlock>(window, block => block.Name == "LastHeardLine")!.Text);

        Assert.Equal(TrayTooltip.LastHeard(null, At), lastHeard);
    }

    /// <summary>
    /// <strong>One window, made at start:</strong> lines are added while it is hidden and show when it opens,
    /// with nothing built again (its list is the log's own list). A second open brings the same window to the
    /// front, closing only hides it, and at quit it closes.
    /// </summary>
    [Fact]
    public void The_window_is_made_at_start_and_only_hidden()
    {
        _harness.Invoke(() =>
        {
            var (log, dispatcher) = Log();
            var viewModel = new ActivityViewModel(log, new FakeClock(At), health: null);
            var made = 0;

            var host = new ActivityWindowHost(
                viewModel,
                settings: null,
                Serilog.Core.Logger.None,
                model =>
                {
                    made++;
                    return new ActivityWindow(model) { ShowActivated = false, ShowInTaskbar = false };
                },
                place: OffScreen);

            var window = host.Create();
            Assert.False(window.IsVisible);

            // Lines while it is hidden.
            log.Decided([Nudge("Reviewer")]);
            dispatcher.Pump();

            using var bindings = new BindingErrorWatch();

            var shown = host.Show();
            window.UpdateLayout();
            _harness.Pump(DispatcherPriority.Background);

            Assert.Same(window, shown);
            Assert.Same(window, host.Show());
            Assert.Equal(1, made);
            Assert.Same(log.Lines, StaHarness.Find<ListBox>(window, list => list.Name == "ActivityList")!.ItemsSource);
            Assert.Single(StaHarness.FindAll<ListBoxItem>(window));

            window.Close();
            _harness.Pump(DispatcherPriority.Background);

            Assert.False(window.IsVisible);
            Assert.Same(window, host.Window);
            Assert.True(window.IsLoaded, "Closing must hide the window, not close it.");

            host.Quit();
            window.Close();
            _harness.Pump(DispatcherPriority.Background);

            Assert.False(window.IsLoaded);
            Assert.Empty(bindings.Problems);
        });
    }

    /// <summary>
    /// <strong>The list is virtualized:</strong> with 20,000 lines in the log, the window realizes only the rows
    /// in view.
    /// </summary>
    [Fact]
    public void Twenty_thousand_lines_realize_only_the_rows_in_view()
    {
        var realized = WithWindow(
            window => (StaHarness.FindAll<ListBoxItem>(window).Count, window.ViewModel.Log.Lines.Count),
            lines: 20_000);

        Assert.Equal(20_000, realized.Item2);
        Assert.InRange(realized.Item1, 1, 100);
    }

    private T WithWindow<T>(Func<ActivityWindow, T> assert, int lines = 0) =>
        _harness.Invoke(() =>
        {
            var (log, dispatcher) = Log();
            var viewModel = new ActivityViewModel(log, new FakeClock(At), health: null);

            if (lines > 0)
            {
                log.Decided([.. Enumerable.Range(0, lines).Select(i => Nudge($"s-{i}"))]);
            }
            else
            {
                log.Decided([Suppressed()]);
                log.Decided([Nudge("Reviewer")]);
            }

            dispatcher.Pump();

            var window = new ActivityWindow(viewModel);
            using var bindings = new BindingErrorWatch();

            try
            {
                OffScreen(window);
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                window.UpdateLayout();
                _harness.Pump(DispatcherPriority.Background);

                var result = assert(window);

                Assert.Empty(bindings.Problems);
                return result;
            }
            finally
            {
                window.Close();
            }
        });

    private Seen Look(ActivityWindow window, double width)
    {
        window.Width = width;
        window.UpdateLayout();
        _harness.Pump(DispatcherPriority.Background);
        window.UpdateLayout();

        var item = StaHarness.FindAll<ListBoxItem>(window).First(candidate => candidate.DataContext is ActivityLineViewModel { What: "permission" });

        bool Shown(string name) => StaHarness.Find<FrameworkElement>(item, element => element.Name == name)?.IsVisible == true;

        return new Seen(
            window.ViewModel.Layout,
            Shown("TimeText"),
            Shown("SignText"),
            Shown("WhatText"),
            Shown("NameText"),
            Shown("WideProject"),
            Shown("WideDetail"),
            Shown("SecondLine"),
            Shown("NarrowProject"),
            Shown("NarrowDetail"),
            StaHarness.Find<TextBlock>(item, block => block.Name == "WideProject")?.ToolTip as string,
            AutomationProperties.GetName(item));
    }

    private static (ActivityLog Log, QueueingDispatcher Dispatcher) Log()
    {
        var dispatcher = new QueueingDispatcher();
        return (new ActivityLog(dispatcher, new FakeClock(At)), dispatcher);
    }

    private static Decision Nudge(string title) =>
        new(At, "s-2", DecisionKind.NudgePlayed, Reason: "permission", Detail: "rung=1 waitedMinutes=7") { SessionTitle = title, Cwd = Folder };

    private static Decision Suppressed() =>
        new(At, "a3f9c21e-77b0", DecisionKind.NoticeSuppressed, Reason: "GroupDone", Detail: "kind=Notice sound=finished") { Cwd = Folder };

    private static void OffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
    }

    /// <summary>Collects WPF's binding diagnostics while a window is realized, as <c>MainWindowTests</c> does.</summary>
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
}
