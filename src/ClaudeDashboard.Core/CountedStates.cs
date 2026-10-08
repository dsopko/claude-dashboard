namespace ClaudeDashboard.Core;

/// <summary>
/// One state to count, and the session it stands for (T1.83, issue #130).
/// </summary>
/// <param name="State">The state that counts: a session's own, or a roster group's roll-up.</param>
/// <param name="Session">
/// The session the state stands for: the session itself, or, for a roster group, the member whose state the roster
/// shows (see <see cref="CountedStates.Of"/>). The tray's decision record names it.
/// </param>
public readonly record struct CountedState(SessionState State, SessionId Session);

/// <summary>
/// The states the counts count: one for each session in no roster, and one for each roster group (T1.83, issue #130).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a roster counts once.</strong> A roster group is one orchestration: its members hand work to one
/// another, so at any moment one works and the others have finished their turn and wait for it. Its heading and its
/// one sound already read it as one thing, by <see cref="RosterSettle.StateOf"/>: Working while any member works or
/// a hand-off is in flight, Unread once every member has been quiet for the settle window. Counted member by member,
/// a working orchestration read <c>2 unread · 1 working</c> and lit the tray green while the work was still going.
/// The counts strip, the tray tooltip and light, and <c>/state</c>'s bands all count this list, so they agree with the
/// heading and with each other.
/// </para>
/// <para>
/// <strong>A folder group is not rolled up</strong>: its members are counted one by one, as before. Sessions that
/// share a folder are not an orchestration, and a Working-dominates roll-up there would hide another session's
/// finished work.
/// </para>
/// <para>
/// <strong>The session total is not this list.</strong> "19 sessions" counts sessions and keeps meaning it; only the
/// band counts read this list.
/// </para>
/// <para>
/// <strong>The order</strong> is the order of <paramref name="sessions"/>, with a roster group's entry where its first
/// member appears, so a reader that takes "the first at the worst state" names the same session it named before for
/// a session in no roster.
/// </para>
/// </remarks>
public static class CountedStates
{
    /// <summary>The states to count, at <paramref name="now"/>.</summary>
    /// <param name="sessions">Every session the dashboard knows about.</param>
    /// <param name="rosters">The rosters that decide which sessions form a roster group.</param>
    /// <param name="now">The instant to read each roster group at; its settle window is measured to it.</param>
    /// <param name="window">The settle window; <see cref="RosterSettle.DefaultWindow"/> when null.</param>
    /// <returns>
    /// One entry for each session in no roster group, and one for each roster group: its
    /// <see cref="RosterSettle.StateOf"/>, standing for the member whose state the roster shows. That member is the first
    /// one, by id, at the roster's state. When the roster reads Working from the settle window alone (every member has
    /// finished, and the window has not run out), no member is Working, and the entry stands for
    /// <see cref="RosterSettle.SettledBy"/>: the member whose finish started the window.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> or <paramref name="rosters"/> is null.</exception>
    public static IReadOnlyList<CountedState> Of(
        IEnumerable<Session> sessions,
        RosterBook rosters,
        DateTimeOffset now,
        TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(rosters);

        var all = sessions.ToList();
        var rosterGroups = GroupResolver.Resolve(all, rosters)
            .Where(group => GroupKeys.KindOf(group.Key) == GroupKeyKind.Roster)
            .ToDictionary(group => group.Key);

        var counted = new List<CountedState>(all.Count);
        var done = new HashSet<GroupKey>();

        foreach (var session in all)
        {
            var key = GroupKeys.Effective(session, rosters);

            if (!rosterGroups.TryGetValue(key, out var group))
            {
                counted.Add(new CountedState(session.State, session.Id));
                continue;
            }

            if (done.Add(key))
            {
                var state = RosterSettle.StateOf(group, now, window);
                counted.Add(new CountedState(state, StandsFor(group, state)));
            }
        }

        return counted;
    }

    /// <summary>The member whose state the roster shows; see <see cref="Of"/>.</summary>
    private static SessionId StandsFor(Group group, SessionState state)
    {
        foreach (var member in group.Members)
        {
            if (member.State == state)
            {
                return member.Id;
            }
        }

        return RosterSettle.SettledBy(group);
    }
}
