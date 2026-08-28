using System.Collections.Concurrent;
using System.IO;
using ClaudeDashboard.Core.Ports;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// Plays the dashboard's sounds through NAudio, following the default output device (Impl Part 7,
/// issue #13).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It decides nothing.</strong> The engine hands it a sound, a gain and a fade, and it
/// plays exactly that. There is no mute here and no notion of a session or a group — mute is
/// policy and lives in <c>SoundPolicyEngine</c>, which proves it by never calling this at all.
/// A second mute in the adapter would be the same rule in two code paths.
/// </para>
/// <para>
/// <strong>One device at a time, one mixer per device.</strong> A resident app that opened a
/// device per beep would pay the open latency on every notice and would stack fifteen independent
/// outputs when fifteen sessions finish together. Instead one endpoint is open at any moment and
/// every sound is added as an input to that endpoint's single
/// <see cref="MixingSampleProvider"/>, which sums them — the burst becomes one stream rather than
/// fifteen, which is Impl Part 7's stated reason for wanting a mixer.
/// </para>
/// <para>
/// <strong>The device is re-opened when Windows changes it, and that is the whole of T1.22.</strong>
/// It used to be opened once in the constructor and held forever, so unplugging a headset left
/// the dashboard silent until restart, with an empty log and a rising "played" count. The player
/// and its mixer are now replaced together as one immutable <see cref="Bound"/> pair, so a caller
/// sees either the whole old pair or the whole new one and never a mixer belonging to a different
/// device. Sounds already queued on a pair being replaced are dropped rather than replayed: a
/// notice raised during the swap is stale by the time the new device opens.
/// </para>
/// <para>
/// <strong>Two oracles, and neither can produce the other's evidence.</strong> An endpoint
/// notification says which endpoint <em>should</em> be bound; <see cref="IWavePlayer.PlaybackStopped"/>
/// says <em>this stream is dead</em> and arrives for a device that is still listed and still
/// reports itself active. One alone would leave a hole: the first cannot see a hung endpoint, and
/// the second cannot see a device that appeared when there had never been one.
/// </para>
/// <para>
/// <strong>Samples are decoded once and cached.</strong> A burst re-reading the same file
/// fifteen times would do fifteen file opens on the consumer thread; the decode happens on the
/// first play of each sound and never again. There are four sounds and they are a fraction of a
/// second each, so the cache is bounded by the enum. The cache survives a device change — it
/// holds samples, not device state.
/// </para>
/// <para>
/// <strong>Never throws, and that is a contract rather than caution.</strong> Audio is the least
/// important thing this application does. A missing file, a device that will not open, a file
/// that will not decode — each degrades to silence and a log line. The caller is the event
/// consumer, and an exception here would take down the loop that keeps the whole dashboard
/// current, to say nothing of the beep.
/// </para>
/// <para>
/// <strong>What this still cannot see.</strong> The false "played" reading is narrowed, not
/// removed. <see cref="HasOutput"/> becomes false when Windows reports no default endpoint, and
/// when the current stream reports itself stopped. It does not become false for a device that is
/// listed, reports itself active, accepts a stream, and is nonetheless inaudible. Nothing this
/// process can ask would distinguish that from a working device with the volume down.
/// </para>
/// </remarks>
public sealed class NAudioSoundPlayer : ISoundPlayer, IDisposable
{
    /// <summary>How many failures against one endpoint before the adapter stops trying it.</summary>
    /// <remarks>
    /// Without a bound, an endpoint that opens and immediately dies gives re-bind, stop, re-bind,
    /// for as long as the process runs. Comparing endpoint IDs does not catch it, because the ID
    /// is the same one every time.
    /// </remarks>
    public const int DefaultMaxStrikes = 3;

    /// <summary>
    /// How long a newly bound stream must survive before its stopping counts as ordinary rather
    /// than as a failure to open.
    /// </summary>
    /// <remarks>
    /// A stream that plays for an hour and then stops has told us the device went away, and the
    /// answer is to re-bind. A stream that stops within a moment of opening has told us the
    /// device does not work, and re-binding to it is the loop above.
    /// </remarks>
    public static readonly TimeSpan DefaultStrikeWindow = TimeSpan.FromSeconds(2);

