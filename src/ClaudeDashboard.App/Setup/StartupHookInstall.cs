using System.IO;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>How a start left the dashboard's connection to Claude Code.</summary>
public enum HookStartOutcome
{
    /// <summary>The plugin was already enabled. Nothing was asked and nothing is shown.</summary>
    Connected = 1,

    /// <summary>This start registered the plugin. Open sessions need a restart.</summary>
    Registered = 2,

    /// <summary>Claude Code's settings still hold a hook from before the plugin. Nothing was registered.</summary>
    OldHooks = 3,

    /// <summary>No Claude Code install was detected.</summary>
    NoClaudeCode = 4,

    /// <summary>Claude Code's settings file could not be read.</summary>
    SettingsUnreadable = 5,

    /// <summary>The plugin is turned off, and stays off.</summary>
    PluginDisabled = 6,

    /// <summary>A plugin of the same name belongs to another data folder.</summary>
    OtherDataFolder = 7,

    /// <summary>The dashboard's own settings could not be read, so the opt-out is unknown.</summary>
    OptOutUnknown = 8,

    /// <summary>The operator removed the plugin, and a start does not put it back.</summary>
    Removed = 9,

    /// <summary>The <c>claude</c> program was not found.</summary>
    ClaudeNotFound = 10,

    /// <summary>Claude Code refused, or the plugin files could not be written.</summary>
    ClaudeRefused = 11,
}

/// <summary>
/// The start-time connection: register the dashboard's plugin with Claude Code when it is missing,
/// and say so on screen when the dashboard is not connected (issues #39 and #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plugin is the only route, and the dashboard works round nothing (the operator's
/// ruling of 2026-10-01).</strong> Until then a start that could not register the plugin wrote the
/// hook into Claude Code's settings file instead, and a machine with an old handler there was
/// topped up in place. Both are gone, with every other write of that file. What a start does now
/// is one of two things: ask Claude Code to register the plugin, or show the operator why it is
/// not connected and what to do.
/// </para>
/// <para>
/// <strong>It exists because T1.28 made registration an install step and left nothing running
/// that step</strong> (issue #39): a user who had never opened a terminal started the exe and
/// received no events, for ever, with one warning line in a log they will not open.
/// </para>
/// <para>
/// <strong>It is a separate type rather than lines in <c>Program.Main</c>, because <c>Main</c>
/// cannot be called from a test.</strong> Every rule here fails silently in production — a
/// dashboard that receives nothing looks exactly like a quiet day — and would be covered by
/// nothing. <c>Program.cs</c> keeps one call, and a source-text tripwire keeps it there.
/// </para>
/// <para>
/// <strong>Nothing is done on quit, and that is not an omission.</strong> The plugin outlives the
/// process: its hook names a script, the script reads <c>listening.txt</c>, and a closed dashboard
/// therefore costs nothing.
/// </para>
/// </remarks>
public static class StartupHookInstall
{
    /// <summary>
    /// Why a start does not register the plugin, or <see langword="null"/> when nothing stands in
    /// the way and it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One question, answered in a fixed order, because several findings can hold at once and the
    /// operator is shown one notice.
    /// </para>
    /// <para>
    /// <strong>No Claude Code first (T1.33, issue #42).</strong> When Claude Code's configuration
    /// directory does not exist, this machine has never had Claude Code, and running <c>claude</c>
    /// could create it. Nothing is asked and nothing is created.
    /// </para>
    /// <para>
    /// <strong>A settings file that would not read is next.</strong> Whether the plugin is there
    /// is then unknown, and asking Claude Code to register it would have Claude Code write a file
    /// that is already damaged.
    /// </para>
    /// <para>
    /// <strong>An old hook outranks the plugin's own state.</strong> A build from before the plugin
    /// left a handler in Claude Code's settings, and it still delivers every event. Registering
    /// the plugin beside it would post each event twice, so the plugin waits until the operator
    /// has removed the old hook. It outranks an enabled plugin too, because both together is the
    /// state that already posts twice, and the notice must say so.
    /// </para>
    /// <para>
    /// <strong>A turned-off plugin stays off</strong> (the ruling of 2026-10-01). <c>claude plugin
    /// install</c> turns a disabled plugin back on — measured on 2.1.286 — so a start does not run
    /// it.
    /// </para>
    /// <para>
    /// <strong>An unreadable opt-out is unknown, not consent (review of T1.32).</strong> When the
    /// dashboard's own settings file cannot be read, <c>SettingsStore</c> hands back defaults, and
    /// the default says install. But a recorded <c>--remove-hooks</c> lives in exactly the file
    /// that could not be read. Only <see cref="SettingsLoadOutcome.Unreadable"/> refuses: a missing
    /// file is a first run, where the default stands in for nothing and must register.
    /// </para>
    /// </remarks>
    /// <param name="presence">What <see cref="HookCheck.Check"/> found.</param>
    /// <param name="installAtStart">The operator's setting.</param>
    /// <param name="settingsOutcome">
    /// How <paramref name="installAtStart"/> was arrived at — read, defaulted from a missing file,
    /// or defaulted from one that would not read.
    /// </param>
    public static HookStartOutcome? Refusal(
        HookPresence presence,
        bool installAtStart,
        SettingsLoadOutcome settingsOutcome)
    {
        if (!presence.ClaudeCodeInstalled)
        {
            return HookStartOutcome.NoClaudeCode;
        }

        if (presence.Problem is not null)
        {
            return HookStartOutcome.SettingsUnreadable;
        }

        if (presence.OldHookEvents > 0)
        {
            return HookStartOutcome.OldHooks;
        }

        if (presence.PluginEnabled)
        {
            return HookStartOutcome.Connected;
        }

        if (presence.PluginDisabled)
        {
            return HookStartOutcome.PluginDisabled;
        }

        if (presence.ForeignPlugin is not null)
        {
            return HookStartOutcome.OtherDataFolder;
        }

        if (settingsOutcome == SettingsLoadOutcome.Unreadable)
        {
            return HookStartOutcome.OptOutUnknown;
        }

        return installAtStart ? null : HookStartOutcome.Removed;
    }

