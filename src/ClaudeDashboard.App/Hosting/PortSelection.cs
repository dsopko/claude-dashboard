using System.Security.Cryptography;
using System.Text;

namespace ClaudeDashboard.App.Hosting;

/// <summary>How the bound port was arrived at (Impl §3.1).</summary>
public enum PortSource
{
    /// <summary>No port could be taken. The dashboard starts and cannot hear.</summary>
    None = 0,

    /// <summary>The port this user last bound, read from <c>port.txt</c>.</summary>
    Recorded = 1,

    /// <summary>A port the operator pinned in <c>settings.json</c>. Honoured before all else.</summary>
    Pinned = 4,

    /// <summary>Derived from this user's identity — the ordinary first-run answer.</summary>
    Derived = 2,

    /// <summary>Found by walking up from the derived candidate.</summary>
    Walked = 3,
}

/// <summary>What was found at one candidate port, for the log and for the tests.</summary>
/// <param name="Port">The candidate.</param>
/// <param name="Occupant">Who holds it, or <see cref="PortOccupant.Free"/> if nobody does.</param>
public readonly record struct PortAttempt(int Port, PortOccupant Occupant)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Port}:{Occupant}";
}

/// <summary>The chosen port and the record of how it was chosen.</summary>
/// <param name="Port">The port to bind, or the last candidate tried when <see cref="Source"/> is None.</param>
/// <param name="Source">Which of §3.1's three attempts produced it.</param>
/// <param name="Attempts">Every candidate tried, in order, with what was found there.</param>
/// <param name="Pin">The port the operator pinned, when they pinned one. Null otherwise.</param>
public sealed record PortChoice(
    int Port,
    PortSource Source,
    IReadOnlyList<PortAttempt> Attempts,
    int? Pin = null)
{
    /// <summary>Whether a port was actually secured.</summary>
    public bool Found => Source != PortSource.None;

    /// <summary>
    /// A pinned port was asked for and could not be taken.
    /// </summary>
    /// <remarks>
    /// <strong>Distinct from an exhausted walk, which is the other way to end with no port.</strong>
    /// They need different words: a walk that runs out means there was nothing free to find, while
    /// this means there were free ports and the dashboard deliberately declined them. Telling a
    /// pinned operator "no free loopback port after 1 attempts from base 52789" is wrong three ways
    /// — free ports exist, the base is one they never chose, and the pin they did choose goes
    /// unmentioned.
    /// </remarks>
    public bool PinRefused => !Found && Pin is not null;

    /// <summary>One line naming every candidate and its occupant, for the log.</summary>
    public string Trail => string.Join(" → ", Attempts);
}

/// <summary>
/// Chooses the loopback port this user's dashboard binds (Impl §3.1, amended 2026-08-26).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The port is per user, not per machine.</strong> A loopback bind is machine-wide while
/// everything else the dashboard owns lives under <c>%LOCALAPPDATA%</c>, so one fixed port lets
/// whoever signs in first take the only one and leaves every other user with a dashboard that can
/// never hear anything. That is issue #5.
/// </para>
/// <para>
/// <strong>Binding is the only question ever asked, and that is a ruling rather than a
/// simplification.</strong> There is no registry of who owns which port and none is to be built.
/// "May I have this port" is answered by trying to take it — instant, definitive, and the same
/// interlock T1.15 already relies on. Nothing here tells a second user that a port is spoken for,
/// because nothing needs to: two users derive different candidates because their identities
/// differ, so <strong>they never contend in the normal case</strong>. The walk exists for a hash
/// collision or a stranger, not for queueing users off a shared base.
/// </para>
/// <para>
/// <strong>SHA-256, never <c>GetHashCode()</c>.</strong> .NET randomises string hash codes per
/// process, so a <c>GetHashCode</c> derivation gives the same user a different port on every
/// launch — <c>port.txt</c> would keep papering over it, the feature would silently not work, and
/// <em>every in-process test would still pass</em>, because within one process the hash is stable.
/// That is the T1.15 trap exactly, and it is why <see cref="Derive"/> takes the slow road.
/// </para>
/// </remarks>
public static class PortSelection
{
    /// <summary>
    /// How many ports the derivation may land on, counted up from the base.
    /// </summary>
    /// <remarks>
    /// Wide enough that two users colliding is a curiosity rather than a routine event, and narrow
    /// enough that the whole range sits inside the private range above the base port. A collision
    /// is not a failure anyway — it costs one walk step.
    /// </remarks>
    public const int DefaultRange = 1000;

