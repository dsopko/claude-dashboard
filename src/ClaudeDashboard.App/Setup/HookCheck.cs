using System.IO;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>What a read of Claude Code's settings found about the dashboard's hook.</summary>
/// <param name="OldHookEvents">
/// How many events carry the handler a build from before the plugin wrote into the settings file.
/// More than zero means that handler is still there. The dashboard does not take it out — it
/// never writes that file — and does not register the plugin beside it, because the two together
/// would post every event twice. Required, with no default, so that a presence is built through
/// this constructor: a record struct's own empty constructor would leave
/// <paramref name="ClaudeCodeInstalled"/> false, which reads as a machine with no Claude Code.
/// </param>
/// <param name="Problem">Why the file could not be read, when it could not be.</param>
/// <param name="ClaudeCodeInstalled">
/// Whether Claude Code's configuration directory exists at all (T1.33, issue #42). The check is
/// the directory and only the directory — the app never goes looking for other software — and
/// its absence is the one reliable sign this machine has never had Claude Code. Defaults to
/// <see langword="true"/> so that a presence built by hand describes the ordinary machine unless
/// it says otherwise.
/// </param>
/// <param name="PluginEnabled">
/// Whether Claude Code has this data folder's plugin enabled (issue #30): the settings name the
/// plugin as enabled, and give this data folder's plugin folder as its source.
/// </param>
/// <param name="PluginDisabled">
/// Whether Claude Code has this data folder's plugin registered and <strong>turned off</strong>: the
/// settings name it, and set it to <see langword="false"/> — what <c>claude plugin disable</c>
/// leaves. That is the operator's choice, and a start honours it (the ruling of 2026-10-01).
/// </param>
/// <param name="ForeignPlugin">
/// The folder of a <see cref="HookPlugin.Name"/> plugin that belongs to another data folder, or
/// null. Two data folders cannot both register a plugin of one name.
/// </param>
public readonly record struct HookPresence(
    int OldHookEvents,
    string? Problem = null,
    bool ClaudeCodeInstalled = true,
    bool PluginEnabled = false,
    bool PluginDisabled = false,
    string? ForeignPlugin = null);

/// <summary>
/// Reads Claude Code's settings and says how the dashboard's hook stands there. Writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This was <c>HookInstaller</c>, and it installed.</strong> It merged the handler into
/// Claude Code's settings file and took it out again. By the operator's ruling of 2026-10-01 the
/// dashboard never writes that file: the hook is a Claude Code plugin, which Claude Code registers
/// itself (<see cref="PluginInstaller"/>). The read is what is left, under a name that says so.
/// </para>
/// <para>
/// <strong>Without the read, a missing hook is undetectable.</strong> The dashboard sits there
/// receiving no events, and that looks exactly like a quiet day. <see cref="StartupHookInstall"/>
/// turns each finding here into a registration or into a notice on screen.
/// </para>
/// <para>
/// <strong>It logs what it found, not the file's contents.</strong> The one value out of the file that reaches a
/// log line is the folder of a same-named plugin from another data folder.
/// </para>
/// </remarks>
public sealed class HookCheck
{
    private readonly ClaudeCodePaths _claude;
    private readonly DashboardPaths _paths;
    private readonly ILogger _logger;

    /// <summary>Creates the check over Claude Code's settings file.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public HookCheck(ClaudeCodePaths claude, DashboardPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _claude = claude;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The script the hook runs.</summary>
    public string ScriptPath => _paths.HookScriptFile;

    /// <summary>
    /// Where Claude Code's configuration lives — the directory whose absence means Claude Code is
    /// not installed (T1.33).
    /// </summary>
    public string ClaudeConfigDirectory => _claude.ConfigDirectory;

    /// <summary>Reads Claude Code's settings, says what is there, and logs what it found.</summary>
    public HookPresence Check() => Reported(Read());

