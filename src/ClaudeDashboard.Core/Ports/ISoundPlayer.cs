namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Plays the dashboard's sounds (Impl §1.3, Part 7). Implemented in App over NAudio
/// (T1.14); implemented in Tests by a player that records what it was asked to play.
/// </summary>
/// <remarks>
/// This is the whole reason the sound-policy engine can be a pure unit-testable function:
/// the engine emits <em>intents</em> against this port and never touches an audio API
/// itself (Impl §2.4).
/// </remarks>
public interface ISoundPlayer
{
    /// <summary>
    /// Starts playing <paramref name="sound"/> at <paramref name="gain"/>, fading in over
    /// <paramref name="fade"/>.
    /// </summary>
    /// <param name="sound">Which sound to play.</param>
    /// <param name="gain">
    /// Linear gain, where <c>1.0</c> is the sound at full volume and <c>0.0</c> is silence.
    /// TS §IV.5: a notice plays at full gain and a nudge plays the same sound lower — there
    /// are no separate "quiet" sound files. Master volume and per-session or per-group mute
    /// are folded in by the caller, since they are policy, not playback. Implementations
    /// clamp out-of-range values rather than rejecting them.
    /// </param>
    /// <param name="fade">
    /// Fade-in duration; <see cref="TimeSpan.Zero"/> plays at once. Impl Part 7: a short
    /// fade-in is what makes a nudge feel softer rather than merely quieter.
    /// </param>
    /// <remarks>
    /// <para>
    /// Returns as soon as playback has been <em>started</em> — it does not wait for the sound
    /// to finish, and callers must not treat it as if it did. Sound is a notification, never
    /// something the event pipeline waits on.
    /// </para>
    /// <para>
    /// <strong>Never throws.</strong> Audio is the least important thing this application
    /// does; a missing sound file or an unavailable output device degrades to silence and a
    /// log line, and must never propagate into the caller (TS §IV.7).
    /// </para>
    /// </remarks>
    void Play(SoundId sound, double gain, TimeSpan fade);
}
