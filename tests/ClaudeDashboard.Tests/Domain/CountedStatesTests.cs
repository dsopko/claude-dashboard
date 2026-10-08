using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// A roster group counts once, at its roll-up; a session in no roster counts on its own (T1.83, issue #130).
/// </summary>
/// <remarks>
/// <para>
/// The orchestration here is the issue's: three members in three folders, handing work on, so that at any moment one
/// works and the other two have finished their turn. Its heading reads Working; the counts read it the same way.
/// </para>
/// <para>
/// Each case asserts the counts through <see cref="StatusSummary"/> (the tray's kinds) and the bands
/// (<see cref="AttentionOrder.BandOf"/>, as the strip and <c>/state</c> count them), so a reader that bypassed the list
/// would disagree with one of the two.
/// </para>
/// </remarks>
public sealed class CountedStatesTests
{
    private const string Orchestration = "orchestration";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private static readonly RosterBook Book =
        RosterBook.From([(Orchestration, ["Director", "Coder", "Reviewer"])]);

    /// <summary>One member working and two finished: <c>1 working</c>, <c>0 unread</c>, and the light is Working.</summary>
    [Fact]
    public void A_working_orchestration_counts_one_working_and_no_unread()
    {
        var sessions = Orchestrating(SessionState.Unread, SessionState.Unread, SessionState.Working);

        var counted = CountedStates.Of(sessions, Book, At.AddMinutes(10));
        var summary = StatusSummary.OfCounted(counted);

        var entry = Assert.Single(counted);
        Assert.Equal(SessionState.Working, entry.State);
        Assert.Equal(new SessionId("s-3"), entry.Session);

        Assert.Equal(1, summary.Working);
        Assert.Equal(0, summary.Unread);
        Assert.Equal(SessionState.Working, summary.Worst);
        Assert.Equal(1, Band(counted, AttentionBand.Working));
        Assert.Equal(0, Band(counted, AttentionBand.Unread));
    }

    /// <summary>
    /// All three quiet for the settle window: <c>1 unread</c>. Inside the window it still reads <c>1 working</c>, from
    /// the window alone, and stands for the member whose finish started it.
    /// </summary>
    [Fact]
    public void A_settled_orchestration_counts_one_unread_and_not_before_the_window()
    {
        var sessions = Orchestrating(SessionState.Unread, SessionState.Unread, SessionState.Unread);
        var lastStop = At.AddMinutes(2);

        var inside = CountedStates.Of(sessions, Book, lastStop + TimeSpan.FromSeconds(1));
        var settled = CountedStates.Of(sessions, Book, lastStop + RosterSettle.DefaultWindow);

        var held = Assert.Single(inside);
        Assert.Equal(SessionState.Working, held.State);
        Assert.Equal(new SessionId("s-3"), held.Session);

        var entry = Assert.Single(settled);
        Assert.Equal(SessionState.Unread, entry.State);

        var summary = StatusSummary.OfCounted(settled);
        Assert.Equal(1, summary.Unread);
        Assert.Equal(0, summary.Working);
        Assert.Equal(1, Band(settled, AttentionBand.Unread));
    }

    /// <summary>All acknowledged: the roster counts in no attention band, as a quiet session does not.</summary>
    [Fact]
    public void An_acknowledged_orchestration_counts_nothing()
    {
        var sessions = Orchestrating(SessionState.Acked, SessionState.Acked, SessionState.Acked);

        var counted = CountedStates.Of(sessions, Book, At.AddMinutes(10));

        Assert.True(StatusSummary.OfCounted(counted).IsAllQuiet);
        Assert.Equal(0, Band(counted, AttentionBand.NeedsYou));
        Assert.Equal(0, Band(counted, AttentionBand.Unread));
        Assert.Equal(0, Band(counted, AttentionBand.Working));
    }

