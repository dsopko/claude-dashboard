using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The Settings window (issue #36). Its first and only setting: start the dashboard when Windows
/// starts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A change applies at once; there is no Save button.</strong> Ticking or unticking writes
/// <c>startWithWindows</c> to the dashboard's settings and makes Windows match it in the same step.
/// </para>
/// <para>
/// <strong>The checkbox shows what Windows will do, not only what the setting says.</strong> It is
/// ticked when the <c>Run</c> value is there for this copy and Windows has not switched it off. So a
/// setting that is on, with Windows' own switch off, shows unticked with a line saying so; ticking
/// it then clears Windows' mark. A registry that refused shows the true state too.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The checkbox's label.</summary>
    public const string StartWithWindowsLabel = "Start Claude Dashboard when Windows starts";

    /// <summary>Why a copy that is not installed cannot register.</summary>
    public const string NotInstalledNote =
        "This copy was not installed with Setup — it is portable, or a build run from source — so it " +
        "has no fixed path for Windows to start. Install Claude Dashboard to use this.";

    /// <summary>Why the checkbox is unticked although the setting is on.</summary>
    public const string WindowsDisabledNote =
        "Windows has Claude Dashboard turned off in Settings › Apps › Startup or in Task Manager. " +
        "Tick the box to turn it back on.";

    /// <summary>
    /// Why the choice will not last past this run: the settings file could not be opened, so this run
    /// saves nothing (T1.56, from its review).
    /// </summary>
    public const string NotRememberedNote =
        "This choice is not remembered: the settings file could not be opened.";

    private readonly StartWithWindows _startup;
    private readonly SettingsStore _store;
    private readonly ILogger _logger;
    private bool _showing;

    /// <summary>Creates the view model.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SettingsViewModel(StartWithWindows startup, SettingsStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _startup = startup;
        _store = store;
        _logger = logger;

        Refresh();
    }

    /// <summary>The checkbox: whether the dashboard will start at the next sign-in.</summary>
    [ObservableProperty]
    private bool _startsWithWindows;

    /// <summary>A line under the checkbox, or null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    /// <summary>Whether this copy can change anything at all.</summary>
    public bool CanChange => _startup.Installed;

    /// <summary>Whether the line under the checkbox is shown.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Reads what Windows has, and shows it. Changes nothing.</summary>
    public void Refresh() => Show(_startup.Read());

    partial void OnStartsWithWindowsChanged(bool value)
    {
        if (_showing)
        {
            return;
        }

        var remembered = Remember(value);
        Show(_startup.Apply(value));

        // After Show, which sets the note from Windows' state: a choice Windows took but the file
        // did not keep must say so, or the next start quietly undoes it.
        if (!remembered)
        {
            Note = NotRememberedNote;
        }
    }

    private void Show(StartupState state)
    {
        _showing = true;

        try
        {
            StartsWithWindows = state.StartsAtSignIn;
            Note = !state.Installed
                ? NotInstalledNote
                : state.Problem is { } problem
                    ? $"Windows did not let the dashboard read its startup entry: {problem}"
                    : state.WindowsDisabled ? WindowsDisabledNote : null;
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>Records the choice in the dashboard's settings, so a start keeps to it.</summary>
    /// <returns>Whether the file holds the choice now.</returns>
    private bool Remember(bool value)
    {
        var loaded = _store.Load();

        if (loaded.Outcome == SettingsLoadOutcome.Unreadable)
        {
            _logger.Warning(
                "Could not record \"startWithWindows\": {Problem}. The dashboard's settings file was left as it is.",
                loaded.Problem);

            return false;
        }

        if (loaded.Settings.StartWithWindows == value)
        {
            return true;
        }

        try
        {
            // False when this run refuses saves (T1.56); the store has logged it.
            return _store.Save(loaded.Settings with { StartWithWindows = value });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not record \"startWithWindows\" in the dashboard's settings.");

            return false;
        }
    }
}
