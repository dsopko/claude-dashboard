using System.ComponentModel;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// The notice that there is no sound device (T1.55, issue #72).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A silent dashboard that looks healthy is the failure it exists to prevent.</strong>
/// Sound is how the dashboard gets the operator's attention. Before T1.55, with no output device
/// (a remote-desktop session, a headset unplugged with nothing else as default, the audio service
/// stopped), every notice and nudge was dropped, the log had one line, and nothing on screen
/// changed.
/// </para>
/// <para>
/// <strong>A notice row and a tooltip line, and no mark on the tray icon</strong> (the operator's
/// ruling of 2026-10-03): the tray keeps its five colours and no other marks (Design §9).
/// </para>
/// <para>
/// <strong>Read on the tick, and it does not flash at start.</strong> <see cref="Tick"/> reads
/// <see cref="ISoundOutput.HasOutput"/> on the 15-second tick that refreshes the tray, so it shows
/// within one tick of the device going and clears at the next tick after one returns. The player
/// finishes its first attempt to bind inside its constructor, which runs while the host is built,
/// before the window or the tray exists. So the first tick already reads the real answer, and a
/// start whose device binds normally never shows this notice.
/// </para>
/// <para>
/// <strong>The limit that stays:</strong> a device that is listed, active and silent (the volume at
/// zero, a monitor with no speakers) counts as an output. Nothing this process can ask tells it
/// apart from a device that works.
/// </para>
/// </remarks>
public sealed class SoundDeviceNotice : INotice, IUiTickTarget
{
    /// <summary>The window's text.</summary>
    public const string WindowText =
        "No sound device. Notices and nudges are silent until Windows has an output device.";

    /// <summary>The tray tooltip's short form.</summary>
    public const string TrayShort = "no sound device";

    private readonly ISoundOutput _output;

    /// <summary>Creates the notice over the player's output state.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public SoundDeviceNotice(ISoundOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _output = output;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public bool IsShown { get; private set; }

    /// <inheritdoc/>
    public string? Text => IsShown ? WindowText : null;

    /// <inheritdoc/>
    public string? TrayText => IsShown ? TrayShort : null;

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        var silent = !_output.HasOutput;

        if (silent == IsShown)
        {
            return;
        }

        IsShown = silent;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
    }
}
