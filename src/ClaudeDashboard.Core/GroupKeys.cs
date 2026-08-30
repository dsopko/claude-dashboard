namespace ClaudeDashboard.Core;

/// <summary>What a <see cref="GroupKey"/> was derived from.</summary>
/// <remarks>
/// A key is an identity, not a display string, and the two kinds are rendered very
/// differently — a workspace group is named after a directory, whereas an ungrouped session
/// stands alone and has no directory to name. Without this the two are indistinguishable
/// strings, and a caller would render a session id as though it were a path.
/// </remarks>
public enum GroupKeyKind
{
    /// <summary>The key came from somewhere this build does not recognize.</summary>
    Unknown = 0,

    /// <summary>Keyed on the session's workspace — the Phase 1 key (TS §IV.3).</summary>
    Workspace = 1,

    /// <summary>
    /// Keyed on the session itself, because no workspace was known. Such a session groups
    /// alone.
    /// </summary>
    Session = 2,

    /// <summary>
    /// Keyed on an operator roster: a named set of session names (issue #16). The one kind the
    /// operator's hand reaches, and it outranks <see cref="Workspace"/>.
    /// </summary>
    /// <remarks>
    /// <strong>Derived from the roster's NAME and never from its membership.</strong> A key built
    /// from members would change every time a session joined or left — and the settle window
    /// (T1.25) identifies a group across ticks by its key, so it would lose its identity at exactly
    /// the moment membership churn is what it exists to survive.
    /// </remarks>
    Roster = 3,
}

/// <summary>
/// The single place a <see cref="GroupKey"/> is assigned (TS §IV.3). Nothing else decides how
/// a session is grouped.
/// </summary>
/// <remarks>
/// <para>
/// Grouping "mirrors observable reality" and is never assigned by hand (TS §IV.3), so a key is
/// a function of what the events said — the workspace in Phase 1, and the virtual desktop in
/// Phase 4. Keys carry a kind prefix so those sources stay distinguishable after the fact;
/// Phase 4 adds a kind here rather than reinterpreting an existing one.
/// </para>
/// <para>
/// <strong>Workspace keys are normalized, and this is a deliberate domain rule rather than a
/// platform assumption.</strong> Two sessions in one directory must land in one group, but
/// <see cref="GroupKey"/> compares ordinally, so <c>C:\Projects\x</c> and <c>C:\projects\x</c>
/// would otherwise be two groups and would split a workspace on screen. This is not
/// hypothetical: this repository's own build emitted both spellings of its own path inside a
/// single run. So a workspace key is compared with separators unified, trailing separators
/// removed and casing folded, and the rule is stated in string terms rather than by calling a
/// path API whose behavior changes with the OS. Core therefore still reasons about strings, not
/// about Windows paths.
/// </para>
/// <para>
/// <strong>What that costs.</strong> On a case-sensitive filesystem, <c>/home/x/Work</c> and
/// <c>/home/x/work</c> are genuinely different directories and this rule merges them into one
/// group — showing together two sessions that are not together.
/// </para>
/// <para>
/// The bound on that risk is the filesystem of the sessions being <em>observed</em>, not the
/// architecture of the host: the dashboard being pinned to <c>win-x64</c> (Impl §1.1) governs
/// where it runs, not what <c>cwd</c> values reach it. A Claude Code session running under WSL
/// on the same machine reports POSIX, case-sensitive paths, and its hooks post to the same
/// loopback endpoint — so a case-sensitive <c>cwd</c> is reachable today. Accepting the cost is
/// still the right call, because triggering it needs two directories differing <em>only</em> by
/// case with live sessions in both, and the result is visible on screen rather than silent. If
/// it ever needs fixing, the fix is to inject the comparison as a port and let the host supply
/// it — a contained change, because the rule lives only in <see cref="Canonical"/>.
/// </para>
/// <para>
/// A key is not a display string. The canonical form has folded casing, so anything rendering
/// a group takes its label from a member's <see cref="Session.Cwd"/> and uses
/// <see cref="KindOf"/> to know whether a directory name is even the right thing to show.
/// </para>
/// </remarks>
public static class GroupKeys
{
    private const string WorkspacePrefix = "workspace:";
    private const string SessionPrefix = "session:";
    private const string RosterPrefix = "roster:";

    /// <summary>
    /// The key <paramref name="session"/> groups under, given the workspace
    /// <paramref name="cwd"/> its events reported.
    /// </summary>
    /// <remarks>
    /// With no workspace the session groups alone, keyed on itself. T1.1 made
    /// <see cref="Session.Cwd"/> required-but-possibly-empty precisely so ingress never drops
    /// a real event for want of a directory, which guarantees this case occurs. Pooling every
    /// directory-less session into one shared "unknown" group would assert they belong
    /// together, which is the one thing grouping must never invent (TS §IV.3).
    /// </remarks>
    public static GroupKey ForSession(string cwd, SessionId session) =>
        string.IsNullOrWhiteSpace(cwd) ? ForUngrouped(session) : ForWorkspace(cwd);

    /// <summary>The key for a known workspace.</summary>
    /// <exception cref="ArgumentException"><paramref name="cwd"/> is null, empty, or whitespace.</exception>
    public static GroupKey ForWorkspace(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            throw new ArgumentException("A workspace key needs a directory.", nameof(cwd));
        }