    /// <summary>
    /// How far the walk may go before the dashboard gives up and starts deaf.
    /// </summary>
    /// <remarks>
    /// Bounded because an unbounded walk on a busy machine is a startup that never finishes, which
    /// is worse than a dashboard that says it cannot hear. Thirty-two consecutive occupied ports
    /// is not a machine this tool can help with.
    /// </remarks>
    public const int DefaultWalk = 32;

    /// <summary>
    /// Derives this user's candidate port: <paramref name="basePort"/> plus a stable offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Stable across processes, machines and reboots, which is the entire requirement.</strong>
    /// SHA-256 of the identity, first four bytes as an unsigned integer, modulo the range. Nothing
    /// about it is secret and it does not need to be: it is a spreading function, not a
    /// protection. What it must be is <em>the same number every time</em>, and that is what rules
    /// out the framework's own string hash.
    /// </para>
    /// <para>
    /// Big-endian by hand rather than <c>BitConverter</c>, so the derivation cannot change with
    /// the endianness of whatever runs it. Today that is academic on Windows-x64; it costs one
    /// line to not have to think about it again.
    /// </para>
    /// </remarks>
    /// <param name="identity">The user's stable identity — their SID in the product.</param>
    /// <param name="basePort">The bottom of the range.</param>
    /// <param name="range">How many ports wide the range is.</param>
    /// <exception cref="ArgumentException"><paramref name="identity"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is not positive, or would run past 65535.</exception>
    public static int Derive(string identity, int basePort, int range = DefaultRange)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new ArgumentException("A port derivation needs a non-empty user identity.", nameof(identity));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(range, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(basePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(basePort + range - 1, 65535);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        var offset =
            ((uint)digest[0] << 24) |
            ((uint)digest[1] << 16) |
            ((uint)digest[2] << 8) |
            digest[3];

        return basePort + (int)(offset % (uint)range);
    }

    /// <summary>
    /// Works through §3.1's three attempts and returns the first port nobody holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="probe"/> is the whole of the mechanism: it answers "may I have this port"
    /// by trying to bind it, and classifies the occupant when the answer is no. In the product it
    /// is <c>HealthProbe.Probe</c>; in tests it is a table, which is what lets the walk be tested
    /// without occupying real ports.
    /// </para>
    /// <para>
    /// <strong>The walk classifies rather than merely counting.</strong> Every candidate and its
    /// occupant is kept in <see cref="PortChoice.Attempts"/>, so "another user's dashboard",
    /// "another copy of ours" and "a stranger" stay distinguishable in the log instead of
    /// collapsing into "taken". At 2am the difference between those three is the difference
    /// between three diagnoses.
    /// </para>
    /// <para>
    /// The walk wraps inside the range rather than running off the top of it, so a derivation near
    /// the ceiling gets the same number of chances as one near the floor.
    /// </para>
    /// </remarks>
    /// <param name="basePort">The bottom of this user's range.</param>
    /// <param name="identity">The user's stable identity.</param>
    /// <param name="recorded">The port from <c>port.txt</c>, or null on a fresh profile.</param>
    /// <param name="probe">Asks whether a port is free, and who holds it when it is not.</param>
    /// <param name="range">How many ports wide the range is.</param>
    /// <param name="walk">How many steps the walk may take.</param>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is null.</exception>
    public static PortChoice Choose(
        int basePort,
        string identity,
        int? recorded,
        Func<int, PortOccupant> probe,
        int? pinned = null,
        int range = DefaultRange,
        int walk = DefaultWalk)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var attempts = new List<PortAttempt>();

        // 0. AN EXPLICIT PIN WINS, AND IT IS THE ONLY ATTEMPT THAT DOES NOT FALL THROUGH.
        //    An operator who names a port has said where they want the dashboard; moving them off
        //    it silently would make the setting a suggestion. If it is taken they get the deaf
        //    start and the tray tooltip, which is a fault they can see and act on — unlike a
        //    dashboard quietly answering somewhere they did not ask for.
        if (pinned is { } pin && pin is > 0 and <= 65535)
        {
            var pinOccupant = probe(pin);
            attempts.Add(new PortAttempt(pin, pinOccupant));

            return new PortChoice(
                pin,
                pinOccupant == PortOccupant.Free ? PortSource.Pinned : PortSource.None,
                attempts,
                Pin: pin);
        }

        // 1. Continuity. The port this user had last time, if it is still to be had.
        if (recorded is { } port && port is > 0 and <= 65535)
        {
            var occupant = probe(port);
            attempts.Add(new PortAttempt(port, occupant));

            if (occupant == PortOccupant.Free)
            {
                return new PortChoice(port, PortSource.Recorded, attempts);
            }
        }

        // 2. The derivation. On a fresh profile this is the first thing tried, and in the ordinary
        //    case it is the last: two users differ here, so neither ever reaches the walk.
        var derived = Derive(identity, basePort, range);
        var derivedOccupant = probe(derived);
        attempts.Add(new PortAttempt(derived, derivedOccupant));

        if (derivedOccupant == PortOccupant.Free)
        {
            return new PortChoice(derived, PortSource.Derived, attempts);
        }

        // 3. The walk, for a hash collision or a stranger. Never for queueing users.
        for (var step = 1; step <= walk; step++)
        {
            var candidate = basePort + ((derived - basePort + step) % range);
            var occupant = probe(candidate);
            attempts.Add(new PortAttempt(candidate, occupant));

            if (occupant == PortOccupant.Free)
            {
                return new PortChoice(candidate, PortSource.Walked, attempts);
            }
        }

        return new PortChoice(derived, PortSource.None, attempts);
    }

