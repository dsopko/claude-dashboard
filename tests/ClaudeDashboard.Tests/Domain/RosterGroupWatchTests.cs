using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The watch: settle edges, and the self-monitor that says the settle window was too short
/// (T1.25, issue #16).
/// </summary>
/// <remarks>
/// 1.5 seconds is the operator's starting value, not a measured one. The mis-mark warning is the
/// instrument that will tell anyone whether it holds, so these tests are as much about the
/// instrument as about the feature.
/// </remarks>
public sealed class RosterGroupWatchTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly GroupKey Key = GroupKeys.ForRoster("orchestration");

    /// <summary>A group settles once, on the edge, not repeatedly while it stays settled.</summary>
    [Fact]
    public void A_group_settles_once_and_stays_settled_quietly()
    {
        var watch = new RosterGroupWatch();

        Assert.Empty(watch.Observe([Quiet(At)], At));

        var settled = watch.Observe([Quiet(At)], At + RosterSettle.DefaultWindow);
        Assert.Equal(RosterGroupEvent.Settled, Assert.Single(settled).Event);

        Assert.Empty(watch.Observe([Quiet(At)], At + TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// <strong>Finished, then working again within five seconds, writes exactly one mis-mark.</strong>
    /// </summary>
    /// <remarks>
    /// Exactly one, and it is reported alongside the unsettle rather than instead of it: the sound
    /// engine still has to be told the group is no longer done, and a caller that treated the
    /// mis-mark as the whole story would leave the group nudging for a result that no longer
    /// exists.
    /// </remarks>
    [Fact]
    public void A_settle_undone_within_five_seconds_is_reported_once()
    {
        var watch = new RosterGroupWatch();

        watch.Observe([Quiet(At)], At + RosterSettle.DefaultWindow);

        var changes = watch.Observe([Working(At)], At + TimeSpan.FromSeconds(3));

        Assert.Equal(
            [RosterGroupEvent.Unsettled, RosterGroupEvent.MisMarked],
            changes.Select(change => change.Event).ToArray());

        // Still working: nothing new to say, and no heartbeat.
        Assert.Empty(watch.Observe([Working(At)], At + TimeSpan.FromSeconds(4)));
    }

    /// <summary>
    /// <strong>A group that goes back to work AFTER five seconds is not a mis-mark.</strong>
    /// </summary>
    /// <remarks>
    /// The control that makes the test above mean something. Without it, a monitor that reported
    /// every unsettle as a mis-mark would pass — and the log would then say the window was too
    /// short every time an operator sent the next prompt, which is the ordinary case and would bury
    /// the real signal.
    /// </remarks>
    [Fact]
    public void A_settle_undone_later_is_not_a_mis_mark()
    {
        var watch = new RosterGroupWatch();

        watch.Observe([Quiet(At)], At + RosterSettle.DefaultWindow);

        var changes = watch.Observe([Working(At)], At + TimeSpan.FromSeconds(30));

        Assert.Equal(RosterGroupEvent.Unsettled, Assert.Single(changes).Event);
    }

    /// <summary>A flapping group reports one mis-mark per flap, and cannot report two per settle.</summary>
    /// <remarks>
    /// The storm answer, asserted rather than argued: the group has to settle again before it can
    /// mis-mark again, so the rate is bounded below by the settle window itself. A second
    /// suppressor on top would only hide how often this is really happening, which is the one thing
    /// the log exists to measure.
    /// </remarks>
    [Fact]
    public void A_flapping_group_reports_one_mis_mark_per_flap()
    {
        var watch = new RosterGroupWatch();
        var marks = 0;

        for (var flap = 0; flap < 3; flap++)
        {
            var quietAt = At + TimeSpan.FromSeconds(10 * flap);

            watch.Observe([Quiet(quietAt)], quietAt + RosterSettle.DefaultWindow);
            marks += watch.Observe([Working(quietAt)], quietAt + TimeSpan.FromSeconds(2))
                .Count(change => change.Event == RosterGroupEvent.MisMarked);
        }

        Assert.Equal(3, marks);
    }

    /// <summary>A group that disappears stops being settled, so it stops nudging.</summary>
    /// <remarks>
    /// Its last member ended, or its roster was deleted. Leaving it settled would go on nudging for
    /// a group that no longer exists — and nothing would ever unsettle it, because it will never be
    /// observed again.
    /// </remarks>
    [Fact]
    public void A_group_that_disappears_is_unsettled()
    {
        var watch = new RosterGroupWatch();

        watch.Observe([Quiet(At)], At + RosterSettle.DefaultWindow);

        var changes = watch.Observe([], At + TimeSpan.FromSeconds(30));

        Assert.Equal(RosterGroupEvent.Unsettled, Assert.Single(changes).Event);
        Assert.Equal(0, watch.Following);
    }

    /// <summary>A workspace group is not watched at all.</summary>
    [Fact]
    public void A_workspace_group_is_not_watched()
    {
        var watch = new RosterGroupWatch();

        var group = new Group(
            GroupKeys.ForWorkspace(@"C:\w"),
            [Member("s-1", SessionState.Unread, At)]);

        Assert.Empty(watch.Observe([group], At + TimeSpan.FromMinutes(5)));
        Assert.Equal(0, watch.Following);
    }

    /// <summary>The deadline is the earliest pending one, and null when none is pending.</summary>
    [Fact]
    public void The_next_deadline_is_the_earliest_pending_one()
    {
        var watch = new RosterGroupWatch();

        var early = new Group(GroupKeys.ForRoster("a"), [Member("s-1", SessionState.Unread, At)]);
        var late = new Group(
            GroupKeys.ForRoster("b"),
            [Member("s-2", SessionState.Unread, At + TimeSpan.FromSeconds(5))]);

        Assert.Equal(At + RosterSettle.DefaultWindow, watch.NextDeadline([early, late], At));
        Assert.Null(watch.NextDeadline([], At));

        // …and once both windows have elapsed nothing is pending, which is what keeps the host on
        // its ordinary tick instead of re-arming on an instant already in the past.
        Assert.Null(watch.NextDeadline([early, late], At + TimeSpan.FromMinutes(1)));
    }

    /// <summary>The windows are injectable, so a test never waits on a real one.</summary>
    [Fact]
    public void The_windows_are_injectable()
    {
        var watch = new RosterGroupWatch(window: TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), watch.Window);
        Assert.Empty(watch.Observe([Quiet(At)], At + TimeSpan.FromSeconds(30)));
        Assert.Single(watch.Observe([Quiet(At)], At + TimeSpan.FromMinutes(1)));
    }

    private static Group Quiet(DateTimeOffset entered) =>
        new(Key, [Member("s-1", SessionState.Unread, entered)]);

    private static Group Working(DateTimeOffset entered) =>
        new(Key, [Member("s-1", SessionState.Working, entered)]);

    private static Session Member(string id, SessionState state, DateTimeOffset entered) => new()
    {
        Id = new SessionId(id),
        State = state,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = entered },
        Cwd = @"C:\w",
        WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\w"),
        EnteredAt = entered,
        LastActivity = entered,
        LastHeardAt = entered,
        Title = "Director",
    };
}
