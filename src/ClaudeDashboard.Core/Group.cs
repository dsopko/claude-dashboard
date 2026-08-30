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
    /// Permission &gt; Error &gt; Question &gt; Unread &gt; Working &gt; Quiet &gt; Ended
    /// (TS §IV.3, as ratified on 2026-08-24) — <strong>except in a roster group, where Working
    /// outranks Unread</strong> (issue #16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The roll-up is <see cref="AttentionOrder.WorstOf(IEnumerable{SessionState}, SeverityOrder)"/>
    /// — the same one the tray uses over every session (Impl §5.2), over the same
    /// <see cref="AttentionOrder.Rank(SessionState, SeverityOrder)"/> the attention engine bands
    /// by, deliberately not a second copy. Because that order is total, members can only tie when
    /// they are in the <em>same</em> state, so member order cannot affect the result. A group
    /// always has at least one member, so the empty answer never arises here.
    /// </para>
    /// <para>
    /// <strong>This type is the one place that knows which ordering applies</strong>, and it knows
    /// it from its own key rather than from a caller. A caller that had to pass the ordering in
    /// would be a caller that could pass the wrong one, and the two answers differ in exactly the
    /// case — a member finishing while another works — that nothing else would notice was wrong.
    /// </para>
    /// <para>
    /// <strong>This is the raw roll-up, before the settle window.</strong> A roster group that has
    /// only just gone quiet reads <see cref="SessionState.Unread"/> here and is still displayed as
    /// working; <see cref="RosterSettle"/> owns that, because it needs a clock and this type holds
    /// no history.
    /// </para>
    /// </remarks>
    public SessionState WorstState =>
        AttentionOrder.WorstOf(_members.Select(member => member.State), Order);

    /// <summary>Which severity order this group rolls up under — decided by its key, never passed in.</summary>
    public SeverityOrder Order =>
        GroupKeys.KindOf(Key) == GroupKeyKind.Roster ? SeverityOrder.RosterGroup : SeverityOrder.Session;

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
