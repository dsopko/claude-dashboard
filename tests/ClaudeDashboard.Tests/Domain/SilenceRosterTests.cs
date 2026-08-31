using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// What a roster group reads when one of its members falls silent (issue #28, issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two tasks apart from where the state was added, which is why it is its own file.</strong>
/// A roster group rolls up to its most urgent member under a <em>swapped</em> severity order —
/// <see cref="SessionState.Working"/> outranks <see cref="SessionState.Unread"/> there, because
/// one member finishing while another works is a hand-off in flight. Adding a state anywhere near
/// those two changes what a group says.
/// </para>
/// <para>
/// <strong>The placement is what makes these answers hold.</strong>
/// <see cref="SessionState.Interrupted"/> ranks below both halves of the swap, so it is unaffected
/// by the swap and can tie with neither. Placed between them it would have tied with
/// <c>Unread</c> under the roster order — and since <see cref="RosterSettle.PendingDeadlineOf"/>
/// returns null unless the roll-up is exactly <c>Unread</c>, the tie would have decided whether
/// the group settles and chimes at all. Visible only as a group that does or does not make a
/// sound. <see cref="A_group_of_one_interrupted_and_one_finished_member_still_settles"/> is the
/// test that would go red.
/// </para>
/// </remarks>
public sealed class SilenceRosterTests
{
    private const string Orchestration = "orchestration";
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>
    /// <strong>One member interrupted while another works: the group still reads Working.</strong>
    /// </summary>
    /// <remarks>
    /// The hand-off is live. A director that stopped talking while the coder builds has not
    /// finished the piece of work the roster names, and reading the group as anything quieter
    /// would be the false reading the whole of issue #16 exists to remove — arriving by a new
    /// route.
    /// </remarks>
    [Fact]
    public void One_interrupted_member_does_not_quieten_a_group_that_is_still_working()
    {
        var group = Roster(
            Member("s-1", SessionState.Interrupted, At),
            Member("s-2", SessionState.Working, At));

        Assert.Equal(SessionState.Working, group.WorstState);
        Assert.Equal(SessionState.Working, RosterSettle.StateOf(group, At + TimeSpan.FromMinutes(1)));
    }

    /// <summary>A group whose members have all fallen silent reads interrupted.</summary>
    /// <remarks>
    /// Truthful: every member has stopped talking, and none of them finished. It is not
    /// <c>Unread</c>, so the settle window never applies — the group is not waiting to be read as
    /// finished, because nothing finished.
    /// </remarks>
    [Fact]
    public void A_group_of_silent_members_reads_interrupted_and_never_settles()
    {
        var group = Roster(
            Member("s-1", SessionState.Interrupted, At),
            Member("s-2", SessionState.Interrupted, At));

        Assert.Equal(SessionState.Interrupted, group.WorstState);
        Assert.Equal(SessionState.Interrupted, RosterSettle.StateOf(group, At + TimeSpan.FromMinutes(1)));
        Assert.Null(RosterSettle.PendingDeadlineOf(group, At + TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// <strong>Finished beats interrupted, so the group settles and the chime still happens.</strong>
    /// </summary>
    /// <remarks>
    /// The tie that was avoided. Under the roster order <c>Unread</c> takes the rank <c>Working</c>
    /// vacated; had <c>Interrupted</c> been placed between them the two would rank equally, and
    /// <see cref="AttentionOrder.WorstOf"/> — a reduction over those ranks — would decide by
    /// accident whether this group's roll-up is <c>Unread</c>. Only an <c>Unread</c> roll-up gets a
    /// settle deadline, so the accident would be a finished chime that fires or does not.
    /// </remarks>
    [Fact]
    public void A_group_of_one_interrupted_and_one_finished_member_still_settles()
    {
        var group = Roster(
            Member("s-1", SessionState.Interrupted, At),
            Member("s-2", SessionState.Unread, At));

        Assert.Equal(SessionState.Unread, group.WorstState);

        // Inside the window it is held at Working; past it, it reads finished.
        Assert.Equal(SessionState.Working, RosterSettle.StateOf(group, At + TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionState.Unread, RosterSettle.StateOf(group, At + TimeSpan.FromSeconds(2)));
        Assert.NotNull(RosterSettle.PendingDeadlineOf(group, At + TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// <strong>A member going silent restarts the settle window, and that is accepted.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window measures from the latest <see cref="Session.EnteredAt"/> across every member,
    /// and entering <c>Interrupted</c> advances that stamp. So the window restarts although
    /// <em>nothing arrived and nobody did anything</em> — an inference restarting a window designed
    /// to be restarted by observations.
    /// </para>
    /// <para>
    /// The cost is one settle window of delay on a finished chime, once, and it is not
    /// special-cased: excluding it would require <c>QuietSince</c> to know <em>why</em> a state
    /// changed, which is a worse thing to teach it. Asserted rather than left to be discovered,
    /// because a delayed chime with no explanation is exactly the kind of thing that gets debugged
    /// twice.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_member_going_silent_restarts_the_settle_window()
    {
        var finishedAt = At;
        var silencedAt = At + TimeSpan.FromMinutes(5);

        var group = Roster(
            Member("s-1", SessionState.Unread, finishedAt),
            Member("s-2", SessionState.Interrupted, silencedAt));

        // Without the restart this would have settled 1.5 s after `finishedAt`, long past.
        Assert.Equal(SessionState.Working, RosterSettle.StateOf(group, silencedAt + TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionState.Unread, RosterSettle.StateOf(group, silencedAt + TimeSpan.FromSeconds(2)));
    }

    /// <summary>A workspace group is unaffected: there, finished still outranks working.</summary>
    /// <remarks>
    /// The control for the roster order. Interrupted sits below both in either order, so the only
    /// thing that changes between them is what it loses to.
    /// </remarks>
    [Fact]
    public void A_workspace_group_ranks_the_same_states_the_same_way()
    {
        var group = new Group(
            GroupKeys.ForWorkspace(@"C:\projects\dashboard"),
            [
                Member("s-1", SessionState.Interrupted, At),
                Member("s-2", SessionState.Working, At),
            ]);

        Assert.Equal(SessionState.Working, group.WorstState);
        Assert.Null(RosterSettle.PendingDeadlineOf(group, At + TimeSpan.FromMinutes(1)));
    }

    private static Group Roster(params Session[] members) =>
        new(GroupKeys.ForRoster(Orchestration), members);

    private static Session Member(string id, SessionState state, DateTimeOffset entered) => new()
    {
        Id = new SessionId(id),
        State = state,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = entered },
        Cwd = @"C:\projects\dashboard",
        WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\projects\dashboard"),
        EnteredAt = entered,
        LastActivity = entered,
        LastHeardAt = entered,
    };
}
