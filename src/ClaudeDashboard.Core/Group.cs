using System.Collections.Immutable;

namespace ClaudeDashboard.Core;

/// <summary>
/// A derived container for the sessions sharing a <see cref="GroupKey"/>, exposing the
/// worst member state and the most recent member activity (TS §IV.3; Impl §2.1).
/// </summary>
/// <remarks>
/// Derived, never operator-assigned: "grouping mirrors observable reality" (TS §IV.3). This
/// type is the <em>shape</em> only — the resolver that partitions sessions into groups, and
/// re-derives them when a <c>cwd</c> changes, is T1.4's.
/// </remarks>
public sealed record Group
{
    private readonly ImmutableArray<Session> _members;

    /// <summary>Builds a group over at least one member.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> names no group, or <paramref name="members"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is null, or contains null.</exception>
    public Group(GroupKey key, IEnumerable<Session> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (key.IsEmpty)
        {
            throw new ArgumentException("A group must have a key.", nameof(key));
        }

        Key = key;
        _members = members.ToImmutableArray();

        if (_members.IsEmpty)
        {
            throw new ArgumentException(
                "A group is derived from its members, so it cannot be empty.", nameof(members));
        }

        if (_members.Any(static m => m is null))
        {
            throw new ArgumentNullException(nameof(members), "A group cannot contain a null member.");
        }
    }

    /// <summary>The key every member shares — <c>cwd</c> in Phase 1 (TS §IV.3).</summary>
    public GroupKey Key { get; }

    /// <summary>The member sessions, in the order the caller supplied.</summary>
    public IReadOnlyList<Session> Members => _members;

    /// <summary>
    /// The group's roll-up state: the most severe state among its members, ranked
    /// Needs&#160;You &gt; Error &gt; Unread &gt; Working &gt; Quiet (TS §IV.3).
    /// </summary>
    /// <remarks>
    /// On a tie — two members equally severe, such as one <see cref="SessionState.NeedsPermission"/>
    /// and one <see cref="SessionState.NeedsQuestion"/> — the earlier member in
    /// <see cref="Members"/> wins. TS §IV.3 ranks the two Needs-You states together and does
    /// not order them against each other, so no ordering is invented here.
    /// </remarks>
    public SessionState WorstState
    {
        get
        {
            var worst = _members[0].State;
            var worstRank = Severity(worst);

            foreach (var member in _members)
            {
                var rank = Severity(member.State);
                if (rank > worstRank)
                {
                    worst = member.State;
                    worstRank = rank;
                }
            }

            return worst;
        }
    }

    /// <summary>The most recent activity across members (TS §IV.3, "group recency").</summary>
    public DateTimeOffset LastActivity
    {
        get
        {
            var latest = _members[0].LastActivity;
            foreach (var member in _members)
            {
                if (member.LastActivity > latest)
                {
                    latest = member.LastActivity;
                }
            }

            return latest;
        }
    }

    /// <summary>
    /// TS §IV.3's roll-up ranking. Deliberately not the <see cref="SessionState"/> ordinal and
    /// deliberately private: this is the group severity order, not TS §IV.2's display bands,
    /// which are T1.3's to define.
    /// </summary>
    /// <remarks>
    /// TS is inconsistent about <see cref="SessionState.Error"/>: §IV.2 puts it <em>inside</em>
    /// the Needs-You band, while §IV.3 ranks it <em>below</em> Needs You. §IV.3 is followed
    /// here because §IV.3 is the section that defines group roll-up. §IV.3 also omits
    /// <see cref="SessionState.Ended"/>; it ranks lowest, matching its last-place band in §IV.2.
    /// </remarks>
    private static int Severity(SessionState state) => state switch
    {
        SessionState.NeedsPermission or SessionState.NeedsQuestion => 5,
        SessionState.Error => 4,
        SessionState.Unread => 3,
        SessionState.Working => 2,
        SessionState.Acked => 1,
        SessionState.Ended => 0,
        _ => 0,
    };

    /// <summary>
    /// Value equality over the key and the member sequence. Written by hand because the
    /// synthesized version would compare <see cref="ImmutableArray{T}"/> by underlying array
    /// reference, making two identical groups unequal.
    /// </summary>
    public bool Equals(Group? other) =>
        other is not null &&
        Key.Equals(other.Key) &&
        _members.AsSpan().SequenceEqual(other._members.AsSpan());

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Key);
        foreach (var member in _members)
        {
            hash.Add(member);
        }

        return hash.ToHashCode();
    }
}
