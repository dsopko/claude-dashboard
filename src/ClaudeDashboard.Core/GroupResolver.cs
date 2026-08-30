namespace ClaudeDashboard.Core;

/// <summary>
/// Derives the set of <see cref="Group"/>s from the sessions the Registry holds (TS §IV.3).
/// </summary>
/// <remarks>
/// <para>
/// A pure function over a session collection, holding no state of its own. The alternative —
/// caching groups and patching them as events arrive — buys nothing here and costs the classic
/// bug: a cache that disagrees with the Registry shows a session in a group it has left. At the
/// scale this tool describes, a burst of fifteen simultaneous sessions (Impl §4), a full
/// re-derivation is one pass to bucket fifteen items plus a handful of small allocations —
/// far below the cost of the dispatcher hop that follows it (Impl §4), let alone of rendering.
/// Correct-by-construction is worth more than an optimization that would not be measurable.
/// </para>
/// <para>
/// Re-derivation is also what TS §IV.3 asks for: the key is "re-derived on directory-change
/// events, not fixed at session start". A session whose <c>cwd</c> moved simply carries a
/// different <see cref="Session.WorkspaceGroup"/> the next time this runs, and lands in the right group
/// with no membership bookkeeping.
/// </para>
/// <para>
/// Callers that want to avoid churning a bound collection can compare successive results
/// directly: <see cref="Group"/> has value equality over its key and member sequence, so an
/// unchanged group compares equal to the one it replaced.
/// </para>
/// <para>
/// Ordering here is for determinism, not for display: groups come back ordered by key and
/// members by session id. Attention banding — ordering groups by their most urgent member, and
/// sessions within them — is the attention engine's (T1.3).
/// </para>
/// </remarks>
public static class GroupResolver
{
    /// <summary>
    /// Partitions <paramref name="sessions"/> into the groups their keys imply.
    /// </summary>
    /// <returns>
    /// One <see cref="Group"/> per distinct <see cref="Session.WorkspaceGroup"/> key, ordered by key.
    /// Never contains an empty group: a group is derived from its members, so it exists exactly
    /// as long as one does, and a session leaving takes its group with it when it was the last.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessions"/> contains null.</exception>
    public static IReadOnlyList<Group> Resolve(IEnumerable<Session> sessions) =>
        Resolve(sessions, RosterBook.Empty);

    /// <summary>
    /// Partitions <paramref name="sessions"/> into groups, with <paramref name="rosters"/> applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The roster overlay is applied here rather than being read from the session, because
    /// <see cref="Session.WorkspaceGroup"/> is what the events reported and a roster is what the
    /// operator asked for. <see cref="GroupKeys.Effective"/> owns the precedence; this method only
    /// buckets by whatever it returns.
    /// </para>
    /// <para>
    /// <strong>Re-deriving on every call is what makes a roster edit take effect immediately.</strong>
    /// There is no membership bookkeeping to update and no cache to invalidate: the next resolve
    /// simply buckets differently, which is the same argument this type already makes for not
    /// caching groups across <c>cwd</c> changes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<Group> Resolve(IEnumerable<Session> sessions, RosterBook rosters)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(rosters);

        var members = sessions.ToList();
        if (members.Exists(static session => session is null))
        {
            throw new ArgumentException("A session collection cannot contain null.", nameof(sessions));
        }

        return
        [
            .. members
                .GroupBy(session => GroupKeys.Effective(session, rosters))
                .OrderBy(static group => group.Key.Value, StringComparer.Ordinal)
                .Select(static group => new Group(
                    group.Key,
                    group.OrderBy(static session => session.Id.Value, StringComparer.Ordinal))),
        ];
    }
}
