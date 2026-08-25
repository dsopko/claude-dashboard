using System.Collections.Concurrent;
using System.IO;
using ClaudeDashboard.Core.Ports;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// Plays the dashboard's sounds through NAudio (Impl Part 7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It decides nothing.</strong> The engine hands it a sound, a gain and a fade, and it
/// plays exactly that. There is no mute here and no notion of a session or a group — mute is
/// policy and lives in <c>SoundPolicyEngine</c>, which proves it by never calling this at all.
/// A second mute in the adapter would be the same rule in two code paths.
/// </para>
/// <para>
/// <strong>One device, one mixer.</strong> A resident app that opened a device per beep would
/// pay the open latency on every notice and would stack fifteen independent outputs when fifteen
/// sessions finish together. Instead the device is opened once and every sound is added as an
/// input to a single <see cref="MixingSampleProvider"/>, which sums them — the burst becomes one
/// stream rather than fifteen, which is Impl Part 7's stated reason for wanting a mixer.
/// </para>
/// <para>
/// <strong>Samples are decoded once and cached.</strong> A burst re-reading the same file
/// fifteen times would do fifteen file opens on the consumer thread; the decode happens on the
/// first play of each sound and never again. There are four sounds and they are a fraction of a
/// second each, so the cache is bounded by the enum.
/// </para>
/// <para>
/// <strong>Never throws, and that is a contract rather than caution.</strong> Audio is the least
/// important thing this application does. A missing file, a device that will not open, a file
/// that will not decode — each degrades to silence and a log line. The caller is the event
/// consumer, and an exception here would take down the loop that keeps the whole dashboard
/// current, to say nothing of the beep.
/// </para>
/// </remarks>
public sealed class NAudioSoundPlayer : ISoundPlayer, IDisposable
{
    /// <summary>
    /// The format everything is mixed at: 44.1kHz stereo float.
    /// </summary>
    /// <remarks>
    /// Fixed rather than taken from the first file, because a mixer has one format and the
    /// second sound to arrive would otherwise be the one that failed. Mono sources are widened
    /// to stereo on the way in.
    /// </remarks>
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly ConcurrentDictionary<SoundId, float[]> _decoded = new();
    private readonly SoundCatalog _catalog;
    private readonly ILogger _logger;
    private readonly MixingSampleProvider _mixer;
    private readonly IWavePlayer? _output;
    private readonly object _gate = new();

    private bool _disposed;

    /// <summary>Creates the player and opens the output device.</summary>
    /// <param name="catalog">Where sound files are found.</param>
    /// <param name="logger">Where degradations are recorded.</param>
    /// <param name="outputFactory">
    /// How the output device is created. Defaults to a <see cref="WaveOut"/>. Tests pass a
    /// factory that throws, to exercise the no-device path, or one that returns a fake, to
    /// assert on the mixer without needing hardware.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> or <paramref name="logger"/> is null.</exception>
    public NAudioSoundPlayer(SoundCatalog catalog, ILogger logger, Func<IWavePlayer>? outputFactory = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _logger = logger;

        _mixer = new MixingSampleProvider(MixFormat)
        {
            // Without this the mixer reports the end of its stream the moment no input is
            // playing, and the device stops. A resident app wants a silent stream that never
            // ends, so the next notice starts instantly instead of restarting the device.
            ReadFully = true,
        };

        try
        {
            var output = (outputFactory ?? (() => new WaveOut()))();
            output.Init(_mixer);
            output.Play();
            _output = output;
        }
        catch (Exception ex)
        {
            // No device, a device that will not open, or one that vanished between enumeration
            // and use — an RDP session, a headless build agent, a USB headset unplugged. The
            // dashboard runs; it is simply quiet.
            _logger.Warning(
                ex,
                "No audio output device could be opened. The dashboard will run silently.");

            _output = null;
        }
    }

    /// <summary>Whether an output device is open. Diagnostic only.</summary>
    public bool HasOutput => _output is not null;

    /// <summary>How many sounds have been started. Diagnostic only.</summary>
    public int PlayedCount { get; private set; }

    /// <summary>How many were asked for and could not be played. Diagnostic only.</summary>
    public int DegradedCount { get; private set; }

    /// <summary>How many inputs the mixer is currently summing. Diagnostic only.</summary>
    internal int MixerInputCount => _mixer.MixerInputs.Count();

    /// <summary>
    /// The gain the last sound was actually handed to the mixer at, after clamping.
    /// </summary>
    /// <remarks>
    /// Exposed because "a sound played" is satisfied by any sound at any volume, and the gain is
    /// the whole difference between a notice and a nudge. This is the number, after the clamp the
    /// port's contract requires.
    /// </remarks>
    internal float LastVolume { get; private set; }

