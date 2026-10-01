using System.Windows;

namespace ClaudeDashboard.App.Ui;

/// <summary>The Settings window (issue #36). See <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Creates the window over <paramref name="viewModel"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is null.</exception>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        InitializeComponent();
        StartWithWindowsLabel.Text = SettingsViewModel.StartWithWindowsLabel;
        DataContext = viewModel;
    }

    /// <summary>What the window shows.</summary>
    public SettingsViewModel ViewModel { get; }
}

/// <summary>
/// Opens the Settings window from the tray, one at a time: a second "Settings…" brings the open
/// window forward and never opens another.
/// </summary>
public sealed class SettingsWindowHost
{
    private readonly SettingsViewModel _viewModel;
    private readonly Func<SettingsViewModel, SettingsWindow> _create;
    private SettingsWindow? _window;

    /// <summary>Creates the host over the one view model.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is null.</exception>
    public SettingsWindowHost(SettingsViewModel viewModel)
        : this(viewModel, model => new SettingsWindow(model))
    {
    }

    /// <summary>For tests: <paramref name="create"/> makes the window, so it can open off-screen.</summary>
    internal SettingsWindowHost(SettingsViewModel viewModel, Func<SettingsViewModel, SettingsWindow> create)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(create);

        _viewModel = viewModel;
        _create = create;
    }

    /// <summary>The window, while it is open.</summary>
    internal SettingsWindow? Window => _window;

    /// <summary>
    /// Shows the window, reading what Windows has afresh, or brings the open one forward. UI thread
    /// only.
    /// </summary>
    public SettingsWindow Show()
    {
        _viewModel.Refresh();

        if (_window is { IsLoaded: true } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();

            return open;
        }

        _window = _create(_viewModel);
        _window.Closed += (_, _) => _window = null;
        _window.Show();

        return _window;
    }
}
