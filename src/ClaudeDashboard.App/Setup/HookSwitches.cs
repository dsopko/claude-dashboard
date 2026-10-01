using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// <c>--install-hooks</c> and <c>--remove-hooks</c>: the one-shot switches that register the
/// dashboard's plugin with Claude Code and take it out (issues #29 and #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plugin, and nothing else (the operator's ruling of 2026-10-01).</strong> These
/// switches wrote Claude Code's settings file until then. They no longer do, and nothing in the
/// product does: both ask Claude Code, through the <c>claude</c> program, and Claude Code records
/// the answer in its own files.
/// </para>
/// <para>
/// <strong><c>--remove-hooks</c> also clears
/// <see cref="ClaudeDashboard.App.Configuration.DashboardSettings.InstallHooksAtStart"/>.</strong>
/// An operator who removes the plugin and finds it back after a restart has been overruled by the
/// application, and the switch would mean nothing. <see cref="StartupHookInstall.RecordSwitch"/>
/// is where that is written down, and <c>--install-hooks</c> sets it back.
/// </para>
/// <para>
/// <strong>They exit without starting the UI, and before the single-instance gate.</strong> Before
/// the gate on purpose: an operator whose dashboard is running must still be able to repair the
/// connection, and a switch that refused because the application was open would be useless exactly
/// when it was needed.
/// </para>
/// <para>
/// <strong>An explicit <c>--install-hooks</c> turns a disabled plugin back on.</strong> A start
/// never does. The operator who types the switch is asking, and is told that it does so.
/// </para>
/// </remarks>
public static class HookSwitches
{
    /// <summary>Registers the plugin with Claude Code.</summary>
    public const string Install = "--install-hooks";

    /// <summary>Removes the plugin from Claude Code.</summary>
    public const string Remove = "--remove-hooks";

    /// <summary>What the operator is told when Claude Code's settings still hold a hook from before the plugin.</summary>
    public const string OldHookAdvice =
        "To remove it, ask Claude, \"remove all hooks for Claude Dashboard from my settings.\" Or " +
        "remove them yourself with the /hooks command.";

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
    /// <param name="check">The read of Claude Code's settings.</param>
    /// <param name="report">Where the lines go — the console, or a test's list.</param>
    /// <param name="plugin">The plugin installer.</param>
    /// <remarks>
    /// <para>
    /// <strong>The reporter is a parameter</strong> so that what is said can be asserted without a
    /// console, and so that a test cannot pass by writing nowhere.
    /// </para>
    /// <para>
    /// <strong>The exit code is the machine-readable half.</strong> Zero means it did what was
    /// asked; anything else means it could not.
    /// </para>
    /// </remarks>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requested"/> is not one of the two switches.</exception>
    public static int Run(string requested, HookCheck check, Action<string> report, PluginInstaller plugin)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(plugin);

        if (string.Equals(requested, Install, StringComparison.OrdinalIgnoreCase))
        {
            return RunInstall(check, report, plugin);
        }

        if (string.Equals(requested, Remove, StringComparison.OrdinalIgnoreCase))
        {
            return RunRemove(check, report, plugin);
        }

