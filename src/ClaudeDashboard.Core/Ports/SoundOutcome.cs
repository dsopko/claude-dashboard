namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// What <see cref="ISoundPlayer.Play"/> did with a sound (T1.55, issue #72).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The record tells the truth (the operator's ruling of 2026-10-03).</strong> Before
/// T1.55 the engine recorded "played" when it handed a sound to the player, and the player dropped
/// it when Windows had no output. A line that says a sound played when none did is worse than no
/// line, for the one question the record exists to answer: what made that sound?
/// </para>
/// <para>
/// <strong>Queued, not heard.</strong> <see cref="Queued"/> means the sound reached the output's
/// mixer. A device that is listed, active and silent (the volume at zero, a monitor with no
/// speakers) cannot be told apart from one that works, so nothing here claims the sound was heard.
/// </para>
/// </remarks>
public enum SoundOutcome
{
    /// <summary>The sound reached the output device's mixer.</summary>
    Queued = 1,

    /// <summary>Dropped: there was no working output device.</summary>
    NoOutput = 2,

    /// <summary>Dropped: the sound could not be played, for example a missing file, or an unexpected exception.</summary>
    Failed = 3,
}
