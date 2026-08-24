using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class GroupTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly GroupKey Key = new(@"C:\projects\dashboard");

    private static Session Member(
        string id,
        SessionState state,
        DateTimeOffset? lastActivity = null) => new()
        {
            Id = new SessionId(id),
            State = state,
            Latest = new Exchange { Prompt = "p", StartedAt = At },
            Cwd = Key.Value,
            Group = Key,
            EnteredAt = At,
            LastActivity = lastActivity ?? At,
        };

    [Fact]
    public void Constructs_over_its_members()
    {
        var group = new Group(Key, [Member("s-1", SessionState.Working)]);

        Assert.Equal(Key, group.Key);
        Assert.Single(group.Members);
        Assert.Equal(new SessionId("s-1"), group.Members[0].Id);
    }

    [Fact]
    public void Preserves_the_member_order_it_is_given()
    {
        var group = new Group(Key, [
            Member("s-1", SessionState.Working),
            Member("s-2", SessionState.Unread),
            Member("s-3", SessionState.Acked),
        ]);

        Assert.Equal(
            [new SessionId("s-1"), new SessionId("s-2"), new SessionId("s-3")],
            group.Members.Select(m => m.Id));
    }

    /// <summary>A group is derived from its members (TS §IV.3), so a memberless one is meaningless.</summary>
    [Fact]
    public void Rejects_an_empty_membership()
    {
        var thrown = Assert.Throws<ArgumentException>(() => new Group(Key, []));

        Assert.Equal("members", thrown.ParamName);
    }

    [Fact]
    public void Rejects_a_null_membership()
    {
        Assert.Throws<ArgumentNullException>(() => new Group(Key, null!));
    }

    [Fact]
    public void Rejects_a_null_member()
    {
        Assert.Throws<ArgumentNullException>(() => new Group(Key, [null!]));
    }

    [Fact]
    public void Rejects_a_key_that_names_no_group()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            new Group(default, [Member("s-1", SessionState.Working)]));

        Assert.Equal("key", thrown.ParamName);
    }

    /// <summary>
    /// TS §IV.3: group state is the worst member state, ranked
    /// Needs You &gt; Error &gt; Unread &gt; Working &gt; Quiet.
    /// </summary>
    [Theory]
    [InlineData(SessionState.Acked, SessionState.Working, SessionState.Working)]
    [InlineData(SessionState.Working, SessionState.Unread, SessionState.Unread)]
    [InlineData(SessionState.Unread, SessionState.NeedsQuestion, SessionState.NeedsQuestion)]
    [InlineData(SessionState.NeedsQuestion, SessionState.Error, SessionState.Error)]
    [InlineData(SessionState.Error, SessionState.NeedsPermission, SessionState.NeedsPermission)]
    [InlineData(SessionState.Ended, SessionState.Acked, SessionState.Acked)]
    public void Worst_state_ranks_by_TS_IV_3_severity(
        SessionState quieter,
        SessionState louder,
        SessionState expected)
    {
        var quieterFirst = new Group(Key, [Member("s-1", quieter), Member("s-2", louder)]);
        var louderFirst = new Group(Key, [Member("s-1", louder), Member("s-2", quieter)]);

        Assert.Equal(expected, quieterFirst.WorstState);
        Assert.Equal(expected, louderFirst.WorstState);
    }

    [Fact]
    public void Worst_state_of_a_single_member_is_that_members_state()
    {
        Assert.Equal(
            SessionState.NeedsQuestion,
            new Group(Key, [Member("s-1", SessionState.NeedsQuestion)]).WorstState);
    }

    [Fact]
    public void Worst_state_ranks_the_full_severity_order()
    {
        var all = new Group(Key, [
            Member("s-1", SessionState.Ended),
            Member("s-2", SessionState.Acked),
            Member("s-3", SessionState.Working),
            Member("s-4", SessionState.Unread),
            Member("s-5", SessionState.Error),
            Member("s-6", SessionState.NeedsPermission),
        ]);

        Assert.Equal(SessionState.NeedsPermission, all.WorstState);
    }

    /// <summary>
    /// TS §IV.3 ranks the two Needs-You states together and does not order them against each
    /// other, so the tie resolves by member order rather than by an invented preference.
    /// </summary>
    [Fact]
    public void Worst_state_ranks_permission_above_question_regardless_of_member_order()
    {
        var permissionFirst = new Group(Key, [
            Member("s-1", SessionState.NeedsPermission),
            Member("s-2", SessionState.NeedsQuestion),
        ]);
        var questionFirst = new Group(Key, [
            Member("s-1", SessionState.NeedsQuestion),
            Member("s-2", SessionState.NeedsPermission),
        ]);

        Assert.Equal(SessionState.NeedsPermission, permissionFirst.WorstState);
        Assert.Equal(SessionState.NeedsPermission, questionFirst.WorstState);
    }

    /// <summary>
    /// The ratified order is total, so members can only tie when they share a state — and then
    /// the answer is that state whichever is examined first. Member order cannot change a
    /// roll-up.
    /// </summary>
    [Fact]
    public void Worst_state_does_not_depend_on_member_order()
    {
        SessionState[] states =
        [
            SessionState.Ended, SessionState.Acked, SessionState.Working,
            SessionState.Unread, SessionState.NeedsQuestion, SessionState.Error,
        ];

        var forward = new Group(Key, states.Select((s, i) => Member($"s-{i}", s)));
        var reversed = new Group(Key, states.Reverse().Select((s, i) => Member($"s-{i}", s)));

        Assert.Equal(SessionState.Error, forward.WorstState);
        Assert.Equal(SessionState.Error, reversed.WorstState);
    }

    /// <summary>TS §IV.3: "Group recency = most recent member event."</summary>
    [Fact]
    public void Last_activity_is_the_most_recent_member_activity()
    {
        var group = new Group(Key, [
            Member("s-1", SessionState.Working, At),
            Member("s-2", SessionState.Working, At.AddMinutes(9)),
            Member("s-3", SessionState.Working, At.AddMinutes(4)),
        ]);

        Assert.Equal(At.AddMinutes(9), group.LastActivity);
    }

    [Fact]
    public void Last_activity_of_a_single_member_is_that_members_activity()
    {
        var group = new Group(Key, [Member("s-1", SessionState.Working, At.AddHours(2))]);

        Assert.Equal(At.AddHours(2), group.LastActivity);
    }

    [Fact]
    public void Has_value_equality_by_key_and_member_sequence()
    {
        var one = new Group(Key, [Member("s-1", SessionState.Working)]);
        var other = new Group(Key, [Member("s-1", SessionState.Working)]);

        Assert.NotSame(one, other);
        Assert.Equal(one, other);
        Assert.True(one == other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void Distinguishes_groups_that_differ()
    {
        var group = new Group(Key, [Member("s-1", SessionState.Working)]);

        Assert.NotEqual(group, new Group(new GroupKey(@"C:\elsewhere"), [Member("s-1", SessionState.Working)]));
        Assert.NotEqual(group, new Group(Key, [Member("s-2", SessionState.Working)]));
        Assert.NotEqual(group, new Group(Key, [Member("s-1", SessionState.Unread)]));
        Assert.NotEqual(group, new Group(Key, [Member("s-1", SessionState.Working), Member("s-2", SessionState.Working)]));
    }

    [Fact]
    public void Is_order_sensitive()
    {
        var forward = new Group(Key, [Member("s-1", SessionState.Working), Member("s-2", SessionState.Working)]);
        var reversed = new Group(Key, [Member("s-2", SessionState.Working), Member("s-1", SessionState.Working)]);

        Assert.NotEqual(forward, reversed);
    }

    /// <summary>The group copies its membership, so mutating the caller's list cannot reach inside it.</summary>
    [Fact]
    public void Copies_the_membership_it_is_given()
    {
        var members = new List<Session> { Member("s-1", SessionState.Working) };
        var group = new Group(Key, members);

        members.Add(Member("s-2", SessionState.Error));

        Assert.Single(group.Members);
        Assert.Equal(SessionState.Working, group.WorstState);
    }
}