        throw new ArgumentException($"Not a hook switch: {requested}", nameof(requested));
    }

    /// <summary>Registers the plugin, or says why it could not and what to do.</summary>
    /// <remarks>
    /// There is no other route to fall back to. Where the plugin cannot be registered the switch
    /// fails, and its report is the remedy: the two commands to run by hand, or the old hook to
    /// remove first.
    /// </remarks>
    private static int RunInstall(HookCheck check, Action<string> report, PluginInstaller plugin)
    {
        var before = check.Check();

        if (before.Problem is { } problem)
        {
            report($"FAILED: Claude Code's settings file could not be read: {problem}");
            report("Nothing was changed.");

            return 1;
        }

        // The plugin waits for the old hook to go: the two together would post every event twice.
        if (before.OldHookEvents > 0)
        {
            report($"FAILED: your Claude Code settings still hold an old Claude Dashboard hook, on {before.OldHookEvents} event(s).");
            report(OldHookAdvice);
            report("Then run --install-hooks again.");

            return 1;
        }

        if (before.ForeignPlugin is { } foreign)
        {
            report($"FAILED: Claude Code already has a plugin named {HookPlugin.Name} that belongs to another data folder: {foreign}");
            report("Two data folders cannot both register it.");

            return 1;
        }

        // An explicit request, so it may turn back on a plugin the operator turned off (the ruling of
        // 2026-10-01): `claude plugin install` does that, measured on 2.1.286. A start never does.
        if (before.PluginDisabled)
        {
            report($"The plugin {HookPlugin.Id} is turned off in Claude Code. --install-hooks turns it back on.");
        }

        // Read again after a failure: a claude that recorded the plugin and then failed has still
        // registered it (the issue #30 review, M2).
        var registered = plugin.Install(() => check.Check().PluginEnabled);

        switch (registered.Outcome)
        {
            case PluginOutcome.Registered:
                report($"Plugin:   {HookPlugin.Id}");
                report($"Folder:   {plugin.PluginFolder}");
                report($"Script:   {check.ScriptPath}");
                report($"Events:   {string.Join(", ", HookEventNames.Accepted.Order(StringComparer.Ordinal))}");
                report("Registered with Claude Code.");
                report("Restart every Claude Code session that is open now. A session that is already open does not see a new plugin.");

                return 0;

            case PluginOutcome.CliNotFound:
                report("FAILED: the claude program was not found, so the plugin could not be registered.");
                report("Run these two commands yourself:");
                report($"  claude plugin marketplace add \"{plugin.PluginFolder}\"");
                report($"  claude plugin install {HookPlugin.Id}");

                return 1;

            default:
                report($"FAILED: the plugin could not be registered: {registered.Problem}");
                report("To try it by hand, run these two commands:");
                report($"  claude plugin marketplace add \"{plugin.PluginFolder}\"");
                report($"  claude plugin install {HookPlugin.Id}");

                return 1;
        }
    }

    /// <summary>Takes the plugin out when Claude Code has it.</summary>
    /// <remarks>
    /// Asked of the settings first: Claude Code's removal commands fail for a plugin that is not
    /// there, and that failure would be reported as ours. A plugin that is turned off is still
    /// registered, and is removed too.
    /// </remarks>
    private static int RunRemove(HookCheck check, Action<string> report, PluginInstaller plugin)
    {
        var before = check.Check();

        if (before.Problem is { } problem)
        {
            report($"FAILED: Claude Code's settings file could not be read: {problem}");
            report("Nothing was changed.");

            return 1;
        }

        var code = 0;

        if (before.PluginEnabled || before.PluginDisabled)
        {
            var removed = plugin.Remove();

            switch (removed.Outcome)
            {
                case PluginOutcome.Removed:
                    report($"Removed plugin:    {HookPlugin.Id}");
                    report($"The plugin files are left at {plugin.PluginFolder}; nothing loads them now.");
                    break;

                case PluginOutcome.CliNotFound:
                    report("FAILED: the claude program was not found, so the plugin was not removed.");
                    report($"Run: claude plugin uninstall {HookPlugin.Id}");
                    code = 1;
                    break;

                default:
                    report($"FAILED: the plugin was not removed: {removed.Problem}");
                    code = 1;
                    break;
            }
        }
        else
        {
            report($"Claude Code does not have the plugin {HookPlugin.Id}. Nothing to remove.");
        }

        // Said, never acted on: the dashboard does not edit Claude Code's settings.
        if (before.OldHookEvents > 0)
        {
            report($"Your Claude Code settings still hold an old Claude Dashboard hook, on {before.OldHookEvents} event(s). This switch does not remove it.");
            report(OldHookAdvice);
        }

        return code;
    }
}
