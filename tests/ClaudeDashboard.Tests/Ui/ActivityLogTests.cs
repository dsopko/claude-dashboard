using System.Reflection;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Pipeline;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The Activity window's one list (T1.70, issue #97): fed from the consumer's decisions, newest first, the
/// shown kinds only, one post for each batch with a shown line, and the newest 20,000 lines kept.
/// </summary>
public sealed class ActivityLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly QueueingDispatcher _dispatcher = new();

    /// <summary>
    /// <strong>Decisions of every kind:</strong> the list shows the shown kinds, newest first, and none of the
    /// others.
    /// </summary>
    [Fact]
    public void Decisions_of_every_kind_show_the_shown_kinds_newest_first()
    {
        var log = new ActivityLog(_dispatcher, new FakeClock(At));

        foreach (var kind in Enum.GetValues<DecisionKind>())
        {
            log.Decided([new Decision(At, "s-1", kind) { SessionTitle = kind.ToString() }]);
        }

        _dispatcher.Pump();

        // The block's table, written out: a kind let through by mistake shows as a line this does not expect.
        DecisionKind[] table =
        [
            DecisionKind.SessionAdded, DecisionKind.StateMoved, DecisionKind.SilenceSwept, DecisionKind.SessionEnded,
            DecisionKind.AckApplied, DecisionKind.NoticePlayed, DecisionKind.NudgePlayed, DecisionKind.GroupNoticePlayed,
            DecisionKind.NoticeSuppressed, DecisionKind.SoundDropped, DecisionKind.MuteApplied, DecisionKind.MuteExpired,
        ];

        var shownInOrder = Enum.GetValues<DecisionKind>().Where(table.Contains).Reverse().Select(kind => kind.ToString());

        Assert.Equal(shownInOrder, log.Lines.Select(line => line.Name));
        Assert.Equal(log.Lines.Select(line => line.Id).OrderDescending(), log.Lines.Select(line => line.Id));
    }

    /// <summary>One post for each batch that holds a shown line, and none for a batch without one.</summary>
    [Fact]
    public void One_post_for_each_batch_with_a_shown_line()
    {
        var log = new ActivityLog(_dispatcher, new FakeClock(At));

        log.Decided([new Decision(At, "s-1", DecisionKind.TrayLightChanged), new Decision(At, "s-1", DecisionKind.EventDeclined)]);
        Assert.Equal(0, log.PostedCount);
        Assert.Equal(0, _dispatcher.PostedCount);

        log.Decided(
        [
            new Decision(At, "s-1", DecisionKind.StateMoved, "Working", "NeedsPermission"),
            new Decision(At, "s-1", DecisionKind.EventDeclined, Reason: "Stale"),
            new Decision(At, "s-1", DecisionKind.NoticePlayed, Reason: "permission"),
        ]);

        Assert.Equal(1, log.PostedCount);
        Assert.Equal(1, _dispatcher.PostedCount);

        _dispatcher.Pump();
        Assert.Equal(["permission", "needs permission"], log.Lines.Select(line => line.What));
    }

    /// <summary>
    /// <strong>Past 20,000 lines the oldest go in one step, down to 19,000</strong> (the T1.70 review): the newest
    /// is on top, the bottom says that older lines are not kept, and the next trim comes 1,000 lines later.
    /// </summary>
    [Fact]
    public void The_twenty_thousand_and_first_line_trims_the_list_to_nineteen_thousand()
    {
        var log = new ActivityLog(_dispatcher, new FakeClock(At));

        log.Decided([.. Enumerable.Range(1, ActivityLog.Limit).Select(i => Played($"s-{i}"))]);
        _dispatcher.Pump();

        Assert.Equal(20_000, log.Lines.Count);
        Assert.False(log.HasDropped);
        Assert.Equal(string.Empty, log.DroppedLine);
        Assert.Equal(0, log.TrimCount);

        log.Decided([Played("newest")]);
        _dispatcher.Pump();

        Assert.Equal(19_000, log.Lines.Count);
        Assert.Equal("newest", log.Lines[0].Name);
        Assert.Equal(20_001 - 19_000 + 1, log.Lines[^1].Id);
        Assert.Equal(1, log.TrimCount);
        Assert.True(log.HasDropped);
        Assert.Equal(ActivityLog.DroppedText, log.DroppedLine);

        // The next 1,000 lines: no trim until the list passes 20,000 again.
        for (var i = 0; i < 1_000; i++)
        {
            log.Decided([Played($"later-{i}")]);
        }

        _dispatcher.Pump();
        Assert.Equal(20_000, log.Lines.Count);
        Assert.Equal(1, log.TrimCount);

        log.Decided([Played("one more")]);
        _dispatcher.Pump();
        Assert.Equal(19_000, log.Lines.Count);
        Assert.Equal(2, log.TrimCount);
    }

    /// <summary>
    /// <strong>A sound line goes above its cause</strong> (the director's ruling on the T1.70 review): in one record
    /// the engine's notice comes before the state change, and the window shows "♪ permission" on top, with
    /// "needs permission" directly under it. The order between records does not change.
    /// </summary>
    [Fact]
    public void A_sound_line_goes_above_the_change_that_caused_it()
    {
        var log = new ActivityLog(_dispatcher, new FakeClock(At));

        log.Decided([new Decision(At, "s-1", DecisionKind.SessionAdded, ToState: "Working")]);
        log.Decided(
        [
            new Decision(At, "s-1", DecisionKind.NoticePlayed, Reason: "permission"),
            new Decision(At, "s-1", DecisionKind.StateMoved, "Working", "NeedsPermission"),
        ]);
        _dispatcher.Pump();

        Assert.Equal(["permission", "needs permission", "new session"], log.Lines.Select(line => line.What));
        Assert.True(log.Lines[0].Line.Played);
        Assert.Equal(log.Lines.Select(line => line.Id).OrderDescending(), log.Lines.Select(line => line.Id));
    }

    /// <summary>A "no sound" line goes above its cause too: why there was no sound, then what happened.</summary>
    [Theory]
    [InlineData(DecisionKind.NoticeSuppressed, "AllMuted")]
    [InlineData(DecisionKind.SoundDropped, "NoOutput")]
    public void A_no_sound_line_goes_above_the_change_that_caused_it(DecisionKind kind, string reason)
    {
        var log = new ActivityLog(_dispatcher, new FakeClock(At));

        log.Decided(
        [
            new Decision(At, "s-1", kind, Reason: reason, Detail: "kind=Notice sound=finished"),
            new Decision(At, "s-1", DecisionKind.StateMoved, "Working", "Unread"),
        ]);
        _dispatcher.Pump();

        Assert.Equal(["no sound", "finished"], log.Lines.Select(line => line.What));
    }

    /// <summary>
    /// <strong>The day name follows the day</strong>: a line at 23:59 reads "23:59" that day, and the next day, after
    /// the tick, "Sun 23:59" (the day's own name), with its screen-reader sentence too. No timer: the tick.
    /// </summary>
    [Fact]
    public void A_line_from_yesterday_reads_its_day_name_after_midnight()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 10, 4, 23, 59, 0, DateTimeKind.Local));
        var clock = new FakeClock(evening);
        var log = new ActivityLog(_dispatcher, clock);
        var viewModel = new ActivityViewModel(log, clock, health: null);

        log.Decided([new Decision(evening, "s-1", DecisionKind.NoticePlayed, Reason: "finished") { SessionTitle = "Director" }]);
        _dispatcher.Pump();

        var line = Assert.Single(log.Lines);
        var changed = new List<string?>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(evening.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture), line.Time);

        // A tick the same day changes nothing.
        viewModel.Tick(evening.AddSeconds(30));
        Assert.Empty(changed);

        var morning = evening.AddMinutes(6);
        viewModel.Tick(morning);

        var dayAndTime = evening.ToString("ddd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal(dayAndTime, line.Time);
        Assert.StartsWith(dayAndTime + ", sound played, finished, Director", line.Sentence, StringComparison.Ordinal);
        Assert.Contains(nameof(ActivityLineViewModel.Time), changed);
        Assert.Contains(nameof(ActivityLineViewModel.Sentence), changed);
    }

    /// <summary>
    /// <strong>The window never opens <c>dashboard.db</c></strong>, by the code path: no type of the window holds a
    /// store, a connection, the data folder or a path to open.
    /// </summary>
    [Theory]
    [InlineData(typeof(ActivityLog))]
    [InlineData(typeof(ActivityViewModel))]
    [InlineData(typeof(ActivityWindowHost))]
    [InlineData(typeof(ActivityWindow))]
    [InlineData(typeof(ActivityWords))]
    public void No_type_of_the_window_can_reach_the_database(Type type)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(fields, field =>
            typeof(IEventStore).IsAssignableFrom(field.FieldType)
            || field.FieldType == typeof(EventArchive)
            || field.FieldType.FullName?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true
            || field.FieldType == typeof(ClaudeDashboard.App.Configuration.DashboardPaths));
    }

    /// <summary>
    /// AppHost joins the recorder to the log from the first event, attaches the view model to the tick with the
    /// main window's, and builds the window's host. Built, never started: nothing is read.
    /// </summary>
    [Fact]
    public void AppHost_joins_the_recorder_to_the_log()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var host = ClaudeDashboard.App.Hosting.AppHost.Build(
                new ClaudeDashboard.App.Configuration.DashboardPaths(root),
                claude: new ClaudeDashboard.App.Configuration.ClaudeCodePaths(System.IO.Path.Combine(root, "claude-config")));

            var services = host.Services;
            var recorder = (ClaudeDashboard.App.Pipeline.DecisionRecorder)services.GetService(typeof(ClaudeDashboard.App.Pipeline.DecisionRecorder))!;

            Assert.Same(services.GetService(typeof(ActivityLog)), recorder.Decided!.Target);

            ClaudeDashboard.App.Hosting.AppHost.AttachWindow(services, (MainViewModel)services.GetService(typeof(MainViewModel))!);

            Assert.Contains(services.GetService(typeof(ActivityViewModel)), ((UiTick)services.GetService(typeof(UiTick))!).Targets.Cast<object>());
            Assert.NotNull(services.GetService(typeof(ActivityWindowHost)));

            (services.GetService(typeof(Serilog.ILogger)) as IDisposable)?.Dispose();
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // Disposable temp folder.
            }
        }
    }

    private static Decision Played(string title) =>
        new(At, "s-1", DecisionKind.NoticePlayed, Reason: "finished") { SessionTitle = title };
}
