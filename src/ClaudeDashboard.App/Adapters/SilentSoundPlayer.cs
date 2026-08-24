using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// An <see cref="ISoundPlayer"/> that logs what would have played and makes no sound.
/// </summary>
/// <remarks>
/// <para>
/// <strong>T1.14 replaces this, and must delete the file rather than register over it.</strong>
/// A superseded-but-present implementation is the kind that comes back — the T1.8 review made
/// that point about this task's other placeholder, and it applies here identically.
/// </para>
/// <para>
/// It exists because the nudge tick has to drive something real: the sound-policy engine is
/// wired and evaluating from this task onward, and Impl §2.4 has it emit intents against this
/// port rather than touching audio. Logging them is the honest placeholder — it makes the
/// policy observable before there is anything to hear, which is also what lets the tick be
/// demonstrated end to end.
/// </para>
/// </remarks>
public sealed class SilentSoundPlayer(ILogger logger) : ISoundPlayer
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>How many intents have been received. Diagnostic only.</summary>
    public int PlayedCount { get; private set; }

    /// <inheritdoc/>
    public void Play(SoundId sound, double gain, TimeSpan fade)
    {
        PlayedCount++;

        _logger.Information(
            "Sound {Sound} at gain {Gain} with a {FadeMs}ms fade. No audio adapter yet (T1.14).",
            sound.Name,
            gain,
            fade.TotalMilliseconds);
    }
}