    /// <summary>
    /// The type of provider the last sound was wrapped in, so a fade can be told from its absence.
    /// </summary>
    /// <remarks>
    /// A nudge that played exactly like a notice would satisfy "the nudge is quieter" — the fade
    /// is the other half of Impl Part 7's "softer rather than merely quieter", and it is only
    /// visible in the shape of the chain.
    /// </remarks>
    internal string LastProviderKind { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public void Play(SoundId sound, double gain, TimeSpan fade)
    {
        try
        {
            PlayCore(sound, gain, fade);
        }
        catch (Exception ex)
        {
            // The catch is deliberately not narrowed. Every named failure below is already
            // handled; this is for the one nobody predicted, and the contract says the caller
            // never sees it.
            DegradedCount++;
            _logger.Warning(ex, "Playing {Sound} failed. Continuing without it.", sound.Name);
        }
    }

    /// <summary>Stops the device and releases it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output?.Dispose();
    }

    private void PlayCore(SoundId sound, double gain, TimeSpan fade)
    {
        if (_disposed || _output is null)
        {
            DegradedCount++;
            return;
        }

        if (Samples(sound) is not { } samples)
        {
            DegradedCount++;
            return;
        }

        // Clamped rather than rejected, per the port's contract. A gain above one would clip
        // against the mixer's other inputs, and a negative one would invert the waveform.
        var volume = (float)Math.Clamp(gain, 0d, 1d);

        ISampleProvider provider = new VolumeSampleProvider(new CachedSound(samples, MixFormat))
        {
            Volume = volume,
        };

        if (fade > TimeSpan.Zero)
        {
            // What makes a nudge feel softer rather than merely quieter (Impl Part 7): the sound
            // arrives instead of appearing.
            var fading = new FadeInOutSampleProvider(provider, initiallySilent: true);
            fading.BeginFadeIn(fade.TotalMilliseconds);
            provider = fading;
        }

        LastVolume = volume;
        LastProviderKind = provider.GetType().Name;

        lock (_gate)
        {
            // The mixer is not documented as thread-safe, and Play is reached from the consumer
            // thread while NAudio reads from its own. This is the only lock in the adapter and it
            // guards a list insert, never a decode or a file read.
            _mixer.AddMixerInput(provider);
        }

        PlayedCount++;
    }

    /// <summary>The decoded samples for <paramref name="sound"/>, or null if it cannot be read.</summary>
    private float[]? Samples(SoundId sound)
    {
        if (_decoded.TryGetValue(sound, out var cached))
        {
            return cached;
        }

        if (_catalog.Resolve(sound) is not { } path)
        {
            _logger.Warning(
                "No sound file for {Sound}. Looked for {File} beside the app and under the config folder.",
                sound.Name,
                sound.Name + SoundCatalog.Extension);

            return null;
        }

        try
        {
            var samples = Decode(path);
            _decoded[sound] = samples;

            return samples;
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or ArgumentException)
        {
            // A file that exists and is not audio: truncated, renamed from something else, or
            // written by an editor that did not finish. Named exceptions here rather than a bare
            // catch, so a genuinely unexpected failure still reaches Play's log line with its
            // own message.
            _logger.Warning(ex, "The sound file {Path} could not be decoded. Skipping it.", path);

            return null;
        }
    }

    /// <summary>Reads a WAV file into mix-format samples.</summary>
    private static float[] Decode(string path)
    {
        using var reader = new AudioFileReader(path);

        ISampleProvider source = reader;

        if (source.WaveFormat.Channels == 1 && MixFormat.Channels == 2)
        {
            source = new MonoToStereoSampleProvider(source);
        }

        if (source.WaveFormat.SampleRate != MixFormat.SampleRate
            || source.WaveFormat.Channels != MixFormat.Channels)
        {
            throw new InvalidDataException(
                $"{path} is {source.WaveFormat.SampleRate}Hz/{source.WaveFormat.Channels}ch; "
                + $"the mixer runs at {MixFormat.SampleRate}Hz/{MixFormat.Channels}ch.");
        }

        var buffer = new List<float>();
        var chunk = new float[source.WaveFormat.SampleRate * source.WaveFormat.Channels];
        int read;

        while ((read = source.Read(chunk.AsSpan())) > 0)
        {
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        return [.. buffer];
    }

    /// <summary>
    /// One playback of an already-decoded sound.
    /// </summary>
    /// <remarks>
    /// The samples are shared and never copied — a burst of fifteen adds fifteen readers over
    /// one array, each with its own position. That is what makes the coalescing cheap as well as
    /// correct.
    /// </remarks>
    private sealed class CachedSound(float[] samples, WaveFormat format) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat => format;

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, samples.Length - _position);

            if (available <= 0)
            {
                return 0;
            }

            samples.AsSpan(_position, available).CopyTo(buffer);
            _position += available;

            return available;
        }
    }
}
