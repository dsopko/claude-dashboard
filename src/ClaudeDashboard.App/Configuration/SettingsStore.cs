using System.Collections.Concurrent;
using System.Globalization;
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
/// <param name="Blocked">
/// <see cref="SettingsLoadOutcome.Unreadable"/> because the file could not be opened at all (no
/// permission, or another program holds it), not because it did not parse (T1.56). Such a file is
/// left alone: it may be perfectly good.
/// </param>
public readonly record struct SettingsLoadResult(
    DashboardSettings Settings,
    SettingsLoadOutcome Outcome,
    string? Problem = null,
    bool Blocked = false);

/// <summary>
/// Reads and writes <c>settings.json</c> (Impl Part 8) with <see cref="System.Text.Json"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A bad settings file never stops the dashboard starting.</strong> Impl §10.1 starts
/// this app when the operator signs in, through the <c>Run</c> key, and nothing starts it again
/// if it stops — so a process that refuses to start over a stray comma does not present as a
/// configuration error. It presents as the dashboard being *gone*, and staying gone. The
/// operator has no console and no window to read an error from; the only diagnostic channel is
/// the log file (Impl Part 8), which requires the process to be running. So a malformed file is
/// logged and replaced with defaults in memory. (Until T1.50 a scheduled task started the app
/// and retried a failed start three times; that task is removed.)
/// </para>
/// <para>
/// <strong>A file that does not parse is kept aside, never overwritten</strong> (T1.56, the
/// operator's ruling of 2026-10-03 on issue #73). It is the operator's file and may hold settings
/// they spent time on. Until T1.56 it was "left as it is", and that held only until the next save,
/// which wrote the defaults over it (issue #26). Now the first start that shows the window renames
/// it to <c>settings.error-&lt;yyyyMMdd-HHmmss&gt;.json</c> and writes a fresh file with the
/// defaults in its place, so later saves go to the fresh file. See <see cref="PrepareForStart"/>.
/// </para>
/// <para>
/// <strong>A file that cannot be opened is left alone, and nothing is saved for the rest of the
/// run.</strong> It may be perfectly good and only held by another program. The refusal is here,
/// where <see cref="Load"/> and <see cref="Save"/> meet, so every save site obeys it: the window's
/// place at quit, the rosters, the Settings window and the <c>installHooksAtStart</c> record.
/// </para>
/// </remarks>
/// <param name="paths">The data folder.</param>
/// <param name="logger">Where a refused save is logged; nothing is logged without one.</param>
public sealed class SettingsStore(DashboardPaths paths, Serilog.ILogger? logger = null)
{
    /// <summary>
    /// The settings files that refuse saves for the rest of this process, by full path (T1.56).
    /// </summary>
    /// <remarks>
    /// Static because "the rest of the run" is the life of the process, and several stores are made
    /// over one file during a start. Keyed by the full path, so stores over other data folders, as
    /// in the tests, are not affected.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, bool> Refused = new(StringComparer.OrdinalIgnoreCase);