    /// <summary>How long to let a burst of notifications settle before resolving the default.</summary>
    /// <remarks>
    /// One unplug raises several notifications within milliseconds — removed, state changed,
    /// default changed — and resolving during that raises the odds of binding to something that
    /// is itself about to change. A quarter of a second is nothing against a notification sound.
    /// </remarks>
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>How long <see cref="Dispose"/> waits for the endpoint worker, as a courtesy.</summary>
    /// <remarks>
    /// <strong>This wait is not what stops a device leaking.</strong> A timeout cannot be the
    /// protection, because the protection would then expire exactly when a slow driver made it
    /// matter. What protects is in <see cref="Rebind"/>: a worker that opened a device takes the
    /// gate before publishing it, sees the disposal, and releases what it opened instead. This
    /// wait only makes the common case tidy.
    /// </remarks>
    private static readonly TimeSpan WorkerShutdownWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The format everything is mixed at: 44.1kHz stereo float.
    /// </summary>
    /// <remarks>
    /// Fixed rather than taken from the first file, because a mixer has one format and the
    /// second sound to arrive would otherwise be the one that failed. Mono sources are widened
    /// to stereo on the way in.
    /// <para>
    /// It survives a device that mixes at some other rate only because the output is opened in
    /// standard WASAPI shared mode, where the Windows audio engine converts the sample rate
    /// itself. <see cref="WindowsAudioEndpoints"/> checks that at every bind rather than trusting
    /// it, and says why.
    /// </para>
    /// </remarks>
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly ConcurrentDictionary<SoundId, float[]> _decoded = new();
    private readonly SoundCatalog _catalog;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly IAudioEndpoints _endpoints;
    private readonly bool _ownsEndpoints;
    private readonly int _maxStrikes;
    private readonly TimeSpan _strikeWindow;
    private readonly TimeSpan _settle;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _worker;
    private readonly IDisposable? _watch;

    private Bound? _bound;
    private bool _disposed;
    private string? _silentReason;
    private int _droppedWhileSilent;
    private string? _struckEndpointId;
    private int _strikes;
    private int _rebindPasses;

    /// <summary>Creates the player, binds the current default endpoint, and follows it thereafter.</summary>
    /// <param name="catalog">Where sound files are found.</param>
    /// <param name="logger">Where degradations are recorded.</param>
    /// <param name="clock">
    /// Used only to measure how long a bound stream survived, which is what separates a device
    /// that went away from one that does not work.
    /// </param>
    /// <remarks>
    /// The only public constructor, and the one dependency injection uses. The Windows side and
    /// the two limits are chosen for it — they are an adapter's internal business, and a caller
    /// who could pass a different audio stack would be a caller who could make this class lie.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public NAudioSoundPlayer(SoundCatalog catalog, ILogger logger, IClock clock)
        : this(catalog, logger, clock, endpoints: null)
    {
    }

    /// <summary>Creates the player over a given audio stack, for tests.</summary>
    /// <param name="catalog">Where sound files are found.</param>
    /// <param name="logger">Where degradations are recorded.</param>
    /// <param name="clock">
    /// Used only to measure how long a bound stream survived, which is what separates a device
    /// that went away from one that does not work.
    /// </param>
    /// <param name="endpoints">
    /// The Windows side. Defaults to <see cref="WindowsAudioEndpoints"/>; tests pass a fake that
    /// names endpoints, changes which is default, and raises the notification on demand. When it
    /// is defaulted this object owns it and disposes it; when it is passed in, the caller does.
    /// </param>
    /// <param name="maxStrikes">Overrides <see cref="DefaultMaxStrikes"/>.</param>
    /// <param name="strikeWindow">Overrides <see cref="DefaultStrikeWindow"/>.</param>
    /// <param name="settle">Overrides <see cref="DefaultSettle"/>. Tests pass zero.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalog"/>, <paramref name="logger"/> or <paramref name="clock"/> is null.
    /// </exception>
    internal NAudioSoundPlayer(
        SoundCatalog catalog,
        ILogger logger,
        IClock clock,
        IAudioEndpoints? endpoints,
        int maxStrikes = DefaultMaxStrikes,
        TimeSpan? strikeWindow = null,
        TimeSpan? settle = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStrikes, 1);

