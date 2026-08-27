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
/// this tool should refuse to start over, so the account name stands in, and the caller logs that
/// it happened rather than swallowing it.
/// </para>
/// <para>
/// <strong>WHY THAT FALL-BACK IS SAFE, WHICH IS NOT OBVIOUS AND IS THE REASON THIS PARAGRAPH
/// EXISTS.</strong> A name-derived port and a SID-derived port are different numbers for the same
/// user, so a token failure looks as though it would move an established user to a new port on the
/// day it happens. <strong>It does not.</strong> Once <c>port.txt</c> exists, §3.1's Recorded
/// branch wins and the derivation is never consulted — the fall-back can only be reached where it
/// changes nothing.
/// </para>
/// <para>
/// The real exposure is narrower and worth naming: <strong>a token failure on a user's very first
/// run</strong> records a name-derived port, permanently. That port is stable and works; it is
/// simply not the port that user would otherwise have had, and nothing later moves them back. The
/// consequence is nil unless somebody is comparing derived ports across machines. It is not silent
/// either — <see cref="Resolve"/> reports which kind of identity it returned, and start-up logs a
/// warning when it is the fall-back.
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