    private readonly Serilog.ILogger? _logger = logger;

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
                    // The setting and the repair, never the value: no setting value is logged (T1.56, T1.65).
                    "The \"port\" setting is not a usable port. The dashboard will choose one for this user " +
                    "instead. Remove the setting, or give it a number between 1 and 65535.";
            }
        }
        catch (JsonException)
        {
            // Unreachable in practice: this runs only after a successful deserialize.
        }

        return null;
    }

    /// <summary>
    /// The values the load repaired, as one sentence each, or null: a port that is not a port, and
    /// a negative <c>history.retentionDays</c> (T1.64). AppHost logs them as one Warning.
    /// </summary>
    private static string? Repaired(string json, DashboardSettings settings)
    {
        var problems = new[] { PortProblem(json, settings), HistoryProblem(json) }
            .Where(problem => problem is not null)
            .ToList();

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>
    /// The sentence for a negative <c>history.retentionDays</c>, or null. The value is not repeated:
    /// the sentence says what the dashboard does instead.
    /// </summary>
    private static string? HistoryProblem(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoringCase(document.RootElement, "history", out var history) &&
                history.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoringCase(history, "retentionDays", out var days) &&
                days.ValueKind == JsonValueKind.Number &&
                days.TryGetInt64(out var value) &&
                value < 0)
            {
                return
                    "The \"history.retentionDays\" setting is negative, so the history keeps the default of " +
                    $"{HistorySettings.DefaultRetentionDays} days. Set it to 0 to keep everything, or to the days to keep.";
            }
        }
        catch (JsonException)
        {
            // Unreachable in practice: this runs only after a successful deserialize.
        }

        return null;
    }

    /// <summary>A property by name, ignoring case, as the deserializer reads it.</summary>
    private static bool TryGetPropertyIgnoringCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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
                : new SettingsLoadResult(settings, SettingsLoadOutcome.Loaded, Repaired(json, settings));
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(
                new DashboardSettings(),
                SettingsLoadOutcome.Unreadable,
                ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                new DashboardSettings(),
                SettingsLoadOutcome.Unreadable,
                ex.Message,
                Blocked: true);
        }
    }

    /// <summary>
    /// Whether this file refuses saves for the rest of the run: this start could not open it, or
    /// could not keep it aside (T1.56).
    /// </summary>
    public bool SavesRefused => Refused.ContainsKey(FullPath);

    private string FullPath => Path.GetFullPath(_paths.SettingsFile);

    /// <summary>Writes <paramref name="settings"/> to the settings file, creating the folder if needed.</summary>
    /// <returns>
    /// True when the file was written. False when saves are refused for this run (T1.56): the file
    /// is left exactly as it is, and one line says so.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public bool Save(DashboardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (SavesRefused)
        {
            // No setting value: the line says that a save was refused, and why, and nothing more.
            _logger?.Warning(
                "Did not save {File}: this start could not open it or keep it aside, so the dashboard " +
                "saves no settings until it restarts.",
                _paths.SettingsFile);

            return false;
        }

        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(
            _paths.SettingsFile,
            JsonSerializer.Serialize(settings, SerializerOptions));

        return true;
    }

    /// <summary>
    /// What a start that will show the window does with the file it loaded (T1.56, issue #73).
    /// </summary>
    /// <param name="loaded">This start's first load, before anything else read the file.</param>
    /// <param name="localNow">The local time, for the backup's name.</param>
    /// <returns>
    /// The start's settings, whose <see cref="SettingsAtStart.Original"/> stays the authority for
    /// the whole start.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Only for a start that is the first instance and will show the window</strong>, after
    /// the single-instance decision. A second instance that exits and the one-shot switches never
    /// call it, so they leave a bad file byte for byte.
    /// </para>
    /// <para>
    /// <strong>A file that does not parse is moved, not copied.</strong> The fresh file is written
    /// first, beside it, under a temporary name; then the bad file is renamed to the backup name,
    /// and the fresh file takes its place. So a failure at any step leaves the bad file where it was
    /// and changes nothing else, and that is the cannot-open case below. The backup is never
    /// written, rewritten or deleted.
    /// </para>
    /// <para>
    /// <strong>A file that cannot be opened, or a keep-aside that fails, refuses saves</strong> for
    /// the rest of the run. Nothing is moved and no file is written: the file may be perfectly good.
    /// </para>
    /// </remarks>
    public SettingsAtStart PrepareForStart(SettingsLoadResult loaded, DateTime localNow)
    {
        if (loaded.Outcome != SettingsLoadOutcome.Unreadable)
        {
            return new SettingsAtStart(loaded);
        }

        if (loaded.Blocked)
        {
            Refuse();

            return new SettingsAtStart(loaded, SavesRefused: true);
        }

        var fresh = _paths.SettingsFile + ".new";
        string? backup = null;

        try
        {
            File.WriteAllText(fresh, JsonSerializer.Serialize(new DashboardSettings(), SerializerOptions));

            backup = BackupPath(localNow);
            File.Move(_paths.SettingsFile, backup);
            File.Move(fresh, _paths.SettingsFile);

            return new SettingsAtStart(loaded, BackupFile: backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the operator's file back if it was moved, and remove the fresh one. Best effort:
            // a failure here leaves the bad file under the backup name, which keeps its bytes.
            try
            {
                if (backup is not null && File.Exists(backup) && !File.Exists(_paths.SettingsFile))
                {
                    File.Move(backup, _paths.SettingsFile);
                }

                File.Delete(fresh);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done here; the start's Error line names the failure.
            }

            Refuse();

            return new SettingsAtStart(loaded, SavesRefused: true, KeepAsideProblem: ex.Message);
        }
    }

    private void Refuse() => Refused[FullPath] = true;

    /// <summary>
    /// <c>settings.error-&lt;yyyyMMdd-HHmmss&gt;.json</c> in the data folder, local time; <c>-2</c>,
    /// <c>-3</c> and so on when that name is taken.
    /// </summary>
    private string BackupPath(DateTime localNow)
    {
        var stamp = localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(_paths.Root, $"settings.error-{stamp}.json");

        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(_paths.Root, $"settings.error-{stamp}-{suffix}.json");
        }

        return path;
    }
}

/// <summary>
/// The settings a start runs with, and what it did with a file it could not read (T1.56).
/// </summary>
/// <param name="Original">
/// The start's first load. <strong>The authority for the whole start</strong>: the hook check reads
/// its outcome, so a start whose file did not parse registers no plugin, even though the fresh file
/// it wrote says <c>installHooksAtStart: true</c> (T1.32's guard).
/// </param>
/// <param name="BackupFile">The full path the unreadable file was renamed to, or null.</param>
/// <param name="SavesRefused">Whether this run saves no settings: the file could not be opened, or kept aside.</param>
/// <param name="KeepAsideProblem">Why the keep-aside failed, when it did.</param>
public sealed record SettingsAtStart(
    SettingsLoadResult Original,
    string? BackupFile = null,
    bool SavesRefused = false,
    string? KeepAsideProblem = null)
{
    /// <summary>Whether the unreadable file was renamed and a fresh one written.</summary>
    public bool KeptAside => BackupFile is not null;
}
