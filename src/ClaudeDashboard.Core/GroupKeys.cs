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
/// single run. So a workspace key is compared with trailing separators removed and casing
/// folded, and the rule is stated in string terms — trim <c>\</c> and <c>/</c>, then fold case
/// invariantly — rather than by calling a path API whose behavior changes with the OS. Core
/// therefore still reasons about strings, not about Windows paths.
/// </para>
/// <para>
/// <strong>What that costs.</strong> On a case-sensitive filesystem, <c>/home/x/Work</c> and
/// <c>/home/x/work</c> are genuinely different directories and this rule would merge them into
/// one group — showing together two sessions that are not together. That is not reachable in
/// any planned deployment: the host is a Windows tray app pinned to <c>win-x64</c> (Impl §1.1)
/// and the Phase 7 remote surface is a second consumer of Core on the same machine, not a
/// second platform. If that ever changes, the fix is to inject the comparison as a port and
/// let the host supply it — a contained change, because the rule lives only in
/// <see cref="Canonical"/>.
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

    /// <summary>What <paramref name="key"/> was derived from.</summary>
    public static GroupKeyKind KindOf(GroupKey key) => key.Value switch
    {
        var v when v.StartsWith(WorkspacePrefix, StringComparison.Ordinal) => GroupKeyKind.Workspace,
        var v when v.StartsWith(SessionPrefix, StringComparison.Ordinal) => GroupKeyKind.Session,
        _ => GroupKeyKind.Unknown,
    };

    /// <summary>
    /// The comparison rule for workspaces: trailing separators are meaningless and casing does
    /// not distinguish directories. See the remarks on this type for what that costs.
    /// </summary>
    private static string Canonical(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');

        // A path that is nothing but separators is the root, not nothing.
        return (trimmed.Length == 0 ? cwd : trimmed).ToUpperInvariant();
    }
}