    /// <summary>One member waiting on a permission: the roster is <c>1 need you</c>, and the tray's <c>1 permissions</c>.</summary>
    [Fact]
    public void A_member_waiting_on_a_permission_makes_the_roster_one_need_you()
    {
        var sessions = Orchestrating(SessionState.Unread, SessionState.NeedsPermission, SessionState.Working);

        var counted = CountedStates.Of(sessions, Book, At.AddMinutes(10));
        var summary = StatusSummary.OfCounted(counted);

        var entry = Assert.Single(counted);
        Assert.Equal(SessionState.NeedsPermission, entry.State);
        Assert.Equal(new SessionId("s-2"), entry.Session);

        Assert.Equal(1, summary.Permissions);
        Assert.Equal(0, summary.Unread);
        Assert.Equal(0, summary.Working);
        Assert.Equal(1, Band(counted, AttentionBand.NeedsYou));
        Assert.Equal(0, Band(counted, AttentionBand.Working));
    }

    /// <summary>
    /// <strong>A folder group is unchanged:</strong> two sessions in one folder, in no roster, one working and one
    /// finished, count <c>1 working, 1 unread</c>, as before.
    /// </summary>
    [Fact]
    public void Two_sessions_in_a_folder_group_still_count_one_by_one()
    {
        Session[] sessions =
        [
            Member("f-1", "Alpha", SessionState.Working, @"C:\shared", At),
            Member("f-2", "Beta", SessionState.Unread, @"C:\shared", At),
        ];

        var counted = CountedStates.Of(sessions, Book, At.AddMinutes(10));
        var summary = StatusSummary.OfCounted(counted);

        Assert.Equal(2, counted.Count);
        Assert.Equal(1, summary.Working);
        Assert.Equal(1, summary.Unread);
        Assert.Equal(SessionState.Unread, summary.Worst);
    }

    /// <summary>
    /// A roster beside a session in no roster: each counts once, in the sessions' order, with the roster where its
    /// first member appears. The rosters do not touch the lone session, and with no rosters every session counts.
    /// </summary>
    [Fact]
    public void A_roster_and_a_lone_session_each_count_once_in_order()
    {
        Session[] sessions =
        [
            Member("x-1", "Solo", SessionState.Unread, @"C:\solo", At),
            .. Orchestrating(SessionState.Unread, SessionState.Unread, SessionState.Working),
        ];

        var counted = CountedStates.Of(sessions, Book, At.AddMinutes(10));

        Assert.Equal(
            [new CountedState(SessionState.Unread, new SessionId("x-1")), new CountedState(SessionState.Working, new SessionId("s-3"))],
            counted);

        // The tray's light is the solo session's Unread, the worst across the two, and it names that session.
        Assert.Equal(SessionState.Unread, StatusSummary.OfCounted(counted).Worst);

        // With no rosters, the same sessions count one by one: the old reading, kept for a session in no roster.
        Assert.Equal(4, CountedStates.Of(sessions, RosterBook.Empty, At.AddMinutes(10)).Count);
        Assert.Equal(3, StatusSummary.Of(sessions).Unread);
    }

    /// <summary>The session total is not this list: three sessions are still three.</summary>
    [Fact]
    public void The_list_has_one_entry_for_the_roster_while_the_sessions_stay_three()
    {
        var sessions = Orchestrating(SessionState.Unread, SessionState.Unread, SessionState.Working);

        Assert.Equal(3, sessions.Length);
        Assert.Single(CountedStates.Of(sessions, Book, At.AddMinutes(10)));
    }

    private static int Band(IEnumerable<CountedState> counted, AttentionBand band) =>
        counted.Count(entry => AttentionOrder.BandOf(entry.State) == band);

    /// <summary>
    /// The issue's orchestration: Director, Coder and Reviewer in three folders, with the given states. Each member
    /// entered its state a minute after the one before, so the last stop is At + 2 minutes.
    /// </summary>
    private static Session[] Orchestrating(SessionState director, SessionState coder, SessionState reviewer) =>
    [
        Member("s-1", "Director", director, @"C:\director", At),
        Member("s-2", "Coder", coder, @"C:\coder", At.AddMinutes(1)),
        Member("s-3", "Reviewer", reviewer, @"C:\reviewer", At.AddMinutes(2)),
    ];

    private static Session Member(string id, string title, SessionState state, string cwd, DateTimeOffset entered) =>
        new()
        {
            Id = new SessionId(id),
            State = state,
            Latest = new Exchange { Prompt = "run the tests", StartedAt = entered },
            Cwd = cwd,
            WorkspaceGroup = GroupKeys.ForWorkspace(cwd),
            EnteredAt = entered,
            LastActivity = entered,
            LastHeardAt = entered,
            Title = title,
        };
}
