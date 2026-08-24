namespace ClaudeDashboard.Core;

/// <summary>
/// Bands and orders sessions for display (TS §IV.2; Impl §2.3). Pure and deterministic: it
/// reads no clock, touches no I/O, and every comparison comes from the timestamps already on
/// the sessions.
/// </summary>
/// <remarks>
/// <para>
/// TS §IV.2 calls the ordering asymmetry the heart of the attention model, and it is worth
/// stating plainly because it looks like an inconsistency: <strong>reds sort by ascending age,
/// greens by descending recency.</strong> A blocked session earns attention the longer it has
/// been blocked, so the oldest rises. A finished session is chased immediately after its
/// chime, so the newest rises. Sorting either one the other way would be quietly useless.
/// </para>
/// <para>
/// Every ordering here is <strong>total</strong>: the last tie-break is the session id, so no
/// two distinguishable sessions ever compare equal and nothing is left to whatever an
/// unstable sort happens to do. That matters beyond tidiness — T1.4 orders group members and
/// groups deterministically so a roll-up cannot flip between runs, and an ordering that
/// returned "equal" for two distinguishable sessions would hand that determinism back.
/// </para>
/// <para>
/// Both entry points preserve the churn-free property T1.4 established: an unchanged result
/// compares equal to the one it replaces, so a bound collection need not be rebuilt.
/// </para>
/// </remarks>
public static class AttentionEngine
{
    /// <summary>
    /// The flat view: every session banded and ordered, bands global and labelled (TS §IV.2).
    /// </summary>
    /// <returns>
    /// The non-empty bands, most urgent first. A band with no sessions is omitted rather than
    /// returned empty, so callers render one header per element.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessions"/> contains null.</exception>
    public static IReadOnlyList<BandedSessions> Order(IEnumerable<Session> sessions)
    {
        var ordered = Sorted(sessions);

        return
        [
            .. ordered
                .GroupBy(static session => AttentionOrder.BandOf(session.State))
                .OrderByDescending(static band => band.Key)
                .Select(static band => new BandedSessions(band.Key, band)),
        ];
    }

    /// <summary>
    /// The grouped view: the same ordering run <em>within</em> each group, with groups ordered
    /// by their most urgent member (TS §IV.2).
    /// </summary>
    /// <returns>
    /// The same groups, each with its members in display order, the groups themselves ordered
    /// by <see cref="Group.WorstState"/> and then — as TS §IV.2 specifies — by latest activity,
    /// so active groups float up.
    /// </returns>
    /// <remarks>
    /// Bands are not labelled in this view: TS §IV.2 labels bands in the flat view only, and
    /// within a group the ordering speaks for itself.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="groups"/> contains null.</exception>
    public static IReadOnlyList<Group> OrderGroups(IEnumerable<Group> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var materialized = groups.ToList();
        if (materialized.Exists(static group => group is null))
        {
            throw new ArgumentException("A group collection cannot contain null.", nameof(groups));
        }

        return
        [
            .. materialized
                .Select(static group => new Group(group.Key, Sorted(group.Members)))
                .OrderByDescending(static group => AttentionOrder.Rank(group.WorstState))
                .ThenByDescending(static group => group.LastActivity)
                .ThenBy(static group => group.Key.Value, StringComparer.Ordinal),
        ];
    }

    /// <summary>Sorts sessions into display order without banding them.</summary>
    private static List<Session> Sorted(IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var ordered = sessions.ToList();
        if (ordered.Exists(static session => session is null))
        {
            throw new ArgumentException("A session collection cannot contain null.", nameof(sessions));
        }

        ordered.Sort(Compare);
        return ordered;
    }

    /// <summary>Display order: band first, then the rule that band sorts by, then the id.</summary>
    /// <remarks>
    /// The band branch is live only through <see cref="OrderGroups"/>, which sorts a group's
    /// members and does not regroup afterwards, so this decides band precedence *within* a
    /// group. <see cref="Order"/> re-bands the sorted sequence and orders the bands itself, so
    /// the flat view does not depend on this branch — do not read it as the thing that puts
    /// Needs-You above Unread on screen.
    /// </remarks>
    private static int Compare(Session left, Session right)
    {
        var leftBand = AttentionOrder.BandOf(left.State);
        var rightBand = AttentionOrder.BandOf(right.State);

        if (leftBand != rightBand)
        {
            return rightBand.CompareTo(leftBand);
        }

        var within = leftBand switch
        {
            // Kind first, then oldest first within a kind — sub-bands, not tie-breaks
            // (TS §IV.3's ratified order; see AttentionOrder).
            AttentionBand.NeedsYou =>
                AttentionOrder.Rank(right.State).CompareTo(AttentionOrder.Rank(left.State)) is var byKind
                    && byKind != 0
                        ? byKind
                        : left.EnteredAt.CompareTo(right.EnteredAt),

            // Newest first: the freshest finish is the one being chased after a beep.
            AttentionBand.Unread => right.EnteredAt.CompareTo(left.EnteredAt),

            // Working, Quiet and Ended all sort by most recent activity.
            _ => right.LastActivity.CompareTo(left.LastActivity),
        };

        return within != 0 ? within : string.CompareOrdinal(left.Id.Value, right.Id.Value);
    }
}