    /// <summary>
    /// Reads Claude Code's settings, registers the plugin when it is missing and wanted, and shows
    /// a notice for every outcome in which the dashboard is not connected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One read, whatever happens next. <see cref="HookCheck.Check"/> says what is there and logs
    /// it; this says what was done about it, on screen and in the log.
    /// </para>
    /// <para>
    /// <strong>Claude Code's settings file is read and never written.</strong> When the plugin
    /// cannot be registered there is no other door: the notice gives the two commands to run by
    /// hand.
    /// </para>
    /// <para>
    /// <strong>A claude that fails after it recorded the plugin is counted as registered</strong>
    /// (the issue #30 review, M2), by reading the settings again.
    /// </para>
    /// </remarks>
    /// <param name="check">The read of Claude Code's settings.</param>
    /// <param name="installAtStart"><see cref="DashboardSettings.InstallHooksAtStart"/>.</param>
    /// <param name="settingsOutcome">How the dashboard's own settings load went.</param>
    /// <param name="logger">Where what was done is recorded.</param>
    /// <param name="plugin">The plugin installer.</param>
    /// <param name="notice">Where the operator is shown that the dashboard is not connected.</param>
    /// <param name="settingsKeptAside">
    /// Whether this start renamed an unreadable settings file and wrote a fresh one (T1.56). The
    /// opt-out notice, which says to fix or delete the file, is then wrong advice and is not shown:
    /// the settings notice says what this start did. Nothing is registered either way.
    /// </param>
    /// <returns>How the start left the connection.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static HookStartOutcome Run(
        HookCheck check,
        bool installAtStart,
        SettingsLoadOutcome settingsOutcome,
        ILogger logger,
        PluginInstaller plugin,
        HookNotice notice,
        bool settingsKeptAside = false)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(notice);

        var presence = check.Check();

        // A registered plugin is loaded in place, so its files are how a new build's event set
        // reaches Claude Code. Kept current at every start, like the script.
        if (presence.PluginEnabled || presence.PluginDisabled)
        {
            plugin.EnsureFiles();
        }

        if (Refusal(presence, installAtStart, settingsOutcome) is { } refusal)
        {
            Show(refusal, presence, check, logger, notice, settingsKeptAside);

            return refusal;
        }

        var registered = plugin.Install(() => check.Check().PluginEnabled);

        switch (registered.Outcome)
        {
            case PluginOutcome.Registered:
                notice.ShowJustRegistered();

                logger.Information(
                    "The dashboard's plugin was missing, so this start registered {Plugin} from " +
                    "{Folder} with Claude Code. A session that is already open does not see the " +
                    "plugin until it restarts. Set \"installHooksAtStart\": false in the dashboard's " +
                    "settings to stop that.",
                    HookPlugin.Id,
                    plugin.PluginFolder);

                return HookStartOutcome.Registered;

            case PluginOutcome.CliNotFound:
                notice.ShowClaudeNotFound(plugin.PluginFolder);

                logger.Warning(
                    "The claude program was not found, so the dashboard's plugin is not registered and " +
                    "the dashboard receives nothing from Claude Code. To register it by hand: {Commands}",
                    HookNotice.ManualCommands(plugin.PluginFolder));

                return HookStartOutcome.ClaudeNotFound;

            default:
                // PluginInstaller has already logged the reason at Warning.
                notice.ShowClaudeRefused(plugin.PluginFolder, registered.Problem);

                return HookStartOutcome.ClaudeRefused;
        }
    }

    /// <summary>
    /// <see cref="Run"/> for a start, from what the start found in its settings file (T1.56).
    /// </summary>
    /// <remarks>
    /// <strong>The start's first load is the authority, never the file as it is now.</strong> A
    /// start that kept an unreadable file aside has written a fresh one that says
    /// <c>installHooksAtStart: true</c>. Reading that would turn this start into an ordinary one and
    /// register a plugin the operator may have removed: the opt-out was in the file that did not
    /// read (T1.32). So this reads <see cref="SettingsAtStart.Original"/> and nothing else.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static HookStartOutcome RunAtStart(
        HookCheck check,
        SettingsAtStart start,
        ILogger logger,
        PluginInstaller plugin,
        HookNotice notice)
    {
        ArgumentNullException.ThrowIfNull(start);

        return Run(
            check,
            start.Original.Settings.InstallHooksAtStart,
            start.Original.Outcome,
            logger,
            plugin,
            notice,
            settingsKeptAside: start.KeptAside);
    }

    private static void Show(
        HookStartOutcome refusal,
        HookPresence presence,
        HookCheck check,
        ILogger logger,
        HookNotice notice,
        bool settingsKeptAside)
    {
        switch (refusal)
        {
            case HookStartOutcome.NoClaudeCode:
                notice.ShowClaudeCodeNotInstalled();

                // Not an error and not a warning: a machine without Claude Code is an ordinary
                // machine, and this dashboard on it is just early (T1.33). The path checked is
                // named, so a CLAUDE_CONFIG_DIR pointing somewhere odd is diagnosable from the
                // same sentence.
                logger.Information(
                    "No Claude Code install was detected — {Directory} does not exist — so the " +
                    "dashboard's plugin was not registered and nothing was created.",
                    check.ClaudeConfigDirectory);
                break;

            case HookStartOutcome.SettingsUnreadable:
                notice.ShowSettingsUnreadable(presence.Problem);
                break;

            case HookStartOutcome.OldHooks:
                notice.ShowOldHooks(presence.PluginEnabled);

                logger.Warning(
                    presence.PluginEnabled
                        ? "The old hook and the plugin are both there, so every event is posted twice " +
                          "until the old hook is removed."
                        : "The dashboard's plugin is not registered while the old hook is there, " +
                          "because both together would post every event twice.");
                break;

            case HookStartOutcome.PluginDisabled:
                notice.ShowPluginDisabled();
                break;

            case HookStartOutcome.OtherDataFolder:
                notice.ShowOtherDataFolder(presence.ForeignPlugin!);
                break;

            case HookStartOutcome.OptOutUnknown when settingsKeptAside:
                // The file was renamed and a fresh one written (T1.56): "fix or delete the file" would
                // be wrong advice, and the settings notice already says what this start did.
                logger.Information(
                    "The dashboard's own settings file could not be read and was kept aside, so this " +
                    "start registered no plugin. The next start registers it unless \"installHooksAtStart\" " +
                    "is set to false in the new settings file.");
                break;

            case HookStartOutcome.OptOutUnknown:
                notice.ShowOptOutUnknown();

                logger.Warning(
                    "The dashboard's own settings file could not be read, so the " +
                    "\"installHooksAtStart\" opt-out is unknown and the plugin was not registered. " +
                    "Fix or delete that settings file, or run --install-hooks.");
                break;

            case HookStartOutcome.Removed:
                notice.ShowPluginRemoved();

                logger.Information(
                    "The dashboard's plugin is not registered and \"installHooksAtStart\" is false, " +
                    "so nothing was registered. Run --install-hooks to put it back.");
                break;

            default:
                // Connected: nothing to show and nothing to say beyond HookCheck's own line.
                break;
        }
    }

    /// <summary>
    /// Records what a hook switch decided, so the next start honours it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this, <c>--remove-hooks</c> is a no-op with extra steps.</strong> The
    /// operator removes the plugin, restarts, and finds it back — the application overriding a
    /// decision they stated explicitly.
    /// </para>
    /// <para>
    /// <strong>Only on success, and only when the value would change.</strong> A switch that failed
    /// decided nothing. Writing only a differing value keeps <c>--install-hooks</c> from creating a
    /// settings file on a machine that has none purely to record the default.
    /// </para>
    /// <para>
    /// <strong>A settings file that will not read is left alone.</strong> This is the dashboard's
    /// own settings file. The store hands back defaults for an unreadable file, and saving those
    /// would overwrite whatever the operator had written there with a fresh object.
    /// </para>
    /// <para>
    /// <strong>It never throws.</strong> The dashboard's own settings failing to save must not turn
    /// a switch that did its work into a failure.
    /// </para>
    /// </remarks>
    /// <param name="requested">The canonical switch, from <see cref="HookSwitches.Requested"/>.</param>
    /// <param name="exitCode">What the switch returned; anything but zero records nothing.</param>
    /// <param name="store">The dashboard's own settings.</param>
    /// <param name="logger">Where a failure to save is reported.</param>
    /// <param name="report">
    /// Where the operator is told what the flag now means — the same console the switch reports to.
    /// </param>
    /// <returns>Whether the flag was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requested"/>, <paramref name="store"/> or <paramref name="logger"/> is null.</exception>
    public static bool RecordSwitch(
        string requested,
        int exitCode,
        SettingsStore store,
        ILogger logger,
        Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        if (exitCode != 0)
        {
            return false;
        }

        var wanted = string.Equals(requested, HookSwitches.Install, StringComparison.OrdinalIgnoreCase);

        if (!wanted && !string.Equals(requested, HookSwitches.Remove, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var loaded = store.Load();

        if (loaded.Outcome == SettingsLoadOutcome.Unreadable)
        {
            logger.Warning(
                "{Switch} could not record \"installHooksAtStart\": {Problem}. The dashboard's own " +
                "settings file was left exactly as it is, so the next start may register the plugin again.",
                requested,
                loaded.Problem);

            return false;
        }

        if (loaded.Settings.InstallHooksAtStart == wanted)
        {
            return false;
        }

        try
        {
            if (!store.Save(loaded.Settings with { InstallHooksAtStart = wanted }))
            {
                // Saves are refused for this run (T1.56); the store has said so.
                return false;
            }

            logger.Information(
                "{Switch}: \"installHooksAtStart\" is now {Value} in the dashboard's own settings.",
                requested,
                wanted);

            report?.Invoke(wanted
                ? "Starts will register the plugin again if it goes missing (\"installHooksAtStart\" is back on)."
                : "Starts will no longer register the plugin (\"installHooksAtStart\" is now false). --install-hooks turns it back on.");

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(
                ex,
                "{Switch} could not write the dashboard's own settings, so " +
                "\"installHooksAtStart\" is unchanged. The plugin itself was still changed.",
                requested);

            return false;
        }
    }
}
