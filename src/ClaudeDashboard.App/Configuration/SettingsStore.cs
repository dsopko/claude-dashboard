using System.IO;
using System.Text.Json;

namespace ClaudeDashboard.App.Configuration;

/// <summary>The outcome of reading <c>settings.json</c>, and why.</summary>
public enum SettingsLoadOutcome
{
    /// <summary>The file was read and parsed.</summary>
    Loaded = 1,

    /// <summary>No file yet — first run, or the operator deleted it. Defaults are in use.</summary>
    Missing = 2,

    /// <summary>The file exists but could not be parsed or read. Defaults are in use.</summary>
    Unreadable = 3,
}

/// <summary>What a load produced.</summary>
/// <param name="Settings">The settings to run with — never null, defaults where the file failed.</param>
/// <param name="Outcome">Whether the file was read, absent, or unusable.</param>
/// <param name="Problem">The parse or I/O failure, when there was one.</param>
public readonly record struct SettingsLoadResult(
    DashboardSettings Settings,
    SettingsLoadOutcome Outcome,
    string? Problem = null);

/// <summary>
/// Reads and writes <c>settings.json</c> (Impl Part 8) with <see cref="System.Text.Json"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A bad settings file never stops the dashboard starting.</strong> Impl §10.1 starts
/// this app at logon from a scheduled task set to "restart every 1 minute, up to 3 times, if
/// the task fails" — so a process that refuses to start over a stray comma does not present as
/// a configuration error. It presents as the dashboard being *gone*, three times, and then
/// staying gone. The operator has no console and no window to read an error from; the only
/// diagnostic channel is the log file (Impl Part 8), which requires the process to be running.
/// So a malformed file is logged and replaced with defaults in memory.
/// </para>
/// <para>
/// The bad file itself is left untouched on disk. It is the operator's file and may hold
/// settings they spent time on; overwriting it with defaults would destroy the evidence of
/// what was wrong along with the content they meant to keep.
/// </para>
/// </remarks>
public sealed class SettingsStore(DashboardPaths paths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly DashboardPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>
    /// Reads the settings file, falling back to defaults for anything that goes wrong.
    /// </summary>
    /// <remarks>
    /// Never throws. The caller decides what to log; this decides only what to run with.
    /// Comments and trailing commas are tolerated, because Impl Part 8 calls this file
    /// "human-editable" and a human editing JSON writes both.
    /// </remarks>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new SettingsLoadResult(new DashboardSettings(), SettingsLoadOutcome.Missing);
        }

        try
        {
            var json = File.ReadAllText(_paths.SettingsFile);
            var settings = JsonSerializer.Deserialize<DashboardSettings>(json, SerializerOptions);

            return settings is null

                // Valid JSON that is literally `null` parses without error and means nothing.
                ? new SettingsLoadResult(
                    new DashboardSettings(),
                    SettingsLoadOutcome.Unreadable,
                    "The settings file contained no object.")
                : new SettingsLoadResult(settings, SettingsLoadOutcome.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                new DashboardSettings(),
                SettingsLoadOutcome.Unreadable,
                ex.Message);
        }
    }

    /// <summary>Writes <paramref name="settings"/> to the settings file, creating the folder if needed.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(DashboardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(
            _paths.SettingsFile,
            JsonSerializer.Serialize(settings, SerializerOptions));
    }
}
