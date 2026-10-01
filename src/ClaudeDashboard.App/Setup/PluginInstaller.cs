using System.IO;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>What asking Claude Code about the plugin came to.</summary>
public enum PluginOutcome
{
    /// <summary>Claude Code now has the plugin registered and enabled.</summary>
    Registered = 1,

    /// <summary>Claude Code no longer has the plugin or its marketplace.</summary>
    Removed = 2,

    /// <summary>The <c>claude</c> program was not found, so nothing was asked.</summary>
    CliNotFound = 3,

    /// <summary>The files could not be written, or Claude Code refused.</summary>
    Failed = 4,
}

/// <summary>The outcome, and the reason when there was a problem.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Problem">
/// Why, when it failed: an I/O message, or what <c>claude</c> printed. Never hook or session text.
/// </param>
public readonly record struct PluginResult(PluginOutcome Outcome, string? Problem = null);

/// <summary>
/// Registers the dashboard's plugin with Claude Code, and takes it out, by asking the
/// <c>claude</c> program (issue #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Claude Code does the writing.</strong> This class writes the dashboard's own plugin
/// files and then runs two <c>claude plugin</c> commands. Claude Code's settings file is changed
/// by Claude Code alone, which is the point of the issue: one writer for that file.
/// </para>
/// <para>
/// <strong>It decides nothing about when.</strong> <see cref="StartupHookInstall"/> and
/// <see cref="HookSwitches"/> hold every rule — the opt-out, the unreadable file, the machine
/// with no Claude Code — and call this only after those rules have passed.
/// </para>
/// <para>
/// <strong>Both install commands may be repeated.</strong> Measured on 2026-09-30: adding a
/// marketplace that is already known and installing a plugin that is already installed both exit
/// 0. The two removal commands exit 1 for a thing that is not there, so <see cref="Remove"/> is
/// for a plugin the settings say is present.
/// </para>
/// </remarks>
public sealed class PluginInstaller
{
    private readonly IClaudeCli _cli;
    private readonly DashboardPaths _paths;
    private readonly ILogger _logger;

    /// <summary>Creates the installer.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public PluginInstaller(IClaudeCli cli, DashboardPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _cli = cli;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The folder Claude Code is pointed at.</summary>
    public string PluginFolder => _paths.PluginFolder;

    /// <summary>
    /// Rewrites the plugin files when they differ from what this build expects.
    /// </summary>
    /// <remarks>
    /// For a start that finds the plugin already registered. Claude Code loads the plugin in
    /// place, so a build that changes the event set reaches the next session through these files
    /// and through nothing else.
    /// </remarks>
    public bool EnsureFiles() => HookPlugin.EnsureWritten(_paths, _logger);

    /// <summary>
    /// Writes the script and the plugin files, then asks Claude Code to add the marketplace and
    /// install the plugin from it.
    /// </summary>
    /// <remarks>
    /// The files first. A plugin whose hook names a script that is not there would make Claude
    /// Code run <c>cmd</c> against a missing path on every event, which is the noise issue #29
    /// exists to remove.
    /// </remarks>
    public PluginResult Install()
    {
        _paths.TryEnsureCreated(out _);
        HookScript.EnsureWritten(_paths, _logger);

        if (!File.Exists(_paths.HookScriptFile))
        {
            return Fail($"{_paths.HookScriptFile} could not be written");
        }

        if (!EnsureFiles())
        {
            return Fail($"the plugin files could not be written to {PluginFolder}");
        }

        var added = _cli.Run(["plugin", "marketplace", "add", PluginFolder]);

        if (!added.Found)
        {
            _logger.Information(
                "The claude program was not found, so the dashboard's plugin was not registered.");

            return new PluginResult(PluginOutcome.CliNotFound);
        }

        if (!added.Succeeded)
        {
            return Fail($"claude plugin marketplace add exited {added.ExitCode}: {added.Output}");
        }

        var installed = _cli.Run(["plugin", "install", HookPlugin.Id]);

        if (!installed.Succeeded)
        {
            return Fail($"claude plugin install exited {installed.ExitCode}: {installed.Output}");
        }

        _logger.Information(
            "Claude Code registered the plugin {Plugin} from {Folder}.",
            HookPlugin.Id,
            PluginFolder);

        return new PluginResult(PluginOutcome.Registered);
    }

    /// <summary>
    /// Asks Claude Code to uninstall the plugin and to forget its marketplace.
    /// </summary>
    /// <remarks>
    /// Both commands run even when the first fails, so one refusal does not leave the other half
    /// behind. The plugin files are left on disk, as <see cref="HookInstaller.Remove"/> leaves the
    /// script: nothing runs them once Claude Code has forgotten them.
    /// </remarks>
    public PluginResult Remove()
    {
        var uninstalled = _cli.Run(["plugin", "uninstall", HookPlugin.Id]);

        if (!uninstalled.Found)
        {
            _logger.Warning(
                "The claude program was not found, so the dashboard's plugin was not removed.");

            return new PluginResult(PluginOutcome.CliNotFound);
        }

        var forgotten = _cli.Run(["plugin", "marketplace", "remove", HookPlugin.Name]);

        if (uninstalled.Succeeded && forgotten.Succeeded)
        {
            _logger.Information("Claude Code removed the plugin {Plugin}.", HookPlugin.Id);

            return new PluginResult(PluginOutcome.Removed);
        }

        return Fail(
            $"claude plugin uninstall exited {uninstalled.ExitCode}: {uninstalled.Output}; " +
            $"claude plugin marketplace remove exited {forgotten.ExitCode}: {forgotten.Output}");
    }

    private PluginResult Fail(string problem)
    {
        _logger.Warning("The dashboard's Claude Code plugin: {Problem}.", problem);

        return new PluginResult(PluginOutcome.Failed, problem);
    }
}
