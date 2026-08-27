using System.Security.Principal;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// The stable per-user string the ingress port is derived from (Impl §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The SID, not the account name, and the difference is not pedantry.</strong> §3.1 asks
/// for a hash of the user's SID. <c>LogonTask.CurrentUserId</c> — which the task named as already
/// resolving this — returns <c>DOMAIN\user</c>, which is an identity but not that one. A SID
/// survives a rename, and two accounts called <c>daves</c> in different domains have different
/// SIDs and the same <c>DOMAIN\user</c> tail. Both are stable enough for the common case; only one
/// is the thing the specification names.
/// </para>
/// <para>
/// <strong>It degrades rather than throwing.</strong> A token without a user SID is not something
/// this tool should refuse to start over, so the account name stands in, and the fall-back is
/// logged by the caller rather than swallowed. The consequence of the fall-back is small and worth
/// stating: the derived port stays stable for that user on that machine, which is all the
/// derivation actually needs.
/// </para>
/// </remarks>
public static class UserIdentity
{
    /// <summary>This user's SID, or their account name if the SID cannot be read.</summary>
    /// <remarks>
    /// Wrapped because <c>WindowsIdentity.GetCurrent()</c> can throw on a token this process
    /// cannot open, and a port derivation is not worth failing a start over.
    /// </remarks>
    public static string Current => Resolve(out _);

    /// <summary>This user's SID, saying whether it really is one.</summary>
    /// <param name="isSid">
    /// True when the returned value is a SID; false when it is the account-name fall-back.
    /// </param>
    public static string Resolve(out bool isSid)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            if (identity.User?.Value is { Length: > 0 } sid)
            {
                isSid = true;

                return sid;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Fall through to the account name.
        }

        isSid = false;

        return LogonTask.CurrentUserId;
    }
}
