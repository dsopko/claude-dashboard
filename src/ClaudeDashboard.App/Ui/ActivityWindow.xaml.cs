using System.ComponentModel;
using System.IO;
using System.Windows;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The Activity window (T1.70, issue #97). The view is <see cref="ActivityViewModel"/>; this file only
/// tells it the window's width.
/// </summary>
public partial class ActivityWindow : Window
{
    /// <summary>Creates the window over <paramref name="viewModel"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is null.</exception>
    public ActivityWindow(ActivityViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        SizeChanged += (_, e) => ViewModel.SetWidth(e.NewSize.Width);

        // Collapsed while the window is hidden (the T1.70 review): a hidden list that has been laid out still
        // runs a layout for each new line. Shown again when the window opens; the recycled rows are made for the
        // view only.
        ActivityList.Visibility = Visibility.Collapsed;
        IsVisibleChanged += (_, _) => ActivityList.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the window shows.</summary>
    public ActivityViewModel ViewModel { get; }
}

/// <summary>
/// The one Activity window (T1.70, issue #97): made at start, and always there. Opening it, from the tray
/// menu or the toolbar, only shows it or brings it to the front; closing it only hides it. It remembers its
/// place and size, as the main window does: in <c>settings.json</c>, under <c>activityWindow</c>.
/// </summary>
/// <remarks>
/// Made at start so that its list is the one list from the first line: nothing is built again when it
/// opens. Its list is virtualized, so lines added while it is hidden are not drawn.
/// </remarks>
public sealed class ActivityWindowHost
{
    /// <summary>The size it opens at the first time.</summary>
    public const double DefaultWidth = 640;

    /// <summary>The size it opens at the first time.</summary>
    public const double DefaultHeight = 480;

    private readonly ActivityViewModel _viewModel;
    private readonly SettingsStore? _settings;
    private readonly ILogger _logger;
    private readonly Func<ActivityViewModel, ActivityWindow> _create;
    private readonly Action<ActivityWindow> _place;
    private ActivityWindow? _window;
    private bool _quitting;

    /// <summary>Creates the host over the one view model and the settings it saves its place in.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ActivityWindowHost(ActivityViewModel viewModel, SettingsStore settings, ILogger logger)
        : this(viewModel, settings, logger, model => new ActivityWindow(model), place: null)
    {
    }

    /// <summary>
    /// For tests: <paramref name="create"/> makes the window, <paramref name="place"/> puts it where the test
    /// wants it (off every monitor), and a null store saves nothing.
    /// </summary>
    internal ActivityWindowHost(
        ActivityViewModel viewModel,
        SettingsStore? settings,
        ILogger logger,
        Func<ActivityViewModel, ActivityWindow> create,
        Action<ActivityWindow>? place)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(create);

        _viewModel = viewModel;
        _settings = settings;
        _logger = logger;
        _create = create;
        _place = place ?? Place;
    }

    /// <summary>The window, once made.</summary>
    internal ActivityWindow? Window => _window;

    /// <summary>Makes the window, hidden, at start. UI thread only. A second call changes nothing.</summary>
    public ActivityWindow Create()
    {
        if (_window is { } made)
        {
            return made;
        }

        var window = _create(_viewModel);
        _place(window);
        window.Closing += OnClosing;
        _window = window;

        return window;
    }

    /// <summary>Shows the window, or brings it to the front. UI thread only.</summary>
    public ActivityWindow Show()
    {
        var window = Create();

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();

        return window;
    }

    /// <summary>At quit: saves the place of a shown window, and lets the window close. UI thread only.</summary>
    public void Quit()
    {
        if (_window is { IsVisible: true } shown)
        {
            Save(shown);
        }

        _quitting = true;
    }

    /// <summary>
    /// Closing saves the place of a shown window, and only hides it. At quit, and when the application shuts
    /// down (which closes every window whatever a handler says), it closes.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not ActivityWindow window)
        {
            return;
        }

        if (window.IsVisible)
        {
            Save(window);
        }

        if (_quitting)
        {
            return;
        }

        e.Cancel = true;
        window.Hide();
    }

    private void Place(ActivityWindow window)
    {
        var saved = _settings?.Load().Settings.ActivityWindow ?? new WindowSettings();
        var monitors = MonitorLayout.WorkingAreas().ToList();
        var decision = WindowPlacement.Decide(
            saved with { Width = saved.Width ?? DefaultWidth, Height = saved.Height ?? DefaultHeight },
            monitors,
            MonitorLayout.ForCursor(monitors));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = decision.Bounds.Left;
        window.Top = decision.Bounds.Top;
        window.Width = decision.Bounds.Width;
        window.Height = decision.Bounds.Height;
    }

    /// <summary>Best effort: a place not saved is a smaller failure than a window that will not hide.</summary>
    private void Save(ActivityWindow window)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            var current = _settings.Load().Settings;
            _settings.Save(current with { ActivityWindow = WindowPresence.Capture(window, alwaysOnTop: false) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Could not save the Activity window's place: {ErrorType}.", ex.GetType().Name);
        }
    }
}

/// <summary>
/// The Activity window's layout, inherited from the list down to each line (T1.71 fix cycle): a new line has it
/// as soon as it joins the list, so it is measured once, in the right layout.
/// </summary>
public static class ActivityLayoutHost
{
    /// <summary>The layout, inherited.</summary>
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.RegisterAttached(
        "Layout",
        typeof(ActivityLayout),
        typeof(ActivityLayoutHost),
        new FrameworkPropertyMetadata(ActivityLayout.Wide, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Reads the layout.</summary>
    public static ActivityLayout GetLayout(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ActivityLayout)element.GetValue(LayoutProperty);
    }

    /// <summary>Sets the layout.</summary>
    public static void SetLayout(DependencyObject element, ActivityLayout value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LayoutProperty, value);
    }
}
