namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Reads the names in a session's transcript (issue #161). Implemented in App over the file system; Core reads no
/// file.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Called on the event consumer's thread, before the Registry applies the event.</strong> The result rides
/// on the event (<see cref="Events.InboundEvent.Transcript"/>), so the Registry has its one writer and the
/// archive's row for that event already has the name the row shows.
/// </para>
/// <para>
/// <strong>An implementation must be cheap, bounded and quiet.</strong> It reads only when the file changed since
/// its last read for the session, only from the end, and never throws: a missing, locked or malformed transcript
/// gives <see cref="TranscriptNames.None"/>.
/// </para>
/// </remarks>
public interface ITranscriptNames
{
    /// <summary>
    /// The names that <paramref name="transcriptPath"/> holds and that this reader has not given for
    /// <paramref name="session"/> before, or <see cref="TranscriptNames.None"/>.
    /// </summary>
    /// <param name="session">The session the event names.</param>
    /// <param name="transcriptPath">The event's <c>transcript_path</c>, or null when it had none.</param>
    /// <returns>What the read found; never throws.</returns>
    TranscriptNames Read(SessionId session, string? transcriptPath);
}
