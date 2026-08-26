namespace ClaudeDashboard.App.Hosting;

/// <summary>What this process should do about the instance already out there (Impl §5.3).</summary>
public enum StartupAction
{
    /// <summary>Nothing else is here. Start, and bind the configured port.</summary>
    StartNormally = 1,

    /// <summary>
    /// Somebody else holds the port and it is not a copy of us. Start anyway — the dashboard is
    /// useful with a window and a tray even when it can hear nothing — but say so loudly.
    /// </summary>
    StartWithoutIngress = 2,

    /// <summary>A copy of us on this data folder is serving. Ask it to surface, then exit.</summary>
    SignalAndExit = 3,

    /// <summary>
    /// A copy of us holds the gate, but it cannot be signalled. Exit, and leave a reason —
    /// starting a rival would be two dashboards on one data folder, which the gate exists to
    /// prevent.
    /// </summary>
    ReportAndExit = 4,
}

/// <summary>
/// The single-instance decision: which of the two interlocks decides, and when (Impl §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gate is the authority; the port corroborates.</strong> The port is fixed, so
/// after a hard kill anything may hold it, and "the port is in use" never means "another copy of
/// us is running". Reading a failed bind as "I must be the second instance" would have this
/// process signal a stranger and exit, and the dashboard would silently never start.
/// </para>
/// <para>
/// <strong>But the gate is silent in exactly one case, and that case is the trap.</strong> When
/// the gate is free it has said nothing, and leaving the corroborating signal to decide alone is
/// precisely what the principle exists to stop. So the port's answer is not "somebody is here"
/// but "here is who I am" — the gate name, over <c>/health</c> — and the comparison, not the
/// occupancy, is what decides. That is why <see cref="PortOccupant"/> distinguishes ours from
/// another dashboard's: a loopback bind is machine-wide while the gate is per logon session and
/// per data folder, so a healthy dashboard on our port may belong to another signed-in user.
/// </para>
/// <para>
/// <strong>Every unresolved case starts rather than exits.</strong> A dashboard that runs
/// half-deaf and says so can be diagnosed; one that exits without a window has no channel left
/// to explain itself with. The single exception is a gate held by a live copy of us, where
/// starting would mean two Registries on one data folder.
/// </para>
/// </remarks>
public static class StartupDecision
{
    /// <summary>Decides what to do, given both interlocks.</summary>
    /// <param name="holdsGate">Whether this process took the single-instance mutex.</param>
    /// <param name="occupant">Who holds the ingress port, from <see cref="HealthProbe"/>.</param>
    public static StartupAction For(bool holdsGate, PortOccupant occupant)
    {
        if (holdsGate)
        {
            return occupant switch
            {
                // Nothing there. The ordinary first start.
                PortOccupant.Free => StartupAction.StartNormally,

                // A copy of us on this data folder is serving without holding the gate — a stale
                // build, or a name that no longer matches. It can be signalled, and should be:
                // two of us on one data folder is the thing to avoid, whichever holds the mutex.
                PortOccupant.OurInstance => StartupAction.SignalAndExit,

                // Another user's dashboard, a stranger, a silent socket, or anything a later
                // build invents. All of them mean the port is not ours to use and not ours to
                // signal, and none of them is a reason for this user to have no dashboard.
                _ => StartupAction.StartWithoutIngress,
            };
        }

        // Something already holds our gate, so a copy of us on this data folder is alive. The
        // only question left is whether it can be reached.
        return occupant == PortOccupant.OurInstance
            ? StartupAction.SignalAndExit
            : StartupAction.ReportAndExit;
    }

    /// <summary>Why <see cref="StartupAction.ReportAndExit"/> was reached, in words for the log.</summary>
    /// <remarks>
    /// The three causes have different fixes, and the log line is the only diagnosis the
    /// operator gets: a second instance has no window and no console.
    /// </remarks>
    public static string ExplainReportAndExit(PortOccupant occupant, int port) => occupant switch
    {
        PortOccupant.Free =>
            $"another copy of the dashboard holds the single-instance gate, but nothing is listening on port {port}. " +
            "It is starting, stopping, or running without ingress. This copy will not start a second one; " +
            "if no dashboard appears, end the other process and try again.",

        // Deliberately not "free the port and restart". The reachable version of this is a
        // dashboard that already started without ingress because something else held the port —
        // so a dashboard *is* running, it simply cannot be asked to surface, and telling the
        // operator to restart it would have them close the only one they have.
        _ =>
            $"another copy of the dashboard holds the single-instance gate, but port {port} is held by something else, " +
            "so this copy cannot ask it to surface. Open the running dashboard from its tray icon. " +
            "To restore ingress, free that port and restart it.",
    };
}
