using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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
public partial class MainWindow : Window, IActivityRows
{
    /// <summary>Creates the window over <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">What the window shows.</param>
    /// <param name="tray">
    /// The tray's view model, which the header's Mute all binds to (T1.47). Required, so a lost
    /// registration fails the container instead of leaving a button that does nothing.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public MainWindow(MainViewModel viewModel, TrayViewModel tray)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(tray);

        ViewModel = viewModel;
        Tray = tray;
        InitializeComponent();

        // Before the window's own DataContext, so the header's Mute all never binds against the
        // wrong object on the way: its label and command are the tray's, from one source (T1.47).
        MuteAllButton.DataContext = tray;
        ActivityButton.DataContext = tray;

        // The notice row too (the ruling of 2026-10-01): the tray carries the notice, so the window
        // and the tooltip cannot disagree about it.
        NoticeRow.DataContext = tray;
        DataContext = viewModel;
        ApplyCaptionIcon();
    }

    /// <summary>What the window is showing.</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// The tray's view model. The header's Mute all binds to it, so the header and the tray menu
    /// read one muted state and publish one command.
    /// </summary>
    public TrayViewModel Tray { get; }

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
    public bool Has(ActivityLine line) => ViewModel.Has(line);

    /// <summary>
    /// A click on an Activity line (T1.71, issue #97): the window comes to the front, then the line's row is
    /// unfolded, opened and scrolled into view. A session or a group that is not here: the front, nothing more.
    /// </summary>
    /// <remarks>
    /// The row is opened before it is scrolled to, and the layout brought up to date between the two, so what is
    /// brought into view is the open row, as much of it as fits. <c>RowsHost</c> is not virtualized, so the row's
    /// container exists as soon as the layout has run.
    /// </remarks>
    public void Show(ActivityLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        ShowDashboard();

        if (ViewModel.Reveal(line) is not { } row)
        {
            return;
        }

        RowsHost.UpdateLayout();

        if (RowsHost.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
        {
            container.BringIntoView();
        }
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
        ApplyCaptionIcon();
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
        ApplyCaptionIcon();
    }

    /// <summary>
    /// Draws the caption icon's frame for the display scale the window is on (T1.38).
    /// </summary>
    /// <remarks>
    /// Three times, because the scale is known three times: from the system in the constructor,
    /// from the window's own monitor once it has a handle (per-monitor aware, it can open on a
    /// monitor that scales differently), and again on every move to another. Read from the
    /// window rather than passed from <see cref="OnDpiChanged"/>, so every caller asks the same
    /// question. Internal for the realized-window test.
    /// </remarks>
    internal void ApplyCaptionIcon() =>
        CaptionIconImage.Source = CaptionIcon.Load(VisualTreeHelper.GetDpi(this).DpiScaleX);

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
