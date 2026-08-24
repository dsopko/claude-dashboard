using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

public sealed class GroupResolverTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private const string Dashboard = @"C:\projects\dashboard";
    private const string Elsewhere = @"C:\projects\elsewhere";

    private static Session Session(
        string id,
        string cwd = Dashboard,
        SessionState state = SessionState.Working,
        DateTimeOffset? lastActivity = null)
    {
        var sessionId = new SessionId(id);
        return new Session
        {
            Id = sessionId,
            State = state,
            Latest = new Exchange { Prompt = "p", StartedAt = At },
            Cwd = cwd,
            Group = GroupKeys.ForSession(cwd, sessionId),
            EnteredAt = At,
            LastActivity = lastActivity ?? At,
        };
    }

    // ---- Partitioning -------------------------------------------------------------------------

    [Fact]
    public void Sessions_sharing_a_workspace_land_in_one_group()
    {
        var groups = GroupResolver.Resolve([Session("s-1"), Session("s-2"), Session("s-3")]);

        var group = Assert.Single(groups);
        Assert.Equal(GroupKeys.ForWorkspace(Dashboard), group.Key);
        Assert.Equal(3, group.Members.Count);
    }

    [Fact]
    public void Sessions_in_different_workspaces_land_in_different_groups()
    {
        var groups = GroupResolver.Resolve([Session("s-1"), Session("s-2", Elsewhere)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(
            [GroupKeys.ForWorkspace(Dashboard), GroupKeys.ForWorkspace(Elsewhere)],
            groups.Select(g => g.Key).Order(Comparer<GroupKey>.Create(
                (a, b) => string.CompareOrdinal(a.Value, b.Value))));
    }

    /// <summary>
    /// The casing case, end to end through the resolver — the failure that would otherwise
    /// split one workspace into two groups on screen.
    /// </summary>
    [Fact]
    public void Casing_differences_do_not_split_a_workspace_into_two_groups()
    {
        var groups = GroupResolver.Resolve(
        [
            Session("s-1", @"C:\Projects\Claude\dashboard"),
            Session("s-2", @"C:\projects\Claude\dashboard"),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void A_trailing_separator_does_not_split_a_workspace_into_two_groups()
    {
        var groups = GroupResolver.Resolve(
        [
            Session("s-1", @"C:\projects\dashboard"),
            Session("s-2", @"C:\projects\dashboard\"),
        ]);

        Assert.Single(groups);
    }

    /// <summary>
    /// The third normalization case, at the level where its failure is actually visible: two
    /// spellings of one directory would show as two entirely legitimate-looking groups.
    /// </summary>
    [Fact]
    public void Separator_spelling_does_not_split_a_workspace_into_two_groups()
    {
        var groups = GroupResolver.Resolve(
        [
            Session("s-1", @"C:\projects\dashboard"),
            Session("s-2", "C:/projects/dashboard"),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void Sessions_without_a_workspace_each_group_alone()
    {
        var groups = GroupResolver.Resolve(
        [
            Session("s-1", string.Empty),
            Session("s-2", string.Empty),
            Session("s-3"),
        ]);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Members));
    }

    [Fact]
    public void No_sessions_derive_no_groups()
    {
        Assert.Empty(GroupResolver.Resolve([]));
    }

    /// <summary>
    /// A group is derived from its members (TS §IV.3), so it exists exactly as long as one
    /// does — there is no such thing as an empty group to remove.
    /// </summary>
    [Fact]
    public void A_group_never_comes_back_empty()
    {
        var groups = GroupResolver.Resolve([Session("s-1"), Session("s-2", Elsewhere)]);

        Assert.All(groups, g => Assert.NotEmpty(g.Members));
    }

    // ---- Roll-up, as consumed through the resolver ------------------------------------------------

    /// <summary>
    /// The ranking itself is tested on <see cref="Group"/> (T1.1); this proves the resolver
    /// puts the right members in the right group for it to rank.
    /// </summary>
    [Fact]
    public void A_groups_worst_state_reflects_its_own_members_only()
    {
        var groups = GroupResolver.Resolve(
        [
            Session("s-1", Dashboard, SessionState.Working),
            Session("s-2", Dashboard, SessionState.NeedsPermission),
            Session("s-3", Elsewhere, SessionState.Acked),
        ]);

        var dashboard = groups.Single(g => g.Key == GroupKeys.ForWorkspace(Dashboard));
        var elsewhere = groups.Single(g => g.Key == GroupKeys.ForWorkspace(Elsewhere));

        Assert.Equal(SessionState.NeedsPermission, dashboard.WorstState);
        Assert.Equal(SessionState.Acked, elsewhere.WorstState);
    }

    [Fact]
    public void A_groups_recency_reflects_its_own_members_only()
    {
        var groups = GroupResolver.Resolve(
        [
            Session("s-1", Dashboard, lastActivity: At),
            Session("s-2", Dashboard, lastActivity: At.AddMinutes(9)),
            Session("s-3", Elsewhere, lastActivity: At.AddHours(3)),
        ]);

        Assert.Equal(
            At.AddMinutes(9),
            groups.Single(g => g.Key == GroupKeys.ForWorkspace(Dashboard)).LastActivity);
        Assert.Equal(
            At.AddHours(3),
            groups.Single(g => g.Key == GroupKeys.ForWorkspace(Elsewhere)).LastActivity);
    }

    // ---- Re-grouping on a cwd change ---------------------------------------------------------------

    /// <summary>
    /// TS §IV.3: the key is "re-derived on directory-change events, not fixed at session
    /// start". Nothing tracks membership — the session simply carries a different key.
    /// </summary>
    [Fact]
    public void A_session_that_moves_leaves_its_old_group_and_joins_the_new_one()
    {
        var before = GroupResolver.Resolve([Session("s-1"), Session("s-2"), Session("s-3", Elsewhere)]);
        Assert.Equal(2, before.Single(g => g.Key == GroupKeys.ForWorkspace(Dashboard)).Members.Count);

        var after = GroupResolver.Resolve([Session("s-1"), Session("s-2", Elsewhere), Session("s-3", Elsewhere)]);

        Assert.Single(after.Single(g => g.Key == GroupKeys.ForWorkspace(Dashboard)).Members);
        Assert.Equal(2, after.Single(g => g.Key == GroupKeys.ForWorkspace(Elsewhere)).Members.Count);
    }

    /// <summary>The group of a sole member goes with it.</summary>
    [Fact]
    public void A_sole_member_moving_away_takes_its_group_with_it()
    {
        var before = GroupResolver.Resolve([Session("s-1", Elsewhere), Session("s-2")]);
        Assert.Equal(2, before.Count);

        var after = GroupResolver.Resolve([Session("s-1"), Session("s-2")]);

        var group = Assert.Single(after);
        Assert.Equal(GroupKeys.ForWorkspace(Dashboard), group.Key);
        Assert.DoesNotContain(after, g => g.Key == GroupKeys.ForWorkspace(Elsewhere));
    }

    [Fact]
    public void A_session_moving_into_a_new_directory_forms_a_new_group()
    {
        var after = GroupResolver.Resolve([Session("s-1", @"C:\projects\third"), Session("s-2")]);

        Assert.Equal(2, after.Count);
        Assert.Contains(after, g => g.Key == GroupKeys.ForWorkspace(@"C:\projects\third"));
    }

    /// <summary>A session that loses its workspace stops sharing a group with its old peers.</summary>
    [Fact]
    public void A_session_that_loses_its_workspace_groups_alone()
    {
        var after = GroupResolver.Resolve([Session("s-1", string.Empty), Session("s-2")]);

        Assert.Equal(2, after.Count);
        Assert.Equal(
            GroupKeyKind.Session,
            GroupKeys.KindOf(after.Single(g => g.Members[0].Id == new SessionId("s-1")).Key));
    }

    // ---- Determinism ---------------------------------------------------------------------------------

    /// <summary>
    /// A pure function has to give the same answer for the same sessions however the caller
    /// happened to enumerate them — the Registry hands over dictionary order, which is not a
    /// guaranteed order.
    /// </summary>
    [Fact]
    public void The_result_does_not_depend_on_the_order_sessions_arrive_in()
    {
        Session[] sessions =
        [
            Session("s-3", Elsewhere), Session("s-1"), Session("s-2"),
        ];

        var forward = GroupResolver.Resolve(sessions);
        var reversed = GroupResolver.Resolve(sessions.Reverse());

        Assert.Equal(forward, reversed);
    }

    /// <summary>
    /// <see cref="Group.WorstState"/> breaks an equal-severity tie by member order, so member
    /// order has to be deterministic or the roll-up flips between runs.
    /// </summary>
    [Fact]
    public void An_equal_severity_tie_resolves_the_same_way_regardless_of_input_order()
    {
        Session[] sessions =
        [
            Session("s-1", Dashboard, SessionState.NeedsPermission),
            Session("s-2", Dashboard, SessionState.NeedsQuestion),
        ];

        Assert.Equal(
            GroupResolver.Resolve(sessions).Single().WorstState,
            GroupResolver.Resolve(sessions.Reverse()).Single().WorstState);
    }

    /// <summary>
    /// The payoff of <see cref="Group"/>'s value equality: a caller can compare successive
    /// results to avoid churning a bound collection when nothing actually moved.
    /// </summary>
    [Fact]
    public void An_unchanged_group_compares_equal_to_the_one_it_replaces()
    {
        Assert.Equal(
            GroupResolver.Resolve([Session("s-1"), Session("s-2")]),
            GroupResolver.Resolve([Session("s-1"), Session("s-2")]));
    }

    [Fact]
    public void A_changed_group_does_not_compare_equal()
    {
        Assert.NotEqual(
            GroupResolver.Resolve([Session("s-1", Dashboard, SessionState.Working)]),
            GroupResolver.Resolve([Session("s-1", Dashboard, SessionState.Unread)]));
    }

    // ---- Validation -------------------------------------------------------------------------------------

    [Fact]
    public void A_null_collection_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => GroupResolver.Resolve(null!));
    }

    [Fact]
    public void A_null_session_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => GroupResolver.Resolve([Session("s-1"), null!]));
    }
}