    /// <summary>
    /// The whole of §3.1 for a data folder: pin, then <c>port.txt</c>, then derive, then walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One entry point so that no caller can accidentally get the pre-T1.21 design.</strong>
    /// <c>AppHost.Build</c>'s port used to fall back to the base port from settings when the caller
    /// supplied none — which produced no error and nothing failing, just a dashboard bound to the
    /// machine-wide port. That is worse than the interlock bug this task already fixed: <em>that</em>
    /// one announced itself by starting deaf, while this one produces a working dashboard on
    /// somebody else's port, with a hook URL that agrees with it and is wrong for this user.
    /// </para>
    /// <para>
    /// A remark saying "callers should pass a port" would have been the weakest fix available,
    /// because the parameter is public and optional. The fallback derives instead.
    /// </para>
    /// </remarks>
    /// <param name="paths">The data folder, which supplies <c>port.txt</c>.</param>
    /// <param name="settings">Supplies the pin, when the operator set one.</param>
    /// <param name="identity">The user's stable identity; defaults to this account's SID.</param>
    /// <param name="probe">Asks whether a port is free; defaults to a real bind through the health probe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="settings"/> is null.</exception>
    public static PortChoice ForDataFolder(
        Configuration.DashboardPaths paths,
        Configuration.DashboardSettings settings,
        string? identity = null,
        Func<int, PortOccupant>? probe = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);

        return Choose(
            Configuration.DashboardSettings.DefaultPort,
            identity ?? UserIdentity.Current,
            Configuration.PortFile.Read(paths),
            probe ?? (port => HealthProbe.Probe(port, SingleInstanceGate.NameFor(paths.Root)).Occupant),
            settings.Port);
    }
}
