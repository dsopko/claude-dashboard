using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Core;

/// <summary>
/// A sound that the player queued, and the session whose row it marks (T1.67, issue #99).
/// Raised by <see cref="SoundPolicyEngine.SoundMarked"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The session is the one the engine decided, not the one the sound is filed under.</strong>
/// A group's own sound reaches the decisions record with an empty session, because it belongs to the
/// group. Its sign goes on the member whose finish settled the group, so a window, or a second
/// interface, gets the same row from the same rule.
/// </para>
/// <para>
/// Identifiers and an instant.
/// </para>
/// </remarks>
/// <param name="session">The session whose row shows the sign.</param>
/// <param name="sound">Which sound played.</param>
/// <param name="at">When the player queued it, by the engine's clock.</param>
public sealed class SoundMarkedEventArgs(SessionId session, SoundId sound, DateTimeOffset at) : EventArgs
{
    /// <summary>The session whose row shows the sign.</summary>
    public SessionId Session { get; } = session;

    /// <summary>Which sound played.</summary>
    public SoundId Sound { get; } = sound;

    /// <summary>When the player queued it.</summary>
    public DateTimeOffset At { get; } = at;
}