        _catalog = catalog;
        _logger = logger;
        _clock = clock;
        _ownsEndpoints = endpoints is null;
        _endpoints = endpoints ?? new WindowsAudioEndpoints(logger);
        _maxStrikes = maxStrikes;
        _strikeWindow = strikeWindow ?? DefaultStrikeWindow;
        _settle = settle ?? DefaultSettle;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "sound-endpoint",
        };

        // Subscribe first, bind second, start the worker third, and the order is the whole race.
        // A notification that arrives during the first bind sets the wake handle, which nothing
        // is consuming yet, so the worker picks it up the moment it starts. Subscribing after the
        // bind instead would drop a change that landed in between; starting the worker before the
        // bind would let two threads open a device at once.
        try
        {
            _watch = _endpoints.Watch(Signal);
        }
        catch (Exception ex)
        {
            // Sound that does not follow the device is still sound. This is the degradation the
            // whole task is about, so it is reported at Error rather than swallowed.
            _logger.Error(
                ex,
                "Audio device changes cannot be watched. Sound will not follow the default device "
                + "until the dashboard restarts.");
        }

        Rebind();

        _worker.Start();
    }

    /// <summary>Whether a working output device is bound. Diagnostic only.</summary>
    /// <remarks>
    /// False when Windows reports no default endpoint, when every attempt at the current one has
    /// failed, and when the bound stream has reported itself stopped. It is <em>not</em> false
    /// for a device that is listed, active, and inaudible — see the note on the class.
    /// </remarks>
    public bool HasOutput
    {
        get
        {
            lock (_gate)
            {
                return _bound is { Stopped: false };
            }
        }
    }

    /// <summary>How many sounds have been handed to the mixer. Diagnostic only.</summary>
    /// <remarks>
    /// <strong>Queued, not heard, and the name now says so.</strong> It was called
    /// <c>PlayedCount</c> and it never meant that: adding an input to a mixer that NAudio reads
    /// on its own thread tells you nothing about whether a sound left the machine. Issue #13's
    /// sharpest symptom was this number rising for sounds nobody heard, and a name that claims
    /// more than it can support is how that went unnoticed.
    /// </remarks>
    public int QueuedCount { get; private set; }

    /// <summary>How many were asked for and could not be played. Diagnostic only.</summary>
    public int DegradedCount { get; private set; }

    /// <summary>
    /// The endpoint that is bound right now as the player itself reports it, or null when none is.
    /// </summary>
    /// <remarks>
    /// This is what the hardware acceptance is read against. It is deliberately the player's
    /// answer rather than the endpoint the adapter asked for: those are different facts, they can
    /// disagree, and only one of them is evidence.
    /// </remarks>
    internal string? BoundEndpoint
    {
        get
        {
            lock (_gate)
            {
                return _bound is { Stopped: false } bound ? bound.Endpoint.ToString() : null;
            }
        }
    }

    /// <summary>Why there is no working output, or null when there is one. Diagnostic only.</summary>
    internal string? SilentReason
    {
        get
        {
            lock (_gate)
            {
                return _silentReason;
            }
        }
    }

    /// <summary>How many sounds have been dropped since the output went away. Diagnostic only.</summary>
    internal int DroppedWhileSilent
    {
        get
        {
            lock (_gate)
            {
                return _droppedWhileSilent;
            }
        }
    }

    /// <summary>Whether <see cref="Dispose"/> has run. Diagnostic only.</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>How many times the endpoint worker has finished a pass. Diagnostic only.</summary>
    /// <remarks>
    /// Exists so a test can wait for the worker to have <em>considered</em> a notification rather
    /// than sleeping for a while and hoping. A test that asserts nothing happened needs to know
    /// the work ran at all, or it is asserting that the thread was slow.
    /// </remarks>
    internal int RebindPasses => Volatile.Read(ref _rebindPasses);

    /// <summary>How many inputs the current mixer is summing. Diagnostic only.</summary>
    internal int MixerInputCount
    {
        get
        {
            lock (_gate)
            {
                return _bound?.Mixer.MixerInputs.Count() ?? 0;
            }
        }
    }

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
            lock (_gate)
            {
                DegradedCount++;
            }

            _logger.Warning(ex, "Playing {Sound} failed. Continuing without it.", sound.Name);
        }
    }

    /// <summary>Stops following the device, releases the output, and stops the worker.</summary>
    /// <remarks>
    /// <para>
    /// The order is the answer to two separate races.
    /// </para>
    /// <para>
    /// <strong>The subscription goes first</strong>, before anything is released. Windows would
    /// otherwise call into a disposed object at an arbitrary later moment. A notification already
    /// in flight is harmless because the handler only sets a wait handle.
    /// </para>
    /// <para>
    /// <strong>The worker is joined, but the join is not the protection.</strong> A worker that
    /// is part-way through opening a device cannot be hurried, and a timeout that expires would
    /// leak exactly the device it was added to protect. What protects is that the worker checks
    /// for this disposal after its open returns and before it publishes anything —
    /// see <see cref="Rebind"/>.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Bound? bound;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bound = _bound;
            _bound = null;
        }

        _watch?.Dispose();
        _stop.Set();

        var stopped = _worker.Join(WorkerShutdownWait);

        Retire(bound);

        if (_ownsEndpoints)
        {
            (_endpoints as IDisposable)?.Dispose();
        }

        if (stopped)
        {
            _wake.Dispose();
            _stop.Dispose();
        }
        else
        {
            // Not disposing the handles is deliberate: the worker may still be inside a wait on
            // them, and disposing them under it would throw on a thread with nobody to catch it.
            _logger.Warning(
                "The audio endpoint worker did not stop within {Wait}. Its wait handles are left "
                + "for the process to reclaim.",
                WorkerShutdownWait);
        }
    }

    private void PlayCore(SoundId sound, double gain, TimeSpan fade)
    {
        Bound? bound;

        lock (_gate)
        {
            bound = _disposed ? null : _bound;

            if (bound is null || bound.Stopped)
            {
                DegradedCount++;
                _droppedWhileSilent++;

                return;
            }
        }

        if (Samples(sound) is not { } samples)
        {
            lock (_gate)
            {
                DegradedCount++;
            }

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

        lock (_gate)
        {
            // The mixer is not documented as thread-safe, and Play is reached from the consumer
            // thread while NAudio reads from its own and the endpoint worker replaces the pair
            // from a third. The gate guards a reference read and a list insert — never a decode,
            // a file read, or a device being opened or released, all of which happen outside it.
            //
            // Re-checked rather than assumed: the pair can have been replaced while this sound
            // was being decoded. Adding to a mixer that is no longer bound would be a sound
            // queued on a device nobody is listening to, counted as queued.
            if (_disposed || !ReferenceEquals(_bound, bound) || bound.Stopped)
            {
                DegradedCount++;
                _droppedWhileSilent++;

                return;
            }

            LastVolume = volume;
            LastProviderKind = provider.GetType().Name;

            bound.Mixer.AddMixerInput(provider);
            QueuedCount++;
        }
    }

    /// <summary>Wakes the endpoint worker. Safe from any thread, including one that is not ours.</summary>
    /// <remarks>
    /// This is everything the notification callback does. Setting an auto-reset event is the
    /// single-slot coalesce: a burst of notifications during one re-bind collapses to one more
    /// pass, not one pass each.
    /// </remarks>
    private void Signal()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // A notification that raced the last moments of Dispose. There is nothing left to do
            // and nothing has been lost.
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (WaitHandle.WaitAny([_stop, _wake]) != 0)
            {
                // Waiting on the stop handle rather than sleeping, so shutdown does not have to
                // outlast the settle.
                if (_stop.WaitOne(_settle))
                {
                    return;
                }

                try
                {
                    Rebind();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Re-binding the audio output failed.");
                }
                finally
                {
                    Interlocked.Increment(ref _rebindPasses);
                }
            }
        }
        catch (Exception ex)
        {
            // A background thread that throws takes the process with it. Sound is the least
            // important thing here; the dashboard is not.
            try
            {
                _logger.Error(
                    ex,
                    "The audio endpoint worker stopped. Sound will not follow the default device "
                    + "until the dashboard restarts.");
            }
            catch
            {
                // Nothing left to report with.
            }
        }
    }

    /// <summary>
    /// Binds whatever endpoint is default now, if that is not already what is bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the constructor and by the worker, never by both at once and never by two
    /// threads. A first device appearing where there was none takes this path exactly as a later
    /// change does — there is no separate case for it, which is the point.
    /// </para>
    /// <para>
    /// <strong>Three things stop a burst opening five devices, and the third does the work.</strong>
    /// The wake handle coalesces; the settle lets a burst finish arriving; and then the endpoint
    /// ID is compared against what is already bound, so a notification about a device we are not
    /// using, and one that resolves back to where we already are, both cost one string comparison.
    /// </para>
    /// </remarks>
    private void Rebind()
    {
        AudioEndpoint? target;

        try
        {
            target = _endpoints.Default;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "The default audio endpoint could not be read.");
            target = null;
        }

        if (target is not { } endpoint)
        {
            GoSilent("Windows reports no default output endpoint");

            return;
        }

        string? gaveUp = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A different endpoint is a clean slate: strikes are held against one device, not
            // against the machine.
            if (_struckEndpointId is not null
                && !string.Equals(_struckEndpointId, endpoint.Id, StringComparison.Ordinal))
            {
                _struckEndpointId = null;
                _strikes = 0;
            }

            if (_bound is { Stopped: false } live
                && string.Equals(live.Endpoint.Id, endpoint.Id, StringComparison.Ordinal))
            {
                return;
            }

            if (_strikes >= _maxStrikes)
            {
                gaveUp = GaveUpOn(endpoint, _strikes);
            }
        }

        if (gaveUp is not null)
        {
            // Announced once, by the strike that reached the limit. Repeating it here would put a
            // line in the log for every notification that arrives while the fault lasts.
            GoSilent(gaveUp);

            return;
        }

        AudioOutput opened;

        try
        {
            // Outside the gate. Opening a device is slow, and a slow driver must not be able to
            // stop a sound being queued or a window being drawn.
            opened = _endpoints.Open(endpoint, MixFormat);
        }
        catch (Exception ex)
        {
            Strike(endpoint, ex, "could not be opened");

            return;
        }

        var mixer = new MixingSampleProvider(MixFormat)
        {
            // Without this the mixer reports the end of its stream the moment no input is
            // playing, and the device stops. A resident app wants a silent stream that never
            // ends, so the next notice starts instantly instead of restarting the device.
            ReadFully = true,
        };

        var bound = new Bound(opened.Player, mixer, opened.Endpoint, _clock.Now);

        try
        {
            opened.Player.PlaybackStopped += (_, args) => OnPlaybackStopped(bound, args);
            opened.Player.Init(mixer);
            opened.Player.Play();
        }
        catch (Exception ex)
        {
            Retire(bound);
            Strike(endpoint, ex, "could not be started");

            return;
        }

        if (bound.Stopped)
        {
            // It reported itself dead inside Play, before it was ever published. The strike is
            // already booked by OnPlaybackStopped, and publishing a stream that has already
            // stopped would clear the strikes below — which are the only thing bounding the
            // re-bind loop that a hung endpoint would otherwise run forever.
            Retire(bound);

            return;
        }

        Bound? previous;
        bool published;
        string? wasSilentFor;
        int dropped;

        lock (_gate)
        {
            published = !_disposed;

            if (published)
            {
                previous = _bound;
                wasSilentFor = _silentReason;
                dropped = _droppedWhileSilent;

                _bound = bound;
                _silentReason = null;
                _droppedWhileSilent = 0;
                _struckEndpointId = null;
                _strikes = 0;
            }
            else
            {
                previous = null;
                wasSilentFor = null;
                dropped = 0;
            }
        }

        if (!published)
        {
            // THE DISPOSAL PROTECTION. Dispose ran while the open was in flight, so it took a
            // null and released nothing. This thread is the only one that will ever hold this
            // device, so this thread releases it. A timed join in Dispose could not do this job:
            // the case that needs protecting is exactly the one where the open was slow.
            Retire(bound);

            return;
        }

        Retire(previous);

        if (wasSilentFor is null)
        {
            _logger.Information("Sound output bound to {Endpoint}.", bound.Endpoint.ToString());
        }
        else
        {
            // Leaving the silent state is an event and gets exactly one line, carrying what the
            // silence cost. Not a repetition every time a sound is dropped: the fault can last
            // hours, and a line per drop would bury the two lines that matter.
            _logger.Information(
                "Sound output bound to {Endpoint}. {Dropped} sound(s) were dropped while there was none.",
                bound.Endpoint.ToString(),
                dropped);
        }
    }

    /// <summary>Records a failed attempt at <paramref name="endpoint"/> and goes quiet.</summary>
    private void Strike(AudioEndpoint endpoint, Exception? error, string what)
    {
        int strikes;

        lock (_gate)
        {
            if (!string.Equals(_struckEndpointId, endpoint.Id, StringComparison.Ordinal))
            {
                _struckEndpointId = endpoint.Id;
                _strikes = 0;
            }

            strikes = ++_strikes;
        }

        _logger.Warning(
            error,
            "Audio endpoint {Endpoint} {What} (attempt {Attempt} of {Limit}).",
            endpoint.ToString(),
            what,
            strikes,
            _maxStrikes);

        GoSilent(strikes >= _maxStrikes ? GaveUpOn(endpoint, strikes) : $"{endpoint} {what}");
    }

    /// <summary>Why the adapter stopped trying, in the words the operator reads.</summary>
    /// <remarks>
    /// Its own sentence because "silent because it gave up" and "silent because nothing happened"
    /// have to be distinguishable in the log. A fix whose failure mode is indistinguishable from
    /// the defect is not a fix.
    /// </remarks>
    private static string GaveUpOn(AudioEndpoint endpoint, int strikes) =>
        $"it gave up on {endpoint} after {strikes} failed attempts";

    /// <summary>Drops the output and announces the silence, once per distinct reason.</summary>
    private void GoSilent(string reason)
    {
        Bound? retire;
        bool announce;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            retire = _bound;
            announce = GoSilentLocked(reason);
        }

        Retire(retire);

        if (announce)
        {
            _logger.Warning(
                "The dashboard has no working audio output: {Reason}. Sounds will be dropped until "
                + "an output device appears.",
                reason);
        }
    }

    /// <summary>The part of <see cref="GoSilent"/> that needs the gate. Returns whether to log.</summary>
    /// <remarks>
    /// One line per distinct reason, not one per notification. A device that fails three times
    /// gives one line for the failure and one for giving up, because those are two different
    /// things for the operator to do something about.
    /// </remarks>
    private bool GoSilentLocked(string reason)
    {
        _bound = null;

        if (string.Equals(_silentReason, reason, StringComparison.Ordinal))
        {
            return false;
        }

        _silentReason = reason;

        return true;
    }

    /// <summary>Handles a stream reporting that it stopped.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Our own teardown raises this too.</strong> NAudio raises
    /// <see cref="IWavePlayer.PlaybackStopped"/> when the player is disposed — measured, with a
    /// null exception — so without the retiring flag every deliberate device swap and every
    /// shutdown would book a strike against a perfectly healthy endpoint. That would be invisible
    /// in every test and would surface as "sound sometimes does not come back".
    /// </para>
    /// <para>
    /// A stream that ran and then stopped is not a fault of the endpoint, so it clears the
    /// strikes: the device proved it works, and whatever happens next deserves the full count of
    /// attempts again.
    /// </para>
    /// </remarks>
    private void OnPlaybackStopped(Bound bound, StoppedEventArgs args)
    {
        if (bound.Retiring)
        {
            return;
        }

        bound.Stopped = true;

        var lifetime = _clock.Now - bound.BoundAt;

        if (lifetime < _strikeWindow)
        {
            Strike(bound.Endpoint, args.Exception, "stopped immediately after opening");
        }
        else
        {
            lock (_gate)
            {
                _struckEndpointId = null;
                _strikes = 0;
            }

            _logger.Warning(
                args.Exception,
                "Audio endpoint {Endpoint} stopped after {Lifetime}. Looking for the default device.",
                bound.Endpoint.ToString(),
                lifetime);
        }

        Signal();
    }

    /// <summary>Releases a pair, marking it first so its own stop is not read as a fault.</summary>
    private void Retire(Bound? bound)
    {
        if (bound is null)
        {
            return;
        }

        bound.Retiring = true;

        try
        {
            bound.Player.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "Releasing the audio output for {Endpoint} failed.",
                bound.Endpoint.ToString());
        }
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
    /// One output device and the mixer that feeds it, replaced together and never apart.
    /// </summary>
    /// <remarks>
    /// A class rather than a record, and held in one field rather than two, because two mutable
    /// fields would let a caller read a new player with an old mixer. One reference, swapped under
    /// the gate, cannot be seen half-changed.
    /// </remarks>
    private sealed class Bound(
        IWavePlayer player,
        MixingSampleProvider mixer,
        AudioEndpoint endpoint,
        DateTimeOffset boundAt)
    {
        /// <summary>Set when this pair is being released on purpose, so its stop is not a fault.</summary>
        public volatile bool Retiring;

        /// <summary>Set when the stream reported that it stopped. Never cleared.</summary>
        public volatile bool Stopped;

        /// <summary>The open output.</summary>
        public IWavePlayer Player => player;

        /// <summary>The mixer this output is reading, and no other.</summary>
        public MixingSampleProvider Mixer => mixer;

        /// <summary>What the player said it opened.</summary>
        public AudioEndpoint Endpoint => endpoint;

        /// <summary>When it was bound, so a stop can be told from a failure to open.</summary>
        public DateTimeOffset BoundAt => boundAt;
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
