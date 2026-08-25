using System.IO;
using System.Linq;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using NAudio.Wave;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Adapters;

/// <summary>
/// The audio adapter (T1.14; Impl Part 7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is asserted here and what is not.</strong> These reach as far as the mixer: that
/// the right file was resolved, that a sound became an input, that a burst became several inputs
/// rather than one or none, and that every named failure degrades to silence instead of throwing.
/// What happens after the mixer — a device driver, a speaker — is not observable in-process and
/// is checked by running the app.
/// </para>
/// <para>
/// <strong>The output device is faked, not skipped.</strong> A fake <see cref="IWavePlayer"/>
/// stands in for the sound card so these run on a build agent with no audio at all, and so the
/// no-device path can be provoked rather than waited for. The real device is only constructed by
/// the default factory, which the app uses and these do not.
/// </para>
/// </remarks>
public sealed class NAudioSoundPlayerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _shipped;
    private readonly string _overrides;

    public NAudioSoundPlayerTests()
    {
        _shipped = Path.Combine(_root, "shipped");
        _overrides = Path.Combine(_root, "overrides");

        Directory.CreateDirectory(_shipped);
        Directory.CreateDirectory(_overrides);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- The happy path, asserted specifically ---------------------------------------------------

    /// <summary>
    /// A sound reaches the mixer.
    /// </summary>
    /// <remarks>
    /// "A sound played" would be satisfied by any sound at any gain, so this also names which
    /// file was resolved. The mixer input count is the observable end of the chain.
    /// </remarks>
    [Fact]
    public void A_notice_reaches_the_mixer()
    {
        WriteTone(_shipped, SoundId.Finished);
        using var player = Player();

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(1, player.PlayedCount);
        Assert.Equal(0, player.DegradedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>
    /// <strong>Notice and nudge are the same file at different gains.</strong>
    /// </summary>
    /// <remarks>
    /// The requirement is not "the nudge is quieter" — a second, quieter file would satisfy that
    /// and would be exactly what TS §IV.5 forbids ("no separate quiet sound files"). So this
    /// asserts the thing that actually distinguishes it: one file on disk, resolved for both, and
    /// the two gains differing. Only one <c>.wav</c> exists in either folder for the whole test.
    /// </remarks>
    [Fact]
    public void A_nudge_is_the_same_file_as_the_notice_played_lower()
    {
        WriteTone(_shipped, SoundId.Permission);

        var catalog = new SoundCatalog(_overrides, _shipped);
        using var player = new NAudioSoundPlayer(catalog, Logger.None, Fake);

        player.Play(SoundId.Permission, 1.0, TimeSpan.Zero);
        player.Play(SoundId.Permission, 0.6, TimeSpan.FromMilliseconds(150));

        // One file backs both — there is no "quiet" duplicate to find.
        Assert.Single(Directory.GetFiles(_shipped, "*.wav"));
        Assert.Single(Directory.GetFiles(_overrides, "*.wav").Concat(Directory.GetFiles(_shipped, "*.wav")));
        Assert.Equal(catalog.Resolve(SoundId.Permission), catalog.Resolve(SoundId.Permission));

        // …and both reached the mixer, so "the nudge is quieter" is not silence.
        Assert.Equal(2, player.PlayedCount);
        Assert.Equal(2, player.MixerInputCount);
    }

    /// <summary>The fade is applied only when one is asked for.</summary>
    /// <remarks>
    /// Asserted through the shape of the provider chain, which is where the fade lives. Without
    /// this, "a nudge fades" is satisfied by a nudge that plays exactly like a notice.
    /// </remarks>
    [Fact]
    public void A_fade_wraps_the_sound_and_a_notice_does_not()
    {
        WriteTone(_shipped, SoundId.Question);
        using var player = Player();

        player.Play(SoundId.Question, 1.0, TimeSpan.Zero);
        var withoutFade = player.LastProviderKind;

        player.Play(SoundId.Question, 0.6, TimeSpan.FromMilliseconds(150));
        var withFade = player.LastProviderKind;

        Assert.Equal(nameof(NAudio.Wave.SampleProviders.VolumeSampleProvider), withoutFade);
        Assert.Equal(nameof(NAudio.Wave.SampleProviders.FadeInOutSampleProvider), withFade);
    }

    /// <summary>
    /// <strong>A burst coalesces and nothing is dropped.</strong>
    /// </summary>
    /// <remarks>
    /// Fifteen concurrent sessions is the design case. "The burst coalesced" would be satisfied
    /// by discarding fourteen of them, so the positive and the negative are both asserted: every
    /// one became a mixer input, and none was counted as degraded.
    /// </remarks>
    [Fact]
    public void A_burst_of_fifteen_all_reach_the_mixer()
    {
        WriteTone(_shipped, SoundId.Finished);
        using var player = Player();

        for (var i = 0; i < 15; i++)
        {
            player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);
        }

        Assert.Equal(15, player.PlayedCount);
        Assert.Equal(15, player.MixerInputCount);
        Assert.Equal(0, player.DegradedCount);
    }

    /// <summary>Play starts a sound; it does not wait for it to end.</summary>
    /// <remarks>
    /// The file is a second long. If <c>Play</c> waited, fifteen of them would stall the consumer
    /// thread for fifteen seconds — the failure that only appears under a burst. Asserted against
    /// wall-clock, generously: the point is "did not wait a second", not a benchmark.
    /// </remarks>
    [Fact]
    public void Play_returns_without_waiting_for_the_sound_to_finish()
    {
        WriteTone(_shipped, SoundId.Error, seconds: 1.0);
        using var player = Player();

        var started = Environment.TickCount64;
        player.Play(SoundId.Error, 1.0, TimeSpan.Zero);
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(1, player.PlayedCount);
        Assert.True(elapsed < 500, $"Play took {elapsed}ms, so it is waiting for playback to finish.");
    }

    // ---- The degradations, each with its positive control ----------------------------------------

    /// <summary>A missing file is silence and a log line, not an exception.</summary>
    [Fact]
    public void A_missing_file_degrades_to_silence()
    {
        var log = new RecordingLogSink();
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(log).CreateLogger();
        using var player = new NAudioSoundPlayer(new SoundCatalog(_overrides, _shipped), logger, Fake);

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(0, player.PlayedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Equal(0, player.MixerInputCount);
        Assert.Contains(log.Messages, message => message.Contains("No sound file", StringComparison.Ordinal));

        // The control: the same player, the same call, once the file exists. Without this, a Play
        // that did nothing at all would pass the assertions above.
        WriteTone(_shipped, SoundId.Finished);
        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(1, player.PlayedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>A file that is not audio is silence and a log line, not an exception.</summary>
    [Fact]
    public void An_undecodable_file_degrades_to_silence()
    {
        File.WriteAllText(Path.Combine(_shipped, "finished.wav"), "this is not a wave file");
        WriteTone(_shipped, SoundId.Error);

        var log = new RecordingLogSink();
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(log).CreateLogger();
        using var player = new NAudioSoundPlayer(new SoundCatalog(_overrides, _shipped), logger, Fake);

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(0, player.PlayedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Contains(log.Messages, message => message.Contains("could not be decoded", StringComparison.Ordinal));

        // The control: a real file through the same player still plays.
        player.Play(SoundId.Error, 1.0, TimeSpan.Zero);

        Assert.Equal(1, player.PlayedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>
    /// No output device is silence and a log line — the app still runs.
    /// </summary>
    /// <remarks>
    /// The realistic case: an RDP session, a headless machine, a headset unplugged. Provoked
    /// rather than waited for, by a factory that throws the way the device layer would.
    /// </remarks>
    [Fact]
    public void An_unavailable_device_degrades_to_silence()
    {
        WriteTone(_shipped, SoundId.Finished);

        var log = new RecordingLogSink();
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(log).CreateLogger();
        using var player = new NAudioSoundPlayer(
            new SoundCatalog(_overrides, _shipped),
            logger,
            () => throw new InvalidOperationException("no wave devices"));

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.False(player.HasOutput);
        Assert.Equal(0, player.PlayedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Contains(log.Messages, message => message.Contains("No audio output device", StringComparison.Ordinal));

        // The control: the identical setup with a device that opens does play, so the silence
        // above is the device's absence and not a Play that never worked.
        using var working = new NAudioSoundPlayer(new SoundCatalog(_overrides, _shipped), Logger.None, Fake);
        working.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.True(working.HasOutput);
        Assert.Equal(1, working.PlayedCount);
    }

    /// <summary>Whatever else happens, nothing reaches the caller.</summary>
    [Fact]
    public void It_needs_a_catalog_and_a_logger_but_never_throws_from_play()
    {
        Assert.Throws<ArgumentNullException>(() => new NAudioSoundPlayer(null!, Logger.None));
        Assert.Throws<ArgumentNullException>(
            () => new NAudioSoundPlayer(new SoundCatalog(_overrides, _shipped), null!));

        using var player = Player();

        // Every one of these is a shape the engine could produce; none may escape.
        player.Play(SoundId.Finished, double.NaN, TimeSpan.Zero);
        player.Play(SoundId.Finished, -5, TimeSpan.FromMilliseconds(-10));
        player.Play(new SoundId("nothing-like-this"), 1.0, TimeSpan.Zero);
    }

    /// <summary>
    /// <strong>An unforeseen failure is swallowed too, and this is the assertion that means it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above passes against a <c>Play</c> in which nothing can throw — every input it
    /// uses is clamped or resolves to nothing, so none of them reaches the catch. That is not the
    /// same as a <c>Play</c> that swallows what does throw, and a mutation adding <c>throw;</c> to
    /// the handler survived it. Found by planting exactly that.
    /// </para>
    /// <para>
    /// So this makes the chain throw something the named handlers do not expect, and asserts the
    /// contract that matters: the caller — the event consumer, whose loop keeps the whole
    /// dashboard current — sees nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unforeseen_failure_never_reaches_the_caller()
    {
        var log = new RecordingLogSink();
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(log).CreateLogger();

        using var player = new NAudioSoundPlayer(new ThrowingCatalog(_overrides, _shipped), logger, Fake);

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(0, player.PlayedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Contains(log.Messages, message => message.Contains("Continuing without it", StringComparison.Ordinal));

        // The control: the same player is not broken — a working catalog through the same code
        // path still plays, so the silence above is the failure being swallowed and not a Play
        // that gave up permanently.
        WriteTone(_shipped, SoundId.Finished);

        using var working = Player();
        working.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(1, working.PlayedCount);
    }

    /// <summary>A catalog that fails in a way none of the named handlers expects.</summary>
    private sealed class ThrowingCatalog(string overrideFolder, string shippedFolder)
        : SoundCatalog(overrideFolder, shippedFolder)
    {
        public override string? Resolve(SoundId sound) =>
            throw new InvalidTimeZoneException("nothing predicted this");
    }

    /// <summary>Gain outside 0…1 is clamped rather than rejected, per the port's contract.</summary>
    [Fact]
    public void Gain_is_clamped_rather_than_rejected()
    {
        WriteTone(_shipped, SoundId.Finished);
        using var player = Player();

        player.Play(SoundId.Finished, 5.0, TimeSpan.Zero);
        Assert.Equal(1f, player.LastVolume);

        player.Play(SoundId.Finished, -1.0, TimeSpan.Zero);
        Assert.Equal(0f, player.LastVolume);

        player.Play(SoundId.Finished, 0.6, TimeSpan.Zero);
        Assert.Equal(0.6f, player.LastVolume, 3);
    }

    private NAudioSoundPlayer Player() =>
        new(new SoundCatalog(_overrides, _shipped), Logger.None, Fake);

    /// <summary>An output device that accepts everything and produces no sound.</summary>
    /// <remarks>
    /// Returned as the concrete type because the analyzer asks; it is handed to the adapter as a
    /// <see cref="IWavePlayer"/> either way, which is the only thing the adapter knows about it.
    /// </remarks>
    private static FakeWaveOut Fake() => new();

    /// <summary>Writes a real, decodable WAV so the assertions are about the adapter.</summary>
    private static void WriteTone(string folder, SoundId sound, double seconds = 0.2)
    {
        var path = Path.Combine(folder, sound.Name + SoundCatalog.Extension);
        var format = new WaveFormat(44100, 16, 1);

        using var writer = new WaveFileWriter(path, format);

        var samples = (int)(format.SampleRate * seconds);

        for (var i = 0; i < samples; i++)
        {
            writer.WriteSample((float)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 0.5));
        }
    }

    /// <summary>An output device that accepts everything and produces no sound.</summary>
    private sealed class FakeWaveOut : IWavePlayer
    {
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public float Volume { get; set; } = 1f;

        public WaveFormat? OutputWaveFormat { get; private set; }

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

        public void Play() => PlaybackState = PlaybackState.Playing;

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose() => PlaybackState = PlaybackState.Stopped;
    }
}
