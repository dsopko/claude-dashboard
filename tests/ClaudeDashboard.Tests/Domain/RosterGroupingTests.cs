using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Roster grouping: who is in a group, what the group reads, and the settle window
/// (T1.25, issue #16).
/// </summary>
/// <remarks>
/// <strong>Nothing here sleeps.</strong> The settle window is driven by handing
/// <see cref="RosterSettle.StateOf"/> an instant, so a test about a second and a half costs
/// nothing and cannot be flaky. That is the same discipline T1.22 used for its strike window.
/// </remarks>
public sealed class RosterGroupingTests
{
    private const string Orchestration = "orchestration";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly RosterBook Book =
        RosterBook.From([(Orchestration, ["Director", "Coder", "Reviewer"])]);

    // ---- Membership ---------------------------------------------------------------------------

    /// <summary>
    /// <strong>A rostered session is grouped by its roster, and not by its workspace.</strong>
    /// </summary>
    /// <remarks>
    /// The two members here are in <em>different</em> directories on purpose. Gathering sessions
    /// that <c>cwd</c> scatters is the entire point of a roster, so a test whose members shared a
    /// workspace would pass just as happily if the roster did nothing at all.
    /// </remarks>
    [Fact]
    public void A_rostered_session_is_grouped_by_its_roster_not_its_workspace()
    {
        var groups = GroupResolver.Resolve(
            [
                Member("s-1", "Director", SessionState.Working, cwd: @"C:\a"),
                Member("s-2", "Coder", SessionState.Working, cwd: @"C:\b"),
            ],
            Book);

        var group = Assert.Single(groups);

        Assert.Equal(GroupKeys.ForRoster(Orchestration), group.Key);
        Assert.Equal(GroupKeyKind.Roster, GroupKeys.KindOf(group.Key));
        Assert.Equal(2, group.Members.Count);
    }

    /// <summary>A session in no roster is grouped exactly as it was before rosters existed.</summary>
    [Fact]
    public void A_session_in_no_roster_is_unaffected()
    {
        var stranger = Member("s-3", "Somebody else", SessionState.Working, cwd: @"C:\c");

        var withRosters = GroupResolver.Resolve([stranger], Book);
        var without = GroupResolver.Resolve([stranger]);

        Assert.Equal(stranger.WorkspaceGroup, Assert.Single(withRosters).Key);
        Assert.Equal(without, withRosters);
    }

    /// <summary>A rename moves a session in and out with no restart.</summary>
    /// <remarks>
    /// Membership follows the session's <em>current</em> title, so this is just two resolves over
    /// the same session record with a different title — which is exactly what the running
    /// application does when a title latches.
    /// </remarks>
    [Fact]
    public void A_rename_moves_a_session_in_and_out()
    {
        var outsider = Member("s-1", "Stranger", SessionState.Working);

        Assert.Equal(outsider.WorkspaceGroup, Assert.Single(GroupResolver.Resolve([outsider], Book)).Key);

        var renamedIn = outsider with { Title = "Director" };
        Assert.Equal(GroupKeys.ForRoster(Orchestration), Assert.Single(GroupResolver.Resolve([renamedIn], Book)).Key);

        var renamedOut = renamedIn with { Title = "Stranger again" };
        Assert.Equal(renamedOut.WorkspaceGroup, Assert.Single(GroupResolver.Resolve([renamedOut], Book)).Key);
    }

    /// <summary>Two live sessions sharing a rostered name both join, each keeping its own row.</summary>
    /// <remarks>
    /// #16's accepted consequence rather than a defect: the dashboard cannot tell them apart by
    /// name because there is nothing to tell apart. Asserted so that a future "fix" has to argue
    /// with a test rather than with a comment.
    /// </remarks>
    [Fact]
    public void Two_sessions_sharing_a_rostered_name_both_join()
    {
        var groups = GroupResolver.Resolve(
            [
                Member("s-1", "Coder", SessionState.Working),
                Member("s-2", "Coder", SessionState.Unread),
            ],
            Book);

        Assert.Equal(2, Assert.Single(groups).Members.Count);
    }

    /// <summary>Editing a roster regroups live sessions on the next resolve.</summary>
    [Fact]
    public void Editing_a_roster_regroups_live_sessions()
    {
        var director = Member("s-1", "Director", SessionState.Working);

        var moved = Book.With("docs", ["Director"]);

        Assert.Equal(GroupKeys.ForRoster("docs"), Assert.Single(GroupResolver.Resolve([director], moved)).Key);
    }

