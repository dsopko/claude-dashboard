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
    /// <summary>
    /// Says so when the file names a port that is not one (Impl §3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Computed from the file, never stored beside the value.</strong> A mistyped port
    /// becomes <see langword="null"/> on <see cref="DashboardSettings.Port"/> — the same as never
    /// having set one — so the two cases are indistinguishable from the settings object alone. This
    /// tells them apart by looking at the file again, which means there is no second copy of the
    /// port to disagree with the first.
    /// </para>
    /// <para>
    /// It matters because the outcomes differ for the operator, not for the code: an absent key is
    /// normal and needs no line, while a key holding <c>0</c> or <c>"52789x"</c> is a person who
    /// meant to pin a port and did not.
    /// </para>
    /// </remarks>
    private static string? PortProblem(string json, DashboardSettings settings)
    {
        if (settings.Port is not null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("port", out var port) &&
                port.ValueKind != JsonValueKind.Null)
            {
                return
                    $"The \"port\" setting is {port} , which is not a usable port. The dashboard will " +
                    "choose one for this user instead. Remove the setting, or give it a number between " +
                    "1 and 65535.";
            }
        }
        catch (JsonException)
        {
            // Unreachable in practice: this runs only after a successful deserialize.
        }

        return null;
    }

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
                : new SettingsLoadResult(settings, SettingsLoadOutcome.Loaded, PortProblem(json, settings));
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
