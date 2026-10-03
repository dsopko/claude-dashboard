namespace ClaudeDashboard.App.Hosting;

/// <summary>What this process should do about the instance already out there (Impl §5.3).</summary>
public enum StartupAction
{
    /// <summary>
    /// No copy of us is serving. Start, and bind the port that <see cref="PortSelection.Choose"/>
    /// finds: a port held by anyone else is only a candidate that is skipped (Impl §3.1).
    /// </summary>
    /// <remarks>
    /// <strong>Value 2 is gone, and so is "start without ingress" (T1.57 review, the director's
    /// ruling of 2026-10-03).</strong> It came from T1.15, when there was one fixed port: a stranger
    /// on it left nowhere else to go. Since T1.21 the port is chosen per user, and a stranger on the
    /// port in <c>port.txt</c> is one candidate among several; the choice walks on. Starting deaf
    /// there left the dashboard hearing nothing while its own choice had found a free port, and its
    /// notice blamed a port that was free. The other values keep their numbers.
    /// </remarks>
    StartNormally = 1,

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
/// <strong>The gate is the authority; the port corroborates.</strong> A port this dashboard
/// releases is a port anything may hold, so after a hard kill "the port is in use" never means
/// "another copy of us is running". Reading a failed bind as "I must be the second instance"
/// would have this process signal a stranger and exit, and the dashboard would silently never
/// start. Deriving the port per user (§3.1, T1.21) does not change that: it makes a collision
/// rarer, and this ruling is about what happens when there is one.
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
/// <strong>Every unresolved case starts rather than exits.</strong> A dashboard that starts can
/// say what is wrong; one that exits without a window has no channel left to explain itself with.
/// The single exception is a gate held by a live copy of us, where starting would mean two
/// Registries on one data folder. <strong>When this process holds the gate, the occupant of the
/// recorded port decides only one thing:</strong> whether it is a copy of us to signal. Anything
/// else on that port is a candidate the port choice skips (Impl §3.1, §5.3); only a pin that is
/// taken, or a walk that runs out, leaves the dashboard without a port.
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
                // signal, and none of them is a reason for this user to have no dashboard: the port
                // choice skips it and binds the next free candidate (T1.57 review).
                _ => StartupAction.StartNormally,
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
    /// <param name="occupant">What the probe found, or <see cref="PortOccupant.Free"/> if nothing was probed.</param>
    /// <param name="port">
    /// The port that was probed, or <see langword="null"/> when this user has never recorded one.
    /// <strong>Nullable since T1.21</strong>: naming the base port to a user who has never bound it
    /// is the same mistake as probing it, one layer out — the number would be real and belong to
    /// somebody else. Found by sweeping the port-bearing sites rather than by a failure.
    /// </param>
    public static string ExplainReportAndExit(PortOccupant occupant, int? port) => occupant switch
    {
        PortOccupant.Free when port is null =>
            "another copy of the dashboard holds the single-instance gate, and this user has no recorded " +
            "port, so there was nothing to ask. It is starting, stopping, or running without ingress. " +
            "This copy will not start a second one; if no dashboard appears, end the other process and " +
            "try again.",

        PortOccupant.Free =>
            $"another copy of the dashboard holds the single-instance gate, but nothing is listening on port {port}. " +
            "It is starting, stopping, or running without ingress. This copy will not start a second one; " +
            "if no dashboard appears, end the other process and try again.",

        // Deliberately not "free the port and restart". The reachable version of this is a
        // dashboard that already started without ingress because no port was free, or its pin was taken —
        // so a dashboard *is* running, it simply cannot be asked to surface, and telling the
        // operator to restart it would have them close the only one they have.
        //
        // AND SINCE T1.21, "FREE THAT PORT" WOULD BE WORSE THAN UNHELPFUL. Ports are per user
        // (§3.1), so a port held by somebody else is the ordinary case rather than a fault — and
        // the holder may be another signed-in user's dashboard, which this operator cannot free
        // and should not try to. The advice now says what they can actually do.
        //
        // STALE BY CONFIDENCE, NOT BY DEMONSTRATION: nobody has shown this arm reachable under the
        // new derivation. It needs the gate held AND the recorded port occupied by a stranger.
        // The wording is corrected because it would be wrong if reached, not because it was
        // observed being reached.
        _ =>
            $"another copy of the dashboard holds the single-instance gate, but port {port} is held by something else, " +
            "so this copy cannot ask it to surface. Open the running dashboard from its tray icon. " +
            "The port it recorded is now held by another program — possibly another user's dashboard, which is " +
            "normal and not yours to close. Quit the running dashboard and start it again, and it will choose " +
            "a free port for itself.",
    };
}
