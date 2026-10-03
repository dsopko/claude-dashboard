using System.ComponentModel;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// The notice that the dashboard fell behind and shed repeated events (T1.58, issue #3).
/// </summary>
/// <remarks>
/// <para>
/// A shed tool batch can leave a row behind the session for a moment, so the operator is told,
/// and told that each session's next event puts its row right. It shows while noise was shed
/// in the last <see cref="ShowsFor"/>, and clears that long after the last shed, on the tick.
/// </para>
/// <para>
/// <strong>Read on the tick, not pushed.</strong> The pipeline is written on request threads; the
/// notice is bound on the UI thread. <see cref="Tick"/> reads the pipeline's last shed instant,
/// published with <see cref="System.Threading.Interlocked"/>, on the tick that refreshes the tray.
/// </para>
/// </remarks>
public sealed class FellBehindNotice : INotice, IUiTickTarget
{
    /// <summary>The window's text.</summary>
    public const string WindowText =
        "The dashboard fell behind and skipped repeated tool events. Rows may lag until each session's next event.";

    /// <summary>The tray tooltip's short form.</summary>
    public const string TrayShort = "fell behind";

    /// <summary>How long the notice shows after the last shed.</summary>
    public static readonly TimeSpan ShowsFor = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset?> _lastShed;

    /// <summary>Creates the notice.</summary>
    /// <param name="lastShed">When noise was last shed, or null if never. Called on the UI thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lastShed"/> is null.</exception>
    public FellBehindNotice(Func<DateTimeOffset?> lastShed)
    {
        ArgumentNullException.ThrowIfNull(lastShed);

        _lastShed = lastShed;
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
        var shown = _lastShed() is { } last && now - last < ShowsFor;

        if (shown == IsShown)
        {
            return;
        }

        IsShown = shown;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
    }
}

/// <summary>
/// The notice that the dashboard lost events at the hard limit (T1.58, issue #3).
/// </summary>
/// <remarks>
/// A lost event may have been one that changes a row, so a row may be wrong until its session's
/// next event. The operator is told so, and that a restart is the way to be sure. It stays until
/// the next start: nothing during this run can say the lost events did not matter.
/// </remarks>
public sealed class EventsLostNotice : INotice, IUiTickTarget
{
    /// <summary>The window's text.</summary>
    public const string WindowText =
        "The dashboard fell far behind and lost events. A row may be wrong until its session's next event; restart the dashboard to be sure.";

    /// <summary>The tray tooltip's short form.</summary>
    public const string TrayShort = "events lost";

    private readonly Func<long> _lost;

    /// <summary>Creates the notice.</summary>
    /// <param name="lost">How many events were lost since the start. Called on the UI thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lost"/> is null.</exception>
    public EventsLostNotice(Func<long> lost)
    {
        ArgumentNullException.ThrowIfNull(lost);

        _lost = lost;
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
        if (IsShown || _lost() == 0)
        {
            return;
        }

        IsShown = true;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
    }
}