        return new GroupKey(WorkspacePrefix + Canonical(cwd));
    }

    /// <summary>The key for a session with no known workspace, which groups alone.</summary>
    /// <exception cref="ArgumentException"><paramref name="session"/> names no session.</exception>
    public static GroupKey ForUngrouped(SessionId session)
    {
        if (session.IsEmpty)
        {
            throw new ArgumentException("An ungrouped key needs a session.", nameof(session));
        }

        return new GroupKey(SessionPrefix + session.Value);
    }

    /// <summary>The key for an operator roster, derived from its name (issue #16).</summary>
    /// <exception cref="ArgumentException"><paramref name="roster"/> is null, empty, or whitespace.</exception>
    public static GroupKey ForRoster(string roster)
    {
        if (string.IsNullOrWhiteSpace(roster))
        {
            throw new ArgumentException("A roster key needs a name.", nameof(roster));
        }

        return new GroupKey(RosterPrefix + roster);
    }

    /// <summary>
    /// The group <paramref name="session"/> is <strong>actually in</strong>: its roster's, if its
    /// current title is in one, and otherwise the group its workspace implies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the only thing that answers "which group is this session in", and the
    /// precedence lives here rather than in any caller.</strong> A roster group outranks the
    /// workspace group: gathering sessions that <c>cwd</c> scatters is the entire point of a
    /// roster, so if the workspace won, a roster would do nothing.
    /// </para>
    /// <para>
    /// <strong>Why this is not stamped on the session by the Registry.</strong> A roster is
    /// operator configuration that changes at runtime, and <em>there is no event for "the operator
    /// edited a roster"</em>. A key stamped during <c>Apply</c> could only be corrected afterwards
    /// by walking the dictionary and rewriting records — a mutation outside the event stream, in a
    /// store whose whole design is that every value it writes comes from the event being applied,
    /// so that a replay rebuilds the same world. Computing the overlay on read costs one dictionary
    /// lookup and makes "editing a roster regroups live sessions immediately" free, which is the
    /// same argument <see cref="GroupResolver"/> already makes for re-deriving rather than caching.
    /// </para>
    /// <para>
    /// <see cref="Session.WorkspaceGroup"/> is named for what it is precisely because of this
    /// function: it is the group observable reality implies, and this is the group the session is
    /// displayed in. They differ exactly when a roster applies.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="rosters"/> is null.</exception>
    public static GroupKey Effective(Session session, RosterBook rosters)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rosters);

        return rosters.RosterFor(session.Title) is { } roster
            ? ForRoster(roster)
            : session.WorkspaceGroup;
    }


    /// <summary>
    /// The roster name inside a roster key, or null when <paramref name="key"/> is not one.
    /// </summary>
    /// <remarks>
    /// A key is not a display string in general — a workspace key is folded and a session key is an
    /// id — but a roster key is built from the operator's own label and nothing is done to it, so
    /// the label comes back out whole. Reading it here rather than looking the roster up in the
    /// store is what stops a header disagreeing with the group it heads: the key <em>is</em> the
    /// name.
    /// </remarks>
    public static string? RosterNameOf(GroupKey key) =>
        key.Value.StartsWith(RosterPrefix, StringComparison.Ordinal)
            ? key.Value[RosterPrefix.Length..]
            : null;
    /// <summary>What <paramref name="key"/> was derived from.</summary>
    public static GroupKeyKind KindOf(GroupKey key) => key.Value switch
    {
        var v when v.StartsWith(WorkspacePrefix, StringComparison.Ordinal) => GroupKeyKind.Workspace,
        var v when v.StartsWith(SessionPrefix, StringComparison.Ordinal) => GroupKeyKind.Session,
        var v when v.StartsWith(RosterPrefix, StringComparison.Ordinal) => GroupKeyKind.Roster,
        _ => GroupKeyKind.Unknown,
    };

    /// <summary>
    /// The comparison rule for workspaces: none of separator spelling, trailing separators or
    /// casing distinguishes one directory from another. See the remarks on this type for what
    /// that costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interior separators are unified as well as trailing ones. Handling one without the other
    /// would be half a rule: <c>C:\Projects\x</c> and <c>C:/Projects/x</c> name one directory,
    /// and leaving them as two keys splits a workspace into two groups that look entirely
    /// legitimate on screen — the failure is invisible, which is what makes it worth closing.
    /// The direction of the unification is arbitrary, since a key is an identity and never
    /// rendered.
    /// </para>
    /// <para>
    /// Unifying separators can in principle merge two distinct directories — a POSIX
    /// <c>/home/x</c> and a drive-relative Windows <c>\home\x</c> canonicalize alike. That needs
    /// live sessions in both, on one machine, at paths identical but for separator spelling;
    /// it is a far smaller risk than the workspace-splitting it prevents.
    /// </para>
    /// </remarks>
    private static string Canonical(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');

        // A path that is nothing but separators is the root, not nothing. Note the asymmetry
        // this leaves: "C:\" canonicalizes to "C:" but a bare "\" stays "\", because the
        // fallback returns the original. Harmless — each spelling still agrees with itself.
        return (trimmed.Length == 0 ? cwd : trimmed).Replace('/', '\\').ToUpperInvariant();
    }
}
