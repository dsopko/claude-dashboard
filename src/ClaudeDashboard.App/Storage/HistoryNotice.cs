using System.ComponentModel;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// The notice that history is not being recorded (T1.54, issue #71).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A history that stops must say so where the operator looks.</strong> Before T1.54 a
/// failing <c>dashboard.db</c> wrote one line in the log, and the window, the tray and the sounds
/// went on as usual. The operator found out days later, looking for a record that was not there.
/// </para>
/// <para>
/// <strong>Shown while the store's last write failed; cleared by the first write that
/// succeeds.</strong> The store tries again each minute (<see cref="SqliteEventStore.RetryAfter"/>),
/// so the text says that, and asks nothing of the operator. The tray keeps its colour: history is
/// a lost feature, not a session that needs anyone.
/// </para>
/// <para>
/// <strong>Read on the tick, not pushed.</strong> The store is written on the archive writer's
/// thread, and the notice is bound on the UI thread. Rather than marshal from one to the other,
/// <see cref="Tick"/> reads the store's published state on the 15-second tick that already
/// refreshes the tray. So the notice follows the store by at most one tick, and costs no timer and
/// no thread.
/// </para>
/// </remarks>
public sealed class HistoryNotice : INotice, IUiTickTarget
{
    /// <summary>The window's text.</summary>
    public const string WindowText =
        "History is not being recorded: the database could not be written. The dashboard tries again each minute.";

    /// <summary>The tray tooltip's short form.</summary>
    public const string TrayShort = "history not recorded";

    private readonly Func<bool> _failing;

    /// <summary>Creates the notice.</summary>
    /// <param name="failing">
    /// Whether the store's last write failed. Called on the UI thread, at each tick; it must be
    /// cheap and must read a value the writer's thread publishes safely.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="failing"/> is null.</exception>
    public HistoryNotice(Func<bool> failing)
    {
        ArgumentNullException.ThrowIfNull(failing);

        _failing = failing;
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
        var failing = _failing();

        if (failing == IsShown)
        {
            return;
        }

        IsShown = failing;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
    }
}
