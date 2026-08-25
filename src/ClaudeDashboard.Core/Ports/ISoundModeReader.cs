namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// A read-only view of the global sound modes, for whoever has to display them (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only on purpose, and that is the whole reason it exists.</strong> The tray has to
/// render "muted 24 min" and "paused", so it needs these two facts; it must never
/// <em>set</em> them, because the setters enter the single-writer region and the tray runs on the
/// Dispatcher. T1.2b made that region mutual exclusion rather than thread affinity, so a mutator
/// called from a menu click succeeds whenever the consumer is idle and throws only when the two
/// overlap — it would pass in testing and fail in front of the operator. Global mode changes go
/// down the Channel as a <c>SoundCommand</c> instead.
/// </para>
/// <para>
/// Handing the host this interface rather than the engine makes that structural instead of
/// remembered: there is no mutator on the surface to call by mistake. It is the same move as
/// giving the ack publisher an event sink rather than the Registry.
/// </para>
/// <para>
/// Both members are safe to read from any thread. They are backed by single-word fields written
/// only inside the single-writer region, so this adds a reader and not a second writer.
/// </para>
/// </remarks>
public interface ISoundModeReader
{
    /// <summary>Whether monitoring is off duty — silence, and a glyph that says so.</summary>
    bool IsMonitoringPaused { get; }

    /// <summary>
    /// When the global mute lapses; null when nothing is globally muted, and
    /// <see cref="DateTimeOffset.MaxValue"/> when the mute has no expiry.
    /// </summary>
    /// <remarks>
    /// A lapse raises no event — the mute is a predicate evaluated where a sound would be
    /// emitted, never a timer that re-enables. Anything displaying this must therefore recompute
    /// on a tick rather than waiting to be told.
    /// </remarks>
    DateTimeOffset? AllMutedUntil { get; }
}
