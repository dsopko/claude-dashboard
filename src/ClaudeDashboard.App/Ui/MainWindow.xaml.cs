using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The dashboard window (Design Document §9; Impl §5.1).
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose. Everything on screen comes from <see cref="MainViewModel"/> through bindings,
/// and the only behaviour here is what a view model cannot express: what closing means, and the
/// two duties a drawn caption leaves to the window (design option 2c). The Win32 half of those
/// two lives in <see cref="CaptionChrome"/> rather than here.
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

    /// <summary>
    /// Whether the pointer is over the maximize or restore button.
    /// </summary>
    /// <remarks>
    /// Reported by <see cref="CaptionChrome"/> and bound by the two buttons' styles, because
    /// neither can see the pointer for itself: the hit-test answer that earns the Snap Layouts
    /// flyout sends it to Windows instead of to WPF, so <c>IsMouseOver</c> is never true there.
    /// </remarks>
    public static readonly DependencyProperty IsMaximizeHoveredProperty =
        DependencyProperty.Register(
            nameof(IsMaximizeHovered),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

    /// <inheritdoc cref="IsMaximizeHoveredProperty"/>
    public bool IsMaximizeHovered
    {
        get => (bool)GetValue(IsMaximizeHoveredProperty);
        private set => SetValue(IsMaximizeHoveredProperty, value);
    }

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
    /// <summary>
    /// Shows the dashboard if it is hidden, hides it if it is showing (Impl §5.2, left-click).
    /// </summary>
    /// <remarks>
    /// A minimised window counts as hidden, not as showing: the operator who clicked the tray
    /// wants to see it, and restoring is what they meant. Only a window that is genuinely up and
    /// visible is hidden by a second click.
    /// </remarks>
    public void ToggleDashboard()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();

            return;
        }

        ShowDashboard();
    }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        CaptionChrome.Attach(
            this,
            () => WindowState == WindowState.Maximized ? RestoreButton : MaximizeButton,
            hovered => IsMaximizeHovered = hovered);

        ApplyMaximizedInset();
    }

    /// <inheritdoc/>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        ApplyMaximizedInset();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The inset is measured in device pixels and spent in device-independent ones, so it has to
    /// be taken again when the window crosses onto a monitor that scales differently.
    /// </remarks>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        ApplyMaximizedInset();
    }

    /// <summary>
    /// Keeps a maximized window's content inside the screen it is maximized on.
    /// </summary>
    /// <remarks>
    /// See <see cref="CaptionChrome.MaximizedInset"/>: the overflow is the window frame, which is
    /// invisible while it is non-client and is content once the caption is drawn instead.
    /// </remarks>
    private void ApplyMaximizedInset() =>
        RootBorder.Margin = WindowState == WindowState.Maximized
            ? CaptionChrome.MaximizedInset(this)
            : default;

    private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.MaximizeWindow(this);

    private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.RestoreWindow(this);

    /// <summary>
    /// The caption's X, which means what the stock one meant.
    /// </summary>
    /// <remarks>
    /// <see cref="SystemCommands.CloseWindow"/> raises the ordinary close, so
    /// <see cref="OnClosing"/> cancels it and hides — the two X's share one path rather than two
    /// implementations of one rule.
    /// </remarks>
    private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

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