    // ---- The roll-up --------------------------------------------------------------------------

    /// <summary>
    /// <strong>In a roster group, Working outranks Finished — and in a workspace group it does
    /// not.</strong>
    /// </summary>
    /// <remarks>
    /// Both halves in one test because they are one decision. The workspace half is what makes the
    /// roster half meaningful: without it, this could pass because the ranking changed everywhere,
    /// which would be a different and much larger change than #16 asked for.
    /// </remarks>
    [Fact]
    public void Working_outranks_finished_in_a_roster_group_only()
    {
        var members = new[]
        {
            Member("s-1", "Director", SessionState.Unread),
            Member("s-2", "Coder", SessionState.Working),
        };

        var roster = Assert.Single(GroupResolver.Resolve(members, Book));
        var workspace = Assert.Single(GroupResolver.Resolve(members, RosterBook.Empty));

        Assert.Equal(SessionState.Working, roster.WorstState);
        Assert.Equal(SessionState.Unread, workspace.WorstState);
    }

    /// <summary>The Needs-You states keep their existing order above both.</summary>
    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.Error)]
    [InlineData(SessionState.NeedsQuestion)]
    public void A_needs_you_member_still_outranks_working(SessionState needsYou)
    {
        var group = Assert.Single(GroupResolver.Resolve(
            [
                Member("s-1", "Director", SessionState.Working),
                Member("s-2", "Coder", needsYou),
            ],
            Book));

        Assert.Equal(needsYou, group.WorstState);
    }

    /// <summary>The roll-up does not depend on the order the members arrive in.</summary>
    [Fact]
    public void The_roster_roll_up_is_order_independent()
    {
        var members = new[]
        {
            Member("s-1", "Director", SessionState.Unread),
            Member("s-2", "Coder", SessionState.Working),
        };

        Assert.Equal(
            Assert.Single(GroupResolver.Resolve(members, Book)).WorstState,
            Assert.Single(GroupResolver.Resolve(members.Reverse(), Book)).WorstState);
    }

    // ---- The settle window --------------------------------------------------------------------

    /// <summary>
    /// <strong>All members quiet ⇒ Finished after 1.5 seconds, and not a moment before.</strong>
    /// </summary>
    /// <remarks>
    /// The "before" assertion is the load-bearing one. A settle window that fired immediately would
    /// satisfy "it eventually reads finished" while doing nothing whatever, and the blip it fails
    /// to prevent is the false done chime this whole feature exists to remove.
    /// </remarks>
    [Fact]
    public void A_quiet_roster_group_reads_finished_only_after_the_settle_window()
    {
        var group = Quiet(At);

        Assert.Equal(SessionState.Working, RosterSettle.StateOf(group, At));
        Assert.Equal(SessionState.Working, RosterSettle.StateOf(group, At + TimeSpan.FromSeconds(1.4)));
        Assert.Equal(SessionState.Unread, RosterSettle.StateOf(group, At + RosterSettle.DefaultWindow));
    }

    /// <summary>The window is measured from the LAST member to stop, not the first.</summary>
    /// <remarks>
    /// Measuring from the earliest would let a group settle while the most recent hand-off was
    /// still inside its window — which is the same failure as having no window, arriving late.
    /// </remarks>
    [Fact]
    public void The_window_runs_from_the_last_member_to_stop()
    {
        var group = new Group(
            GroupKeys.ForRoster(Orchestration),
            [
                Member("s-1", "Director", SessionState.Unread, entered: At),
                Member("s-2", "Coder", SessionState.Unread, entered: At + TimeSpan.FromSeconds(10)),
            ]);

        Assert.Equal(SessionState.Working, RosterSettle.StateOf(group, At + TimeSpan.FromSeconds(11)));
        Assert.Equal(SessionState.Unread, RosterSettle.StateOf(group, At + TimeSpan.FromSeconds(11.5)));
    }

    /// <summary>
    /// <strong>Director stops, coder starts one second later: the group never leaves Working.</strong>
    /// </summary>
    /// <remarks>
    /// #16's motivating scenario, asserted at every instant that matters rather than only at the
    /// end — the defect being prevented is a blip, so a test that looked only at the final state
    /// would miss it entirely.
    /// </remarks>
    [Fact]
    public void A_hand_off_inside_the_window_never_reads_finished()
    {
        var stopped = Quiet(At);

        Assert.Equal(SessionState.Working, RosterSettle.StateOf(stopped, At + TimeSpan.FromSeconds(0.5)));
        Assert.Equal(SessionState.Working, RosterSettle.StateOf(stopped, At + TimeSpan.FromSeconds(1)));

        var resumed = new Group(
            GroupKeys.ForRoster(Orchestration),
            [
                Member("s-1", "Director", SessionState.Unread, entered: At),
                Member("s-2", "Coder", SessionState.Working, entered: At + TimeSpan.FromSeconds(1)),
            ]);

        Assert.Equal(SessionState.Working, RosterSettle.StateOf(resumed, At + TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionState.Working, RosterSettle.StateOf(resumed, At + TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// <strong>The reverse arrival order reaches the same end state.</strong>
    /// </summary>
    /// <remarks>
    /// Asserted explicitly because arrival order is not guaranteed: events are stamped when they
    /// reach ingress, not when they happened, so which of the director's stop and the coder's start
    /// lands first is a race between two HTTP posts (TS §II.4). A design that only worked in one
    /// order would fail roughly half the time, in a way that looked like flakiness.
    /// </remarks>
    [Fact]
    public void The_reverse_arrival_order_reaches_the_same_state()
    {
        var coderFirst = new Group(
            GroupKeys.ForRoster(Orchestration),
            [
                Member("s-2", "Coder", SessionState.Working, entered: At),
                Member("s-1", "Director", SessionState.Unread, entered: At + TimeSpan.FromSeconds(1)),
            ]);

        Assert.Equal(SessionState.Working, RosterSettle.StateOf(coderFirst, At + TimeSpan.FromSeconds(30)));
    }

    /// <summary>A group quiet for hours is not held at Working — the window never applied to it.</summary>
    /// <remarks>
    /// Its roll-up is Acked rather than Unread, so there is nothing to hold back. Worth asserting
    /// because a settle implemented as "hold anything quiet at Working" would keep an acknowledged
    /// group looking busy for ever.
    /// </remarks>
    [Fact]
    public void An_acknowledged_group_is_not_held_at_working()
    {
        var group = new Group(
            GroupKeys.ForRoster(Orchestration),
            [Member("s-1", "Director", SessionState.Acked, entered: At)]);

        Assert.Equal(SessionState.Acked, RosterSettle.StateOf(group, At));
    }

    /// <summary>A workspace group never waits: the settle belongs to rosters alone.</summary>
    [Fact]
    public void A_workspace_group_is_never_held_back()
    {
        var group = Assert.Single(GroupResolver.Resolve(
            [Member("s-9", "Stranger", SessionState.Unread, entered: At)],
            RosterBook.Empty));

        Assert.Equal(SessionState.Unread, RosterSettle.StateOf(group, At));
        Assert.Null(RosterSettle.DeadlineOf(group));
    }

    /// <summary>A pending group reports a deadline; anything else reports none.</summary>
    /// <remarks>
    /// This is what lets the host wake once instead of polling, so "null when nothing is pending"
    /// is as load-bearing as the deadline itself: a non-null answer for a settled group would wake
    /// the pipeline for ever.
    /// </remarks>
    [Fact]
    public void Only_a_pending_roster_group_has_a_deadline()
    {
        Assert.Equal(At + RosterSettle.DefaultWindow, RosterSettle.DeadlineOf(Quiet(At)));

        var working = new Group(
            GroupKeys.ForRoster(Orchestration),
            [Member("s-1", "Director", SessionState.Working, entered: At)]);

        Assert.Null(RosterSettle.DeadlineOf(working));
    }

    private static Group Quiet(DateTimeOffset entered) =>
        new(
            GroupKeys.ForRoster(Orchestration),
            [
                Member("s-1", "Director", SessionState.Unread, entered: entered),
                Member("s-2", "Coder", SessionState.Acked, entered: entered - TimeSpan.FromMinutes(1)),
            ]);

    private static Session Member(
        string id,
        string title,
        SessionState state,
        string cwd = @"C:\w",
        DateTimeOffset? entered = null) =>
        new()
        {
            Id = new SessionId(id),
            State = state,
            Latest = new Exchange { Prompt = "run the tests", StartedAt = entered ?? At },
            Cwd = cwd,
            WorkspaceGroup = GroupKeys.ForWorkspace(cwd),
            EnteredAt = entered ?? At,
            LastActivity = entered ?? At,
            Title = title,
        };
}
