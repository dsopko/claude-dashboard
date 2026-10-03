using System.ComponentModel;
using System.IO;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// The notice that the dashboard's own settings file could not be read (T1.56, issue #73).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Running on defaults must not look like running on your settings.</strong> Before T1.56,
/// one wrong character in <c>settings.json</c> put the dashboard on its defaults: the pinned port,
/// the sound levels, the window's place and the saved rosters were all ignored, and one log line
/// was the only sign.
/// </para>
/// <para>
/// <strong>Three texts, one per case.</strong> A file that did not parse was renamed and a fresh one
/// written (<see cref="SettingsStore.PrepareForStart"/>): the text names the backup and the folder,
/// and says how to get the settings back. A file that could not be opened, or one that opened but
/// could not be kept aside, was left alone: each has its own text, and each says no settings are
/// saved until a restart. Neither shows a setting value or the
/// parse error; the error is in the log only.
/// </para>
/// <para>
/// <strong>It stays until the next start.</strong> Nothing during this run changes what this start
/// did, so the notice is fixed when it is made and never changes.
/// </para>
/// </remarks>
public sealed class SettingsNotice : INotice
{
    /// <summary>The tray tooltip's short form, in both cases.</summary>
    public const string TrayShort = "settings not read · using defaults";

    /// <summary>Creates the notice for what this start did with its settings file.</summary>
    /// <param name="start">This start's settings; null, or a file that read, shows nothing.</param>
    /// <param name="paths">The data folder, named in the text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public SettingsNotice(SettingsAtStart? start, DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (start is { KeptAside: true, BackupFile: { } backup })
        {
            Text = KeptAsideText(Path.GetFileName(backup), paths.Root);
        }
        else if (start is { SavesRefused: true, KeepAsideProblem: not null })
        {
            Text = NotKeptAsideText(paths.Root);
        }
        else if (start is { SavesRefused: true })
        {
            Text = NotOpenedText(paths.Root);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Never raised: the notice is fixed for the life of the start.</remarks>
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public string? Text { get; }

    /// <inheritdoc/>
    public string? TrayText => Text is null ? null : TrayShort;

    /// <inheritdoc/>
    public bool IsShown => Text is not null;

    /// <summary>The window's text after the file was renamed and a fresh one written.</summary>
    public static string KeptAsideText(string backupName, string folder) =>
        $"settings.json could not be read. It was renamed to {backupName}, and a new settings.json " +
        $"with the defaults was written in {folder}. Copy your settings back from the renamed file, " +
        "then restart the dashboard. Copy \"installHooksAtStart\": false and \"startWithWindows\": false back " +
        "too, if you had set them, or the next start turns them on again.";

    /// <summary>
    /// The window's text when the file opened but could not be kept aside: the rename failed (T1.56's
    /// review). It is not "could not be opened", which would send the operator to the wrong cause.
    /// </summary>
    public static string NotKeptAsideText(string folder) =>
        $"settings.json in {folder} could not be read or kept aside, so the dashboard runs on its defaults " +
        "and saves no settings until it is restarted.";

    /// <summary>The window's text when the file could not be opened.</summary>
    public static string NotOpenedText(string folder) =>
        $"settings.json in {folder} could not be opened, so the dashboard runs on its defaults and " +
        "saves no settings until it is restarted.";
}
