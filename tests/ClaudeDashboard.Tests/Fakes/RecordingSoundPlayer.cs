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
/// <para>
/// <strong>Safe across threads</strong> (T1.75, for issue #112). In a pipeline test the consumer thread plays while the
/// test thread polls, and a plain list read during an add threw "Collection was modified". <see cref="Play"/> and
/// <see cref="Clear"/> change the list under a lock, and every reader takes a copy under the same lock and answers
/// from it. So <see cref="Played"/> is a copy taken when it is read, not a live view.
/// </para>
/// </remarks>
public sealed class RecordingSoundPlayer : ISoundPlayer
{
    private readonly List<PlayedSound> _played = [];
    private readonly Lock _gate = new();

    /// <summary>Every call so far, in order: a copy, taken when it is read.</summary>
    public IReadOnlyList<PlayedSound> Played => Copy();

    /// <summary>The most recent call, or null if nothing has played.</summary>
    public PlayedSound? Last
    {
        get
        {
            lock (_gate)
            {
                return _played.Count == 0 ? null : _played[^1];
            }
        }
    }

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
    public IReadOnlyList<double> Gains => [.. Copy().Select(p => p.Gain)];

    /// <summary>
    /// What <see cref="Play"/> answers (T1.55): queued unless a test says the output is gone or the
    /// sound failed. The call is recorded either way, because the engine made it.
    /// </summary>
    public SoundOutcome Outcome { get; set; } = SoundOutcome.Queued;

    /// <inheritdoc/>
    public SoundOutcome Play(SoundId sound, double gain, TimeSpan fade)
    {
        lock (_gate)
        {
            _played.Add(new PlayedSound(sound, gain, fade));
        }

        return Outcome;
    }

    /// <summary>Every call for one sound, in order.</summary>
    public IReadOnlyList<PlayedSound> PlayedOf(SoundId sound) =>
        [.. Copy().Where(p => p.Sound == sound)];

    /// <summary>Forgets everything recorded so far, so a test can assert over one phase at a time.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _played.Clear();
        }
    }

    /// <summary>The calls so far, copied under the lock.</summary>
    private List<PlayedSound> Copy()
    {
        lock (_gate)
        {
            return [.. _played];
        }
    }
}
