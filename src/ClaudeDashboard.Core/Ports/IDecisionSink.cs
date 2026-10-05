namespace ClaudeDashboard.Core.Ports;

/// <summary>What kind of sound decision was made (T1.37, issue #48).</summary>
public enum SoundDecisionKind
{
    /// <summary>The first sound for a state entry.</summary>
    Notice = 1,

    /// <summary>A reminder that the session is still waiting.</summary>
    Nudge = 2,

    /// <summary>A roster group finished as one.</summary>
    GroupNotice = 3,

    /// <summary>The settled group's single soft reminder.</summary>
    GroupNudge = 4,
}

/// <summary>Why a sound that was due did not play (T1.37, issue #48).</summary>
public enum SuppressionReason
{
    /// <summary>Monitoring is paused — the operator went off duty.</summary>
    MonitoringPaused = 1,

    /// <summary>Everything is muted, timed or indefinitely.</summary>
    AllMuted = 2,

    /// <summary>This session is muted.</summary>
    SessionMuted = 3,

    /// <summary>This session's group is muted.</summary>
    GroupMuted = 4,

    /// <summary>
    /// A roster member's done notice belongs to the group, which announces once for everyone
    /// (issue #16).
    /// </summary>
    GroupDone = 5,

    /// <summary>
    /// An entry the engine had already announced came back unchanged — same state, same entry
    /// instant — after a quiet tick put the row back (T1.44, issue #56). It is the same entry, so
    /// it is not announced twice, and its nudge ladder resumes where it was.
    /// </summary>
    AlreadyAnnounced = 6,
}

/// <summary>
/// Where the sound engine records what it decided — played or deliberately not played — and why
/// (T1.37, issue #48).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An intent port, exactly like <see cref="ISoundPlayer"/>, and it exists for the same
/// reason.</strong> The engine's suppression reasons and nudge ladder live in its private state
/// and never crossed its one output port — <see cref="ISoundPlayer.Play"/> carries a sound, a
/// gain and a fade, nothing else — so when a sound played or went silent, nothing outside the
/// engine could say why. Recomputing the policy outside would be a second copy of the rules;
/// this port is the engine saying it itself, at the moment it decides.
/// </para>
/// <para>
/// <strong>Observation only.</strong> Nothing the sink does may change a decision: the engine
/// calls it after deciding, implementations must not throw, and the default is
/// <see cref="NullDecisionSink"/>, which is the engine exactly as it was.
/// </para>
/// <para>
/// <strong>Identifiers and enums only</strong> — a session id, a group key, a sound id, a rung,
/// a duration.
/// </para>
/// </remarks>
public interface IDecisionSink
{
    /// <summary>A sound was emitted, and the player queued it on the output.</summary>
    /// <param name="kind">Notice, nudge, or the group's.</param>
    /// <param name="session">The session it is about; empty for a group's own sound.</param>
    /// <param name="group">The effective group.</param>
    /// <param name="sound">Which sound.</param>
    /// <param name="rung">The nudge ladder step; 0 for a notice.</param>
    /// <param name="waited">How long the session had been waiting; zero for a notice.</param>
    void SoundPlayed(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        int rung,
        TimeSpan waited);

    /// <summary>
    /// A sound was emitted, and the player dropped it (T1.55, issue #72): there was no output, or
    /// it failed. Recorded in place of <see cref="SoundPlayed"/>, with the same identifiers.
    /// </summary>
    /// <param name="kind">Notice, nudge, or the group's.</param>
    /// <param name="session">The session it is about; empty for a group's own sound.</param>
    /// <param name="group">The effective group.</param>
    /// <param name="sound">Which sound was dropped.</param>
    /// <param name="rung">The nudge rung; zero for a notice.</param>
    /// <param name="waited">How long the session had waited, for a nudge.</param>
    /// <param name="outcome">Why: <see cref="SoundOutcome.NoOutput"/> or <see cref="SoundOutcome.Failed"/>.</param>
    void SoundDropped(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        int rung,
        TimeSpan waited,
        SoundOutcome outcome);

    /// <summary>A sound that was due was deliberately not emitted.</summary>
    /// <param name="kind">Notice, nudge, or the group's.</param>
    /// <param name="session">The session it is about; empty for a group's own sound.</param>
    /// <param name="group">The effective group.</param>
    /// <param name="sound">Which sound would have played.</param>
    /// <param name="reason">Why it did not.</param>
    void SoundSuppressed(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        SuppressionReason reason);
}

/// <summary>The default sink: the engine exactly as it was before T1.37.</summary>
public sealed class NullDecisionSink : IDecisionSink
{
    /// <summary>The one instance; the type carries no state.</summary>
    public static readonly NullDecisionSink Instance = new();

    private NullDecisionSink()
    {
    }

    /// <inheritdoc/>
    public void SoundPlayed(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        int rung,
        TimeSpan waited)
    {
    }

    /// <inheritdoc/>
    public void SoundDropped(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        int rung,
        TimeSpan waited,
        SoundOutcome outcome)
    {
    }

    /// <inheritdoc/>
    public void SoundSuppressed(
        SoundDecisionKind kind,
        SessionId session,
        GroupKey group,
        SoundId sound,
        SuppressionReason reason)
    {
    }
}
