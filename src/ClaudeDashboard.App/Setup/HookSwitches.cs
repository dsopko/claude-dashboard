using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// <c>--install-hooks</c> and <c>--remove-hooks</c>: the one-shot switches that write Claude
/// Code's settings (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>They exist because first-run setup does not.</strong> T10.2 is unbuilt, and without a
/// call site <see cref="HookInstaller"/> would be code nobody could run and no new user could get
/// hooks at all. These are the operator's tool today and T10.2's call site tomorrow.
/// </para>
/// <para>
/// <strong>They are no longer the only thing that writes the operator's hooks, and the sentence
/// that said so is kept because it explains the half that still holds (issue #39).</strong> It read:
/// "They are the only thing that writes the operator's hooks, and they never run by themselves …
/// the running dashboard reads Claude Code's settings to check its handler is there and writes
/// nothing." T1.32 makes a start put a <em>missing</em> handler back, because until then nothing
/// called the install step at all. <strong>Removal is untouched by that</strong>: nothing but
/// <see cref="Remove"/> takes a handler out, nothing runs it but this switch, and nothing is
/// written on the way down — a build that removed an <c>http</c> handler on its own would still be
/// indistinguishable from the design issue #29 removed.
/// </para>
/// <para>
/// <strong>Which is why <c>--remove-hooks</c> also clears
/// <see cref="ClaudeDashboard.App.Configuration.DashboardSettings.InstallHooksAtStart"/>.</strong>
/// An operator who removes their hooks and finds them back after a restart has been overruled by
/// the application, and the switch would mean nothing. <see cref="StartupHookInstall.RecordSwitch"/>
/// is where that is written down, and <c>--install-hooks</c> sets it back.
/// </para>
/// <para>
/// <strong>They exit without starting the UI, and before the single-instance gate.</strong> Before
/// the gate on purpose: an operator whose dashboard is running must still be able to repair their
/// hooks, and a switch that refused because the application was open would be useless exactly when
/// it was needed.
/// </para>
/// <para>
/// <strong>Every removal is printed by name.</strong> Both removal rules match on a shape rather
/// than on a marker, so an entry the operator wrote themselves can match. Printing what left their
/// file is the safeguard, and it is a requirement rather than a courtesy.
/// </para>
/// </remarks>
public static class HookSwitches
{
    /// <summary>Writes the script and merges the handler.</summary>
    public const string Install = "--install-hooks";

    /// <summary>Takes out both the command handler and the legacy HTTP ones.</summary>
    public const string Remove = "--remove-hooks";

    /// <summary>The switch named on the command line, or null when none was.</summary>
    /// <remarks>
    /// <para>
    /// Ordinal-ignore-case, and the first one wins. Both at once is not an error worth its own
    /// path: they are opposites, so the operator meant the first.
    /// </para>
    /// <para>
    /// <strong>The canonical spelling comes back, not the one that was typed.</strong> Every caller
    /// compares the answer against <see cref="Install"/> or <see cref="Remove"/>, and returning
    /// <c>--INSTALL-HOOKS</c> would make a case-insensitive match here into a case-sensitive
    /// failure two calls later.
    /// </para>
    /// <para>
    /// Whole arguments only. A prefix match would make <c>--install-hooks-please</c> start an
    /// installer instead of the dashboard.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static string? Requested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (var argument in args)
        {
            if (string.Equals(argument, Install, StringComparison.OrdinalIgnoreCase))
            {
                return Install;
            }

            if (string.Equals(argument, Remove, StringComparison.OrdinalIgnoreCase))
            {
                return Remove;
            }
        }