    /// <summary>
    /// Reads Claude Code's settings and says what is there, logging nothing. Never throws for a
    /// file that is locked or malformed: that is in <see cref="HookPresence.Problem"/>.
    /// </summary>
    /// <remarks>
    /// For the notice's re-read when a session reports (<see cref="HookNotice.ConfirmPluginWith"/>).
    /// That runs again and again while the plugin stays off, and a Warning each time would bury the
    /// one that <see cref="Check"/> wrote at the start.
    /// </remarks>
    public HookPresence Read()
    {
        // The one existence check that says whether this machine has Claude Code at all (T1.33).
        // Measured once and threaded through every arm, though only the absent-file arm can
        // carry false: a file cannot be found, read, or fail to parse inside a directory that is
        // not there.
        var claudeCodeInstalled = Directory.Exists(_claude.ConfigDirectory);

        string text;

        try
        {
            if (!File.Exists(_claude.UserSettingsFile))
            {
                return new HookPresence(0, ClaudeCodeInstalled: claudeCodeInstalled);
            }

            text = File.ReadAllText(_claude.UserSettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HookPresence(0, Problem: ex.Message, ClaudeCodeInstalled: claudeCodeInstalled);
        }

        try
        {
            var settings = HookHandlers.Parse(text);

            // "Ours" is decided by the folder the settings give for the marketplace, never by the
            // name alone: a second data folder registers a plugin of the same name from a
            // different folder.
            var pluginFolder = HookPlugin.MarketplaceFolder(settings);
            var pluginIsOurs = HookPlugin.IsFolderOf(_paths, pluginFolder);

            return new HookPresence(
                HookHandlers.CountInSettings(settings, ScriptPath),
                ClaudeCodeInstalled: claudeCodeInstalled,
                PluginEnabled: pluginIsOurs && HookPlugin.IsEnabled(settings),
                PluginDisabled: pluginIsOurs && HookPlugin.IsDisabled(settings),
                ForeignPlugin: pluginFolder is not null && !pluginIsOurs ? pluginFolder : null);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new HookPresence(0, Problem: ex.Message, ClaudeCodeInstalled: claudeCodeInstalled);
        }
    }

    /// <summary>Logs the finding and hands it back.</summary>
    /// <remarks>
    /// The finding only. What is done about it, and what the operator is told on screen, belongs
    /// to <see cref="StartupHookInstall"/>, which is the only thing that knows.
    /// </remarks>
    private HookPresence Reported(HookPresence presence)
    {
        if (presence.Problem is { } problem)
        {
            _logger.Warning(
                "Could not read Claude Code's settings at {File} to see whether the dashboard's " +
                "plugin is enabled: {Problem}. The file was left as it is.",
                _claude.UserSettingsFile,
                problem);

            return presence;
        }

        if (presence.OldHookEvents > 0)
        {
            _logger.Warning(
                "Claude Code's settings still carry a hook for {Script} on {Events} event(s), from a " +
                "build before the dashboard's plugin. The dashboard does not edit that file.",
                ScriptPath,
                presence.OldHookEvents);
        }

        if (presence.PluginEnabled)
        {
            _logger.Debug(
                "Claude Code has the dashboard's plugin {Plugin} enabled from {Folder}.",
                HookPlugin.Id,
                _paths.PluginFolder);
        }
        else if (presence.PluginDisabled)
        {
            _logger.Warning(
                "Claude Code has the dashboard's plugin {Plugin} turned off. The dashboard leaves it " +
                "off, as it was set.",
                HookPlugin.Id);
        }
        else if (presence.ForeignPlugin is { } foreign)
        {
            _logger.Warning(
                "Claude Code has a plugin named {Plugin} from {Foreign}, which is not this data " +
                "folder's {Folder}. Two data folders cannot both register that plugin. Check " +
                "{HomeVariable}.",
                HookPlugin.Name,
                foreign,
                _paths.PluginFolder,
                DashboardPaths.HomeVariable);
        }
        else
        {
            // Information where the machine has no Claude Code at all (T1.33 review): the missing
            // plugin is the expected state there.
            _logger.Write(
                presence.ClaudeCodeInstalled
                    ? Serilog.Events.LogEventLevel.Warning
                    : Serilog.Events.LogEventLevel.Information,
                "Claude Code does not have the dashboard's plugin {Plugin}.",
                HookPlugin.Id);
        }

        return presence;
    }
}
