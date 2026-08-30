using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// How a test says "this session is grouped by its workspace" when telling the sound engine.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SoundPolicyEngine.OnSessionChanged"/> requires the session's <em>effective</em>
/// group, and requires it deliberately: a defaulted parameter would let a caller silently get
/// workspace behaviour for a roster member, which is wrong in a way nothing would report.
/// </para>
/// <para>
/// <strong>So this helper is named for what it asserts rather than for the method it calls.</strong>
/// Every test that predates rosters means "no roster applies here", and saying that in the call is
/// what stops a later roster test reaching for the short form and quietly testing the wrong thing.
/// A helper called <c>OnSessionChanged</c> would have reinstated the defaulted parameter under
/// another name.
/// </para>
/// <para>
/// <strong>And it says WORKSPACE group, not "ungrouped", because that is what it passes.</strong>
/// A workspace group is a group: it has its own key and its own mute entry, so a session in one is
/// not ungrouped in any sense the sound engine cares about. The earlier name was a small false
/// statement sitting on top of a decision made to avoid exactly those.
/// </para>
/// </remarks>
internal static class SoundEngineCalls
{
    /// <summary>Tells the engine about a session grouped by its workspace — that is, in no roster.</summary>
    public static void ChangedInWorkspaceGroup(this SoundPolicyEngine engine, Session session)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(session);

        engine.OnSessionChanged(session, session.WorkspaceGroup);
    }
}
