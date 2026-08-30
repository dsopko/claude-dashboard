using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// How a test says "this session is in no roster" when telling the sound engine about it.
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
/// </remarks>
internal static class SoundEngineCalls
{
    /// <summary>Tells the engine about a session that is in no roster.</summary>
    public static void ChangedUngrouped(this SoundPolicyEngine engine, Session session)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(session);

        engine.OnSessionChanged(session, session.WorkspaceGroup);
    }
}
