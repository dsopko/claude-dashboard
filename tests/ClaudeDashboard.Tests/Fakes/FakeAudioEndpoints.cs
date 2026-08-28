using ClaudeDashboard.App.Adapters;
using NAudio.Wave;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// The Windows audio stack, driven by the test (issue #13, T1.22).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the old seam had to go.</strong> The adapter used to take a
/// <c>Func&lt;IWavePlayer&gt;</c>. Nothing in that shape can say <em>which</em> device is being
/// opened, so no test written against it could fail when the product bound to the wrong one —
/// which is the entire defect. This fake names endpoints, decides which is default, changes that
/// answer, and raises the notification, all with no hardware.
/// </para>
/// <para>
/// <strong>What it cannot do, which is most of the risk.</strong> It raises a change because a
/// test told it to. It cannot show that Windows raises anything on a real unplug, which of the
/// four notifications arrive, or in what order — that premise is shared with the code, so no test
/// here can fail on it. Nor can it show that a real endpoint makes a sound. Those live on the
/// hardware acceptance card.
/// </para>
/// </remarks>
internal sealed class FakeAudioEndpoints : IAudioEndpoints
{
    private readonly object _gate = new();
    private readonly List<AudioEndpoint> _opened = [];

    private Action? _onChanged;
    private AudioEndpoint? _default;

    /// <summary>Creates a fake with no default endpoint — a machine with every output disabled.</summary>
    public FakeAudioEndpoints(AudioEndpoint? initial = null) => _default = initial;

    /// <summary>The endpoint <see cref="Default"/> answers with. Settable from the test.</summary>
    public AudioEndpoint? Default
    {
        get
        {
            lock (_gate)
            {
                return _default;
            }
        }

        set
        {
            lock (_gate)
            {
                _default = value;
            }
        }
    }

    /// <summary>Thrown by <see cref="Open"/> when set, the way a busy device layer would.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>Runs on the worker thread inside <see cref="Open"/>, before anything is created.</summary>
    /// <remarks>
    /// The hook that makes the disposal race testable: a test can hold the worker inside a slow
    /// open and dispose the adapter under it.
    /// </remarks>
    public Action<AudioEndpoint>? BeforeOpen { get; set; }

    /// <summary>What the opened player claims it opened. Defaults to what was asked for.</summary>
    /// <remarks>
    /// Settable so a test can make the two differ. The adapter must report the player's answer,
    /// not its own request, or the readout the hardware acceptance is judged by would be
    /// satisfiable by an adapter that bound nothing at all.
    /// </remarks>
    public Func<AudioEndpoint, AudioEndpoint>? Reports { get; set; }

    /// <summary>Whether every opened player stops the instant it is played — a hung endpoint.</summary>
    public bool StopOnPlay { get; set; }

    /// <summary>Every endpoint that was opened, in order.</summary>
    public IReadOnlyList<AudioEndpoint> Opened
    {
        get
        {
            lock (_gate)
            {
                return [.. _opened];
            }
        }
    }

    /// <summary>How many times a device was opened.</summary>
    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _opened.Count;
            }
        }
    }

    /// <summary>The most recently opened player, so a test can stop it or check its disposal.</summary>
    public FakeWaveOut? LastPlayer { get; private set; }

    /// <summary>Whether a subscription is live right now.</summary>
    public bool Watching { get; private set; }

    /// <summary>How many times the subscription was disposed.</summary>
    public int WatchDisposals { get; private set; }

    /// <inheritdoc/>
    public AudioOutput Open(AudioEndpoint endpoint, WaveFormat format)
    {
        BeforeOpen?.Invoke(endpoint);

        lock (_gate)
        {
            _opened.Add(endpoint);
        }

        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        var player = new FakeWaveOut { StopOnPlay = StopOnPlay };
        LastPlayer = player;

        return new AudioOutput(player, Reports?.Invoke(endpoint) ?? endpoint);
    }

    /// <inheritdoc/>
    public IDisposable Watch(Action onChanged)
    {
        _onChanged = onChanged;
        Watching = true;

        return new Subscription(this);
    }

    /// <summary>Raises the endpoint-changed notification, the way Windows would.</summary>
    /// <remarks>
    /// Does nothing once the subscription is disposed, which is what makes "Dispose unregisters"
    /// assertable rather than merely visible in the code.
    /// </remarks>
    public void RaiseChanged()
    {
        if (Watching)
        {
            _onChanged?.Invoke();
        }
    }

    private sealed class Subscription(FakeAudioEndpoints owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Watching = false;
            owner.WatchDisposals++;
        }
    }
}

/// <summary>An output device that accepts everything and produces no sound.</summary>
/// <remarks>
/// It records its own disposal, which is how the tests tell a device that was released from one
/// that was leaked — the difference the disposal race is about.
/// </remarks>
internal sealed class FakeWaveOut : IWavePlayer
{
    /// <summary>Whether playing stops the stream at once — a device that opens and does not work.</summary>
    public bool StopOnPlay { get; init; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed { get; private set; }

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

    public float Volume { get; set; } = 1f;

    public WaveFormat? OutputWaveFormat { get; private set; }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

    public void Play()
    {
        PlaybackState = PlaybackState.Playing;

        if (StopOnPlay)
        {
            RaiseStopped(new InvalidOperationException("the endpoint went away"));
        }
    }

    public void Pause() => PlaybackState = PlaybackState.Paused;

    public void Stop()
    {
        PlaybackState = PlaybackState.Stopped;
        RaiseStopped(null);
    }

    /// <summary>Reports that the stream stopped, the way NAudio does when a device is invalidated.</summary>
    public void RaiseStopped(Exception? error)
    {
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    /// <summary>
    /// Releases the device, and reports the stop that NAudio reports here.
    /// </summary>
    /// <remarks>
    /// <strong>The stop on disposal is the point, not an accident of the fake.</strong> A real
    /// <c>WasapiPlayer</c> raises <c>PlaybackStopped</c> when it is disposed — measured, with a
    /// null exception. Without it here, the fake would be kinder than the real thing and the
    /// adapter's guard against counting its own shutdown as a device fault would never be
    /// exercised.
    /// </remarks>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }
}
