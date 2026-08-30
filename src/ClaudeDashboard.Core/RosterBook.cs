using System.Collections.Immutable;

namespace ClaudeDashboard.Core;

/// <summary>
/// A named set of session names the operator groups together (TS §IV.3; issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The members are session titles, and a title can carry the operator's words.</strong> A
/// session nobody named gets a title written by a background model call summarising their first
/// prompt (T1.24). So a roster is logged by its <see cref="Name"/> and never by its
/// <see cref="Members"/>.
/// </para>
/// <para>
/// The name is the operator's own label, typed to name a group. It is never derived from a prompt
/// or an answer, and it is deliberately loggable — that is the whole reason the two halves are
/// separated here rather than being one bag of strings.
/// </para>
/// </remarks>
/// <param name="Name">The operator's label for this roster. Never empty.</param>
/// <param name="Members">The session titles that belong to it. Never empty; ordinal and unique.</param>
public sealed record Roster(string Name, ImmutableArray<string> Members)
{
    /// <summary>Whether <paramref name="title"/> is one of this roster's members.</summary>
    public bool Contains(string title) => Members.Contains(title, StringComparer.Ordinal);
}

/// <summary>
/// Every roster the operator has defined, and the two invariants that hold whatever the caller
/// does (issue #16 rules 4 and 6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable, replaced rather than mutated.</strong> Two threads read a book — the
/// dispatcher, when the window regroups, and the event consumer, when it decides a session's
/// effective group for sound — and one writes it. Making every operation return a new book is what
/// lets that happen without a lock, exactly as the Registry's records do.
/// </para>
/// <para>
/// <strong>THE INVARIANTS LIVE HERE, NOT IN THE CALLER.</strong> Issue #16's rules 4 and 6 —
/// a name in at most one roster, and a roster with no members ceases to exist — are enforced by
/// every operation on this type. An invariant a caller must remember to maintain is one the second
/// caller breaks, and there will be a second caller: the operator UI is T1.26, the settings file is
/// another, and a future import would be a third.
/// </para>
/// <para>
/// <strong>Matching is ordinal and exact.</strong> A member name is not something the operator
/// types from memory — T1.26 forms a roster by ticking live rows, so the stored name is copied from
/// the title Claude Code reported. Comparing exactly is therefore comparing two copies of one
/// string. Folding case would be guessing at an equivalence nothing here has evidence for, and it
/// would silently merge two sessions the operator can see are differently named.
/// </para>
/// </remarks>
public sealed class RosterBook
{
    /// <summary>No rosters at all — every session groups by its workspace.</summary>
    public static readonly RosterBook Empty = new([]);

    private readonly ImmutableArray<Roster> _rosters;
    private readonly Dictionary<string, string> _rosterByMember;

    private RosterBook(ImmutableArray<Roster> rosters)
    {
        _rosters = rosters;
        _rosterByMember = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var roster in rosters)
        {
            foreach (var member in roster.Members)
            {
                _rosterByMember[member] = roster.Name;
            }
        }
    }

    /// <summary>The rosters, in the order they were defined.</summary>
    public IReadOnlyList<Roster> Rosters => _rosters;

    /// <summary>Whether there are no rosters, which is the ordinary case.</summary>
    public bool IsEmpty => _rosters.IsEmpty;

    /// <summary>
    /// Builds a book from raw pairs, applying every invariant. <strong>This is the only way in.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A file is not the store.</strong> A hand-edited <c>settings.json</c> can hold a name
    /// in two rosters, a roster with no members, a blank name, or the same member twice; none of
    /// those is representable here, so they are resolved on the way in rather than tolerated. The
    /// order is fixed so that two loads of one file always agree:
    /// </para>
    /// <list type="number">
    /// <item><description>names and members are trimmed; blank ones are dropped</description></item>
    /// <item><description>members are deduplicated within a roster</description></item>
    /// <item><description>a member in more than one roster stays in the <em>first</em> and leaves
    /// the rest (rule 4)</description></item>
    /// <item><description>a roster left with no members ceases to exist (rule 6), including one
    /// emptied by the step above</description></item>
    /// <item><description>a repeated roster name merges into the first of that name, for the same
    /// reason a repeated member does not duplicate</description></item>
    /// </list>
    /// <param name="rosters">Name and members pairs, in the order they should be resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rosters"/> is null.</exception>
    /// </remarks>
    public static RosterBook From(IEnumerable<(string Name, IEnumerable<string> Members)> rosters)
    {
        ArgumentNullException.ThrowIfNull(rosters);

        var order = new List<string>();
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (rawName, rawMembers) in rosters)
        {
            var name = (rawName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var members))
            {
                members = [];
                byName[name] = members;
                order.Add(name);
            }

            foreach (var rawMember in rawMembers ?? [])
            {
                var member = (rawMember ?? string.Empty).Trim();

                // `claimed` carries both rules at once: it rejects a repeat within this roster and
                // a name already taken by an earlier one, which are the same operation.
                if (member.Length > 0 && claimed.Add(member))
                {
                    members.Add(member);
                }
            }
        }

        return new RosterBook(
        [
            .. order
                .Where(name => byName[name].Count > 0)
                .Select(name => new Roster(name, [.. byName[name]])),
        ]);
    }

    /// <summary>
    /// The name of the roster <paramref name="title"/> belongs to, or null when it belongs to none.
    /// </summary>
    /// <remarks>
    /// A session with no title can never be in a roster: there is nothing to match, and matching
    /// "no title" against "no title" would gather every unnamed session into one group — the one
    /// thing grouping must never invent (TS §IV.3).
    /// </remarks>
    public string? RosterFor(string? title) =>
        !string.IsNullOrEmpty(title) && _rosterByMember.TryGetValue(title, out var roster)
            ? roster
            : null;

    /// <summary>
    /// The book that results from putting <paramref name="members"/> in a roster called
    /// <paramref name="name"/>, replacing whatever that roster held.
    /// </summary>
    /// <remarks>
    /// Rules 4 and 6 apply: each name leaves any other roster it was in, and a roster emptied by
    /// that ceases to exist. Passing no members removes the roster, which is rule 6 again rather
    /// than a separate operation.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is null.</exception>
    public RosterBook With(string name, IEnumerable<string> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var incoming = members.ToList();

        // The new roster is resolved FIRST, so rule 4 takes names off the others rather than the
        // other way round: the operator's latest instruction is the one that wins.
        var pairs = new List<(string, IEnumerable<string>)> { (name, incoming) };

        foreach (var roster in _rosters)
        {
            if (!string.Equals(roster.Name, name, StringComparison.Ordinal))
            {
                pairs.Add((roster.Name, roster.Members));
            }
        }

        return From(pairs);
    }

    /// <summary>
    /// The book that results from taking <paramref name="member"/> out of whatever roster holds it
    /// (issue #16 rule 5).
    /// </summary>
    /// <remarks>
    /// Removal is permanent, which is rule 5's whole point: the session does not rejoin on the next
    /// start. A roster left empty by it ceases to exist (rule 6).
    /// </remarks>
    public RosterBook Without(string member) =>
        From(_rosters.Select(roster => (
            roster.Name,
            roster.Members.Where(m => !string.Equals(m, member, StringComparison.Ordinal)).AsEnumerable())));
}