        return null;
    }

    /// <summary>Runs <paramref name="requested"/> and reports what it did.</summary>
    /// <param name="requested">One of <see cref="Install"/> or <see cref="Remove"/>.</param>
    /// <param name="installer">The installer to drive.</param>
    /// <param name="report">Where the lines go — the console, or a test's list.</param>
    /// <remarks>
    /// <para>
    /// <strong>The reporter is a parameter</strong> so that what is said can be asserted without a
    /// console, and so that a test cannot pass by writing nowhere. It is the only reason this is
    /// not simply a method on <see cref="HookInstaller"/>.
    /// </para>
    /// <para>
    /// <strong>The exit code is the machine-readable half.</strong> Zero means it did what was
    /// asked; anything else means it could not, and T10.2 will read that rather than the text.
    /// </para>
    /// </remarks>
    /// <param name="plugin">
    /// The plugin route (issue #30), or <see langword="null"/> to keep to the settings file.
    /// </param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requested"/>, <paramref name="installer"/> or <paramref name="report"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requested"/> is not one of the two switches.</exception>
    public static int Run(
        string requested,
        HookInstaller installer,
        Action<string> report,
        PluginInstaller? plugin = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(report);

        if (string.Equals(requested, Install, StringComparison.OrdinalIgnoreCase))
        {
            return RunInstall(installer, report, plugin);
        }

        if (string.Equals(requested, Remove, StringComparison.OrdinalIgnoreCase))
        {
            return RunRemove(installer, report, plugin);
        }

        throw new ArgumentException($"Not a hook switch: {requested}", nameof(requested));
    }

    /// <summary>
    /// Installs by the plugin route when it can, and by the settings file when it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This switch is also the migration (issue #30).</strong> A start never moves an
    /// existing settings handler to the plugin, because a session that is already open does not
    /// see a new plugin and would stop reporting. An operator who runs this is asking, and is
    /// told to restart the open sessions. So once Claude Code has registered the plugin, the old
    /// settings entries are taken out here — the one write to that file the plugin route makes,
    /// and only when there is something of ours in it.
    /// </para>
    /// <para>
    /// <strong>The settings file is the fallback, never a refusal.</strong> No <c>claude</c>
    /// program, a refusal from Claude Code, or a plugin of the same name that belongs to another
    /// data folder each end in the settings handler, with the reason printed. The operator asked
    /// for hooks; they get hooks.
    /// </para>
    /// </remarks>
    private static int RunInstall(HookInstaller installer, Action<string> report, PluginInstaller? plugin)
    {
        if (plugin is null)
        {
            return InstallIntoSettings(installer, report);
        }

        var before = installer.Check();

        if (before.ForeignPlugin is { } foreign)
        {
            report($"Claude Code already has a plugin named {HookPlugin.Name} from another data folder: {foreign}");
            report("Two data folders cannot both register it, so this one uses Claude Code's settings file.");

            return InstallIntoSettings(installer, report);
        }

        // An explicit request, so it may turn back on a plugin the operator turned off (the ruling of
        // 2026-10-01): `claude plugin install` does that, measured on 2.1.286. A start never does.
        if (before.PluginDisabled)
        {
            report($"The plugin {HookPlugin.Id} is turned off in Claude Code. --install-hooks turns it back on.");
        }

        // Read again after a failure: a claude that recorded the plugin and then failed has still
        // registered it, and the settings handler must not go in beside it (the issue #30 review, M2).
        var registered = plugin.Install(() => installer.Check().PluginEnabled);

        if (registered.Outcome != PluginOutcome.Registered)
        {
            report(registered.Outcome == PluginOutcome.CliNotFound
                ? "The claude program was not found, so the plugin could not be registered."
                : $"The plugin could not be registered: {registered.Problem}");
            report("Writing the hook into Claude Code's settings file instead.");

            return InstallIntoSettings(installer, report);
        }

        report($"Plugin:   {HookPlugin.Id}");
        report($"Folder:   {plugin.PluginFolder}");
        report($"Script:   {installer.ScriptPath}");
        report($"Events:   {string.Join(", ", HookEventNames.Accepted.Order(StringComparer.Ordinal))}");
        report("Registered with Claude Code. Claude Code recorded it; the dashboard did not write its settings.");

        var (result, removed) = installer.Remove();

        ReportRemoved(removed, report);

        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                report($"Took {removed.Total} old entr{(removed.Total == 1 ? "y" : "ies")} out of Claude Code's settings. Backup: {result.BackupPath ?? "(none needed)"}");
                break;

            case SettingsWriteOutcome.NothingToDo:
                break;

            default:
                report($"FAILED to take the old entries out of Claude Code's settings: {result.Problem}.");
                report("Until they are out, each event is posted twice.");
                return 1;
        }

        report("Restart every Claude Code session that is open now. A session that is already open does not see a new plugin.");

        return 0;
    }

    private static int InstallIntoSettings(HookInstaller installer, Action<string> report)
    {
        var result = installer.Install();

        report($"Script:   {installer.ScriptPath}");
        report($"Runs as:  {HookInstaller.Interpreter} /c <script>");
        report($"Events:   {string.Join(", ", HookEventNames.Accepted.Order(StringComparer.Ordinal))}");

        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                report($"Installed. Backup: {result.BackupPath ?? "(none needed — there was no file)"}");
                return 0;

            case SettingsWriteOutcome.NothingToDo:
                // Not a failure and not a no-op the operator should worry about: installing twice
                // is meant to be safe, and this is what "safe" looks like from the outside.
                report("Already installed. Nothing changed.");
                return 0;

            default:
                report($"FAILED: {result.Problem}. Claude Code's settings are unchanged.");
                return 1;
        }
    }

    /// <summary>Takes out the plugin when Claude Code has it, then the settings entries.</summary>
    /// <remarks>
    /// Both, always, so that one switch leaves nothing of the dashboard's behind whichever route
    /// put it there. A plugin that could not be removed makes the switch fail even when the
    /// settings half succeeded: Claude Code would go on running the script, and exit code zero
    /// would say it does not.
    /// </remarks>
    private static int RunRemove(HookInstaller installer, Action<string> report, PluginInstaller? plugin)
    {
        var pluginGone = plugin is null || RemovePlugin(installer, plugin, report);
        var code = RemoveFromSettings(installer, report);

        return pluginGone ? code : 1;
    }

    private static bool RemovePlugin(HookInstaller installer, PluginInstaller plugin, Action<string> report)
    {
        // Asked of the settings first: Claude Code's removal commands fail for a plugin that is
        // not there, and that failure would be reported as ours.
        if (!installer.Check().PluginEnabled)
        {
            return true;
        }

        var removed = plugin.Remove();

        switch (removed.Outcome)
        {
            case PluginOutcome.Removed:
                report($"Removed plugin:    {HookPlugin.Id}");
                report($"The plugin files are left at {plugin.PluginFolder}; nothing loads them now.");
                return true;

            case PluginOutcome.CliNotFound:
                report("FAILED: the claude program was not found, so the plugin was not removed.");
                report($"Run: claude plugin uninstall {HookPlugin.Id}");
                return false;

            default:
                report($"FAILED: the plugin was not removed: {removed.Problem}");
                return false;
        }
    }

    private static void ReportRemoved(HookRemoval removed, Action<string> report)
    {
        foreach (var path in removed.ScriptPaths)
        {
            report($"Removed hook:      {path}");
        }

        foreach (var url in removed.Urls)
        {
            report($"Removed old hook:  {url}");
        }

        foreach (var url in removed.AllowListUrls)
        {
            report($"Removed allowlist: {url}");
        }
    }

    private static int RemoveFromSettings(HookInstaller installer, Action<string> report)
    {
        var (result, removed) = installer.Remove();

        ReportRemoved(removed, report);

        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                report($"Removed {removed.Total} entr{(removed.Total == 1 ? "y" : "ies")}. Backup: {result.BackupPath ?? "(none needed)"}");
                report($"The script itself is left at {installer.ScriptPath}; nothing runs it now.");
                return 0;

            case SettingsWriteOutcome.NothingToDo:
                report("Nothing of the dashboard's was in Claude Code's settings. Nothing changed.");
                return 0;

            default:
                report($"FAILED: {result.Problem}. Claude Code's settings are unchanged.");
                return 1;
        }
    }
}
