using System.ComponentModel;
using System.Windows;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The dashboard window (Design Document §9; Impl §5.1).
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose. Everything on screen comes from <see cref="MainViewModel"/> through bindings,
/// and the only behaviour here is the one thing a view model cannot express: what closing means.
/// </para>
/// <para>
/// <strong>Closing hides.</strong> Impl §5.1: the window is shown and hidden, never recreated, so
/// it keeps its position and its expanded rows; the process exits only via the tray's Quit. Until
/// that tray exists (T1.13) a closed window can be brought back by the <c>/show</c> endpoint
/// (T1.15) or by restarting — which is the documented arrangement, not an oversight.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Creates the window over <paramref name="viewModel"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is null.</exception>
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>What the window is showing.</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>Brings the window back, wherever it was left (Impl §5.1, §5.3).</summary>
    public void ShowDashboard()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Impl §5.1: cancel the close and hide. A dashboard that exited when its window closed
        // would stop consuming hooks, and the operator would have no way to tell that from a
        // quiet afternoon.
        if (!e.Cancel)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
