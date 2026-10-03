namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// Whether the sound player has a working output device: what the sound device notice reads
/// (T1.55, issue #72).
/// </summary>
/// <remarks>
/// <para>
/// An App interface rather than a member of Core's <c>ISoundPlayer</c>: a device is an adapter's
/// business, and the engine never needs to know. The notice reads this, never the NAudio type.
/// </para>
/// <para>
/// <strong>Read from the UI thread, set on the audio threads.</strong> An implementation publishes
/// the value safely, and answers quickly: the UI tick waits for the answer.
/// </para>
/// </remarks>
public interface ISoundOutput
{
    /// <summary>
    /// Whether a working output device is bound. Already true or false when the player is built:
    /// the first attempt to bind finishes before its constructor returns.
    /// </summary>
    bool HasOutput { get; }
}
