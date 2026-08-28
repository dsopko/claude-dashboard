using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>One Windows audio output endpoint, named by the identity Windows gives it.</summary>
/// <param name="Id">
/// The endpoint ID string. Opaque — pass it back to Windows or compare it, never parse it.
/// </param>
/// <param name="Name">The name a person would recognise, for the log and nothing else.</param>
internal readonly record struct AudioEndpoint(string Id, string Name)
{
    /// <summary>Both halves, because the name alone does not identify a device.</summary>
    /// <remarks>
    /// Two endpoints can share a friendly name — this machine enumerates four called
    /// "NVIDIA Output" — so a log line carrying only the name cannot tell the operator which one
    /// the dashboard bound. The ID is what distinguishes them.
    /// </remarks>
    public override string ToString() => Name.Length == 0 ? Id : $"{Name} ({Id})";
}

/// <summary>An open output, and the endpoint the player itself says it opened.</summary>
/// <param name="Player">The playback device.</param>
/// <param name="Endpoint">
/// What the player reports, <strong>not what it was asked for</strong>. The distinction is the
/// whole point: "a device is bound" is satisfied by binding to the same dead one, so the readout
/// the operator judges the fix by has to come from something the adapter does not control.
/// </param>
internal sealed record AudioOutput(IWavePlayer Player, AudioEndpoint Endpoint);

/// <summary>
/// The Windows side of sound output: which endpoint is default, how to open one, and how to hear
/// that the set of them changed (issue #13).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This interface exists because the old seam could not express the defect.</strong> The
/// adapter used to take a <c>Func&lt;IWavePlayer&gt;</c>, which has no notion of <em>which</em>
/// device it opens. Every test passed a factory that ignored the question, so every test would
/// have kept passing while the product bound to nothing in particular. A seam that cannot say
/// "open the endpoint that is now default" cannot test a fault about following the default.
/// </para>
/// <para>
/// It is deliberately not a port in Core. Core knows nothing of devices, and
/// <see cref="ClaudeDashboard.Core.Ports.ISoundPlayer"/> does not grow a member for this: it is
/// an adapter fault and it is fixed inside the adapter.
/// </para>
/// </remarks>
internal interface IAudioEndpoints
{
    /// <summary>The endpoint Windows would play a notification sound on, or null if there is none.</summary>
    AudioEndpoint? Default { get; }

    /// <summary>Opens <paramref name="endpoint"/> for playback at <paramref name="format"/>.</summary>
    /// <exception cref="Exception">
    /// Any failure to open. The caller treats every one the same way — a strike against this
    /// endpoint and a degradation — so this deliberately promises no particular exception type.
    /// </exception>
    AudioOutput Open(AudioEndpoint endpoint, WaveFormat format);

    /// <summary>
    /// Calls <paramref name="onChanged"/> whenever the endpoints change. Dispose to unregister.
    /// </summary>
    /// <remarks>
    /// <paramref name="onChanged"/> runs on a thread that is not ours and must return at once —
    /// see <see cref="WindowsAudioEndpoints.Watch"/>.
    /// </remarks>
    IDisposable Watch(Action onChanged);
}

/// <summary>
/// <see cref="IAudioEndpoints"/> over WASAPI (NAudio 3.0.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>WASAPI rather than WinMM, and an explicit endpoint rather than stream routing.</strong>
/// NAudio 3.0.1 offers a third option this does not take:
/// <c>WasapiPlayerBuilder.WithDefaultDeviceStreamRouting()</c> asks Windows to follow the default
/// device with no application code at all, and it works. It is rejected for one decisive reason
/// and one supporting one. The decisive reason: under routing there is no fixed endpoint, so
/// <c>WasapiPlayer.DeviceId</c> and <c>DeviceFriendlyName</c> are both null — measured, and
/// documented — and the only readout left would be what we separately believe the default to be.
/// That is a different fact from what the player opened, the two can disagree, and the whole
/// acceptance of issue #13 rests on the operator being able to see which endpoint is bound. The
/// supporting reason: routing cannot start with no device at all, so the notification
/// subscription below would be needed anyway.
/// </para>
/// <para>
/// <strong>Shared mode, and no low-latency path, and this is load-bearing.</strong> The mixer
/// runs at a fixed 44.1kHz while the device on this machine mixes at 48kHz. That works only
/// because standard shared mode converts in the Windows audio engine, sample rate included —
/// verified here by <c>GetPlaybackCapability</c> at every bind rather than trusted. The
/// <c>IAudioClient3</c> low-latency path does <em>not</em> resample, so adding
/// <c>WithLowLatency</c> would break the format, and it would break it silently.
/// </para>
/// <para>
/// <strong>The obsolete API is not used.</strong> <c>WasapiOut</c> carries
/// <see cref="ObsoleteAttribute"/> in NAudio 3.0.1 and <c>WaveOut</c> cannot name an endpoint at
/// all.
/// </para>
/// </remarks>
internal sealed class WindowsAudioEndpoints : IAudioEndpoints, IDisposable
{
    /// <summary>
    /// The device role the dashboard's sounds belong to.
    /// </summary>
    /// <remarks>
    /// A literal because an external authority owns the value. Microsoft defines the Core Audio
    /// roles as: <c>eConsole</c> — games, system notification sounds and voice commands;
    /// <c>eMultimedia</c> — music, movies, narration; <c>eCommunications</c> — talking to another
    /// person. What this application plays is a system notification sound and nothing else.
    /// <c>eCommunications</c> is wrong on purpose: a notice must not follow the audio path of a
    /// call the operator is in.
    /// </remarks>
    private const Role NotificationRole = Role.Console;

