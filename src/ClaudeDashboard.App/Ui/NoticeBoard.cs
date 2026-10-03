using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// One source of the window's notice row: something the operator must see on screen, not only in
/// the log (T1.54, issue #71).
/// </summary>
/// <remarks>
/// <para>
/// Each source owns its texts and its rule for when it clears. The board only orders the sources
/// and shows the ones that are shown. A source that must look at the world on a clock, as the
/// history notice does, also implements <see cref="IUiTickTarget"/>, and the board passes the tick
/// on: no source has a timer or a thread of its own.
/// </para>
/// <para>
/// Raise <see cref="INotifyPropertyChanged.PropertyChanged"/> on the UI thread, or before the
/// window and the tray read the source.
/// </para>
/// </remarks>
public interface INotice : INotifyPropertyChanged
{
    /// <summary>The window's text, or null when there is nothing to say.</summary>
    string? Text { get; }

    /// <summary>The tray tooltip's short form, or null when the tooltip has nothing to lead with.</summary>
    string? TrayText { get; }

    /// <summary>Whether the window shows <see cref="Text"/>.</summary>
    bool IsShown { get; }
}

/// <summary>
/// The notices the window and the tray show, in a fixed order (T1.54, issue #71).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A short list, because two can be true at one time.</strong> The plugin is turned off and
/// the disk is full: each is a reason the screen looks quieter than the world, and neither hides
/// the other. The window shows each shown notice on its own line. The tray tooltip leads with each
/// one's short form, joined by " · ", after the ingress fault.
/// </para>
/// <para>
/// <strong>The order is the order of the sources given, and it is fixed.</strong> The connection to
/// Claude Code comes first: nothing arrives without it. Then the history. A later source (no sound
/// device, #72; settings not read, #73; port taken, #14) is one more <see cref="INotice"/> passed
/// here, and nothing in this class changes.
/// </para>
/// </remarks>
public sealed class NoticeBoard : ObservableObject, IUiTickTarget, IDisposable
{
    private readonly INotice[] _sources;
    private bool _disposed;

    /// <summary>Creates the board over <paramref name="sources"/>, in the order they are shown.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> or one of them is null.</exception>
    public NoticeBoard(params INotice[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source, nameof(sources));
        }

        _sources = [.. sources];

        foreach (var source in _sources)
        {
            source.PropertyChanged += OnSourceChanged;
        }

        Recompute();
    }

    /// <summary>The window texts of the shown notices, in order. Empty when none is shown.</summary>
    public IReadOnlyList<string> Texts { get; private set; } = [];

    /// <summary>The tray texts of the shown notices, joined by " · ", or null when none has one.</summary>
    public string? TrayText { get; private set; }

    /// <summary>Whether any notice is shown.</summary>
    public bool HasAny => Texts.Count > 0;

    /// <inheritdoc/>
    /// <remarks>Passed on to each source that looks at the world on the clock.</remarks>
    public void Tick(DateTimeOffset now)
    {
        foreach (var source in _sources)
        {
            (source as IUiTickTarget)?.Tick(now);
        }
    }

    /// <summary>Stops following the sources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var source in _sources)
        {
            source.PropertyChanged -= OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    private void Recompute()
    {
        var shown = _sources.Where(source => source.IsShown && !string.IsNullOrEmpty(source.Text)).ToList();
        var texts = shown.Select(source => source.Text!).ToList();
        var tray = string.Join(" · ", shown.Select(source => source.TrayText).Where(text => !string.IsNullOrEmpty(text)));

        if (texts.SequenceEqual(Texts, StringComparer.Ordinal) && string.Equals(tray.Length == 0 ? null : tray, TrayText, StringComparison.Ordinal))
        {
            return;
        }

        Texts = texts;
        TrayText = tray.Length == 0 ? null : tray;

        OnPropertyChanged(nameof(Texts));
        OnPropertyChanged(nameof(TrayText));
        OnPropertyChanged(nameof(HasAny));
    }
}
