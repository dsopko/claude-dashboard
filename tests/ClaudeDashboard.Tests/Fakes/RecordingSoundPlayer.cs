using System.Collections.Immutable;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>One call to <see cref="ISoundPlayer.Play"/>, as recorded by <see cref="RecordingSoundPlayer"/>.</summary>
/// <param name="Sound">The sound asked for.</param>
/// <param name="Gain">The gain asked for.</param>
/// <param name="Fade">The fade-in asked for.</param>
public readonly record struct PlayedSound(SoundId Sound, double Gain, TimeSpan Fade);

/// <summary>
/// An <see cref="ISoundPlayer"/> that records what it was asked to play instead of playing it.
/// </summary>
/// <remarks>
/// T1.5's engine emits intents against this port and never touches audio (Impl §2.4), so
/// asserting against this recording <em>is</em> asserting the sound policy: that a notice
/// fires on entry, that nudges widen, that <c>Acked</c> cancels them, and that a mute
/// suppresses them (TS §IV.5).
/// </remarks>
public sealed class RecordingSoundPlayer : ISoundPlayer
{
    private readonly List<PlayedSound> _played = [];

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<PlayedSound> Played => _played;

    /// <summary>The most recent call, or null if nothing has played.</summary>
    public PlayedSound? Last => _played.Count == 0 ? null : _played[^1];

    /// <summary>
    /// The gains of every call so far, in order — the shape a widening-nudge assertion wants.
    /// </summary>
    /// <remarks>
    /// Typed <see cref="IReadOnlyList{T}"/> rather than <see cref="ImmutableArray{T}"/>
    /// deliberately: <c>ImmutableArray</c> compares by underlying array reference, so
    /// <c>Assert.Equal(expected, player.Gains)</c> binds to the value-equality overload and
    /// fails even when every element matches. Handing back a plain list keeps the collection
    /// comparison consumers expect.
    /// </remarks>
    public IReadOnlyList<double> Gains => [.. _played.Select(p => p.Gain)];

    /// <inheritdoc/>
    public void Play(SoundId sound, double gain, TimeSpan fade) =>
        _played.Add(new PlayedSound(sound, gain, fade));

    /// <summary>Every call for one sound, in order.</summary>
    public IReadOnlyList<PlayedSound> PlayedOf(SoundId sound) =>
        [.. _played.Where(p => p.Sound == sound)];

    /// <summary>Forgets everything recorded so far, so a test can assert over one phase at a time.</summary>
    public void Clear() => _played.Clear();
}