    private readonly ILogger _logger;
    private readonly MMDeviceEnumerator _enumerator = new();

    private MMDeviceNotificationClient? _client;
    private bool _disposed;

    /// <summary>Creates the enumerator this holds for its lifetime.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public WindowsAudioEndpoints(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc/>
    public AudioEndpoint? Default
    {
        get
        {
            // Try rather than Get: "there is no default endpoint" is an ordinary state on a
            // machine with every output disabled, and it should not arrive as an exception.
            if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, NotificationRole, out var device)
                || device is null)
            {
                return null;
            }

            using (device)
            {
                return new AudioEndpoint(device.ID, device.FriendlyName);
            }
        }
    }

    /// <inheritdoc/>
    public AudioOutput Open(AudioEndpoint endpoint, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        // The player does not need the MMDevice to outlive the build — measured: a player whose
        // device was disposed immediately afterwards still initialised, played, and reported its
        // own endpoint. So the device is scoped here rather than held for the player's lifetime.
        using var device = _enumerator.GetDevice(endpoint.Id);

        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .Build();

        try
        {
            var capability = player.GetPlaybackCapability(format);

            if (!capability.Supported)
            {
                // Documented as unreachable in shared mode, which is exactly why it is checked
                // rather than assumed: the promise costs one call to verify, and the failure it
                // guards against is a wrong format that nothing else would report.
                throw new InvalidOperationException(
                    $"{endpoint} will not accept {format}. {capability.Reason}");
            }

            return new AudioOutput(
                player,
                new AudioEndpoint(
                    player.DeviceId ?? endpoint.Id,
                    player.DeviceFriendlyName ?? endpoint.Name));
        }
        catch
        {
            player.Dispose();

            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <strong>The callback posts and returns; it never opens a device.</strong> NAudio's own
    /// documentation says these arrive "on a Windows audio system worker thread that holds an
    /// internal lock while dispatching the notification", and must not block or call back into
    /// the audio stack. Opening an endpoint does both.
    /// </para>
    /// <para>
    /// <strong>No synchronization context, deliberately.</strong> With one, NAudio marshals to
    /// the context captured where the client was created — and this class is built by dependency
    /// injection, which may well be running on the WPF UI thread. That would post every device
    /// open onto the thread that draws the window, to fix the sound.
    /// </para>
    /// <para>
    /// <strong>All four events, unfiltered, and one handler between them.</strong> Filtering to
    /// render endpoints would mean a COM lookup per notification on the very thread that must not
    /// be blocked; a spurious wake costs the caller one string comparison. One handler because no
    /// caller should care which of the four arrived: that is what makes a first device appearing
    /// take the same path as a later change rather than being a special case.
    /// <c>PropertyValueChanged</c> is not taken — it fires for volume and peak-meter movement.
    /// </para>
    /// </remarks>
    public IDisposable Watch(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);

        client.DefaultDeviceChanged += (_, _) => onChanged();
        client.DeviceAdded += (_, _) => onChanged();
        client.DeviceRemoved += (_, _) => onChanged();
        client.DeviceStateChanged += (_, _) => onChanged();

        _client = client;

        return client;
    }

    /// <summary>Unregisters the notification client and releases the enumerator.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _client?.Dispose();
            _enumerator.Dispose();
        }
        catch (Exception ex)
        {
            // Releasing COM must not take the process down on the way out.
            _logger.Warning(ex, "Releasing the audio endpoint enumerator failed.");
        }
    }
}
