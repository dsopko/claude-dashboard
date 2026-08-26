using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using H.NotifyIcon;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The shell notification icon, bound to <see cref="TrayViewModel"/> (Impl §5.1, §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything here is binding and Win32.</strong> No decision is taken in this type: the
/// colour, the tooltip, the labels and the commands all come from the view model, which is where
/// they can be asserted without a shell. What genuinely cannot be observed in-process is whether
/// Windows drew the icon in the notification area — that is the one manual check this task
/// leaves, and it is the only one.
/// </para>
/// <para>
/// Built in code rather than XAML because it has no visual parent: a <c>TaskbarIcon</c> is not
/// in the window's tree, so there is no page to declare it on, and a resource dictionary would
/// hide the bindings this type exists to make explicit.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private bool _disposed;

    /// <summary>Creates the icon, binds it, and puts it on the clock.</summary>
    /// <param name="viewModel">The state and the commands.</param>
    /// <param name="tick">
    /// The UI tick. Attached here rather than by <c>Program</c> because that is the difference
    /// between a tray that is registered and one that is <em>driven</em>: a global mute lapses by
    /// predicate and raises no event, so a tray nobody ticks shows a tooltip that was right once,
    /// at startup. Attaching in the composition root means resolving a <see cref="TrayIcon"/> is
    /// what wires it, and a test that resolves one can prove it — which a line in <c>Main</c>
    /// could not.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public TrayIcon(TrayViewModel viewModel, UiTick tick)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(tick);

        ViewModel = viewModel;
        tick.Attach(viewModel);

        _icon = new TaskbarIcon
        {
            DataContext = viewModel,
            // Left-click toggles the window (Impl §5.2). Bound rather than handled, so the same
            // command the menu's Open uses is the one the click runs.
            LeftClickCommand = viewModel.OpenCommand,
            ContextMenu = BuildMenu(viewModel),
            NoLeftClickDelay = true,
        };

        BindingOperations.SetBinding(
            _icon,
            TaskbarIcon.IconProperty,
            new Binding(nameof(TrayViewModel.Icon)) { Source = viewModel });

        BindingOperations.SetBinding(
            _icon,
            TaskbarIcon.ToolTipTextProperty,
            new Binding(nameof(TrayViewModel.Tooltip)) { Source = viewModel });

        _icon.ForceCreate();
    }

    /// <summary>The state behind the icon.</summary>
    public TrayViewModel ViewModel { get; }

    /// <summary>The context menu, exposed so its wiring can be asserted.</summary>
    internal ContextMenu Menu => (ContextMenu)_icon.ContextMenu!;

    /// <summary>
    /// What the shell icon is actually showing, exposed so the binding can be asserted rather
    /// than assumed.
    /// </summary>
    /// <remarks>
    /// The value on the icon, not the one on the view model. Reading the view model would prove
    /// the tooltip was computed; this proves it arrived — and from T1.15 the tooltip is the only
    /// place an operator learns that the dashboard cannot hear anything.
    /// </remarks>
    internal string ToolTipText => _icon.ToolTipText;

    /// <summary>Removes the icon from the notification area.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Dispose();
    }

    /// <summary>
    /// Open · Mute all (and for 30 min) · Pause monitoring · Settings · Quit (Impl §5.2).
    /// </summary>
    /// <remarks>
    /// The mute and pause headers bind their text, because both items toggle: "Mute all" becomes
    /// "Unmute all", and "Pause monitoring" becomes "Resume monitoring". Impl §5.2 is explicit
    /// that pause is one toggling item rather than two, so that the menu says what the current
    /// state is instead of offering both directions at once.
    /// </remarks>
    private static ContextMenu BuildMenu(TrayViewModel viewModel)
    {
        var menu = new ContextMenu { DataContext = viewModel };

        menu.Items.Add(Item("Open", viewModel.OpenCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Bound(nameof(TrayViewModel.MuteAllLabel), viewModel, viewModel.MuteAllCommand));
        menu.Items.Add(Item("Mute all for 30 min", viewModel.MuteAllForThirtyMinutesCommand));
        menu.Items.Add(Bound(nameof(TrayViewModel.PauseLabel), viewModel, viewModel.TogglePauseCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", viewModel.OpenSettingsCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", viewModel.QuitCommand));

        return menu;
    }

    private static MenuItem Item(string header, ICommand command) =>
        new() { Header = header, Command = command };

    private static MenuItem Bound(string path, TrayViewModel source, ICommand command)
    {
        var item = new MenuItem { Command = command };

        BindingOperations.SetBinding(
            item,
            HeaderedItemsControl.HeaderProperty,
            new Binding(path) { Source = source });

        return item;
    }
}
