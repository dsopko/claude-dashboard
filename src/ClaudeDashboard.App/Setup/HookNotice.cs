using System.ComponentModel;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.App.Setup;

/// <summary>Which finding a <see cref="HookNotice"/> is showing.</summary>
public enum HookNoticeKind
{
    /// <summary>Nothing is shown.</summary>
    None = 0,

    /// <summary>Claude Code has the dashboard's plugin turned off.</summary>
    PluginDisabled = 1,

    /// <summary>The operator removed the plugin with <c>--remove-hooks</c>.</summary>
    PluginRemoved = 2,

    /// <summary>No Claude Code install was detected.</summary>
    ClaudeCodeNotInstalled = 3,

    /// <summary>The <c>claude</c> program was not found, so the plugin was not registered.</summary>
    ClaudeNotFound = 4,

    /// <summary>Claude Code refused to register the plugin.</summary>
    ClaudeRefused = 5,

    /// <summary>Claude Code's settings file could not be read.</summary>
    SettingsUnreadable = 6,

    /// <summary>The dashboard's own settings file could not be read, so the opt-out is unknown.</summary>
    OptOutUnknown = 7,

    /// <summary>A plugin of the same name belongs to another data folder.</summary>
    OtherDataFolder = 8,

    /// <summary>Claude Code's settings still hold a hook from a build before the plugin.</summary>
    OldHooks = 9,

    /// <summary>The plugin was registered by this start; open sessions need a restart.</summary>
    JustRegistered = 10,
}

/// <summary>
/// What a start found about the dashboard's connection to Claude Code that the operator must see
/// on screen, not only in the log (the operator's rulings of 2026-10-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A dashboard that receives nothing must not look like a quiet day.</strong> The plugin
/// is the only route by which Claude Code reaches the dashboard, and the dashboard works round
/// nothing: when the plugin is missing, turned off, or could not be registered, it says so here,
/// with what to do. The window shows <see cref="Text"/>; the tray tooltip leads with
/// <see cref="TrayText"/>, the way it leads with a port that is taken.
/// </para>
/// <para>
/// <strong>Two kinds of notice, and they clear differently.</strong> Most say "nothing is
/// reporting". An event that arrives is proof of the opposite, so
/// <see cref="EventArrived"/> clears those — a notice that went on saying "receives nothing"
/// above rows that are updating would be a statement the screen itself contradicts. The other
/// kind is about a file: an old hook still in Claude Code's settings. Events arrive normally in
/// that state, through that very hook, so they prove nothing; that notice stays until a start
/// finds the hook gone.
/// </para>
/// <para>
/// <strong>Every command in a text here was run before it was written</strong>, on Claude Code
/// 2.1.286: <c>claude plugin enable</c>, <c>claude plugin marketplace add</c> with a folder, and
/// <c>claude plugin install</c> each exit 0. A session that was open before any of them reported
/// nothing afterwards, and a session started after reported at once — so each text says to
/// restart the open sessions.
/// </para>
/// <para>
/// Set on the start's thread, before the window or the tray reads it, and cleared on the UI
/// thread. Raised as property changes so a binding made earlier still follows.
/// </para>
/// </remarks>
public sealed class HookNotice : INotifyPropertyChanged
{
    /// <summary>The tray's short form for a turned-off plugin, in the voice of its other faults.</summary>
    public const string PluginDisabledShort = "plugin off · not receiving hooks";

    /// <summary>The tray's short form for every other way of not being connected.</summary>
    public const string NotConnectedShort = "not connected to Claude Code";

    /// <summary>The tray's short form when there is no Claude Code to connect to.</summary>
    public const string NoClaudeCodeShort = "no Claude Code install detected";

    /// <summary>The tray's short form for an old hook in Claude Code's settings.</summary>
    public const string OldHooksShort = "old hook in Claude Code settings";

    private const string Restart =
        "Then restart each Claude Code session that is open: an open session does not see the change.";

    private const string ClearsOnEventText = "This notice clears when a session reports.";

    /// <summary>The window's text for a turned-off plugin.</summary>
    public static readonly string PluginDisabledText =
        "Claude Code's plugin for this dashboard is turned off, so the dashboard receives nothing from " +
        $"Claude Code. To turn it on, run this in a terminal: claude plugin enable {HookPlugin.Id}. " +
        $"{Restart} {ClearsOnEventText}";

    /// <summary>The window's text for a plugin the operator removed.</summary>
    public static readonly string PluginRemovedText =
        "This dashboard is not connected to Claude Code, because its plugin was removed with " +
        "--remove-hooks. To connect it again, run ClaudeDashboard.App.exe --install-hooks from the " +
        $"install folder. {Restart} {ClearsOnEventText}";

    /// <summary>The window's text when no Claude Code is installed.</summary>
    public static readonly string ClaudeCodeNotInstalledText =
        "No Claude Code install was detected on this computer, so the dashboard has nothing to " +
        "listen to. Install Claude Code, then restart the dashboard.";

    /// <summary>The window's text when the dashboard's own settings could not be read.</summary>
    public static readonly string OptOutUnknownText =
        "The dashboard could not read its own settings file, so it does not know whether you removed " +
        "its plugin, and it has not connected itself to Claude Code. Fix or delete the dashboard's " +
        "settings file, then restart the dashboard. Or run ClaudeDashboard.App.exe --install-hooks " +
        "from the install folder.";

    /// <summary>The window's text for an old hook, when the plugin is not registered.</summary>
    public static readonly string OldHooksText =
        "Your Claude Code settings still hold an old Claude Dashboard hook. Remove it, then restart " +
        "the dashboard. The easy way: ask Claude, \"remove all hooks for Claude Dashboard from my " +
        "settings.\" Or remove them yourself with the /hooks command.";

    /// <summary>The window's text for an old hook beside an enabled plugin.</summary>
    public static readonly string OldHooksTwiceText =
        "Your Claude Code settings still hold an old Claude Dashboard hook, so every event arrives " +
        "twice. Remove it, then restart the dashboard. The easy way: ask Claude, \"remove all hooks " +
        "for Claude Dashboard from my settings.\" Or remove them yourself with the /hooks command.";

    /// <summary>The window's text when this start registered the plugin.</summary>
    public static readonly string JustRegisteredText =
        "The dashboard has just connected itself to Claude Code. A Claude Code session that was " +
        $"already open does not report until you restart it. {ClearsOnEventText}";

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Which finding is shown.</summary>
    public HookNoticeKind Kind { get; private set; }

    /// <summary>The window's text, or null when there is nothing to say.</summary>
    public string? Text { get; private set; }

    /// <summary>The tray tooltip's short form, or null when the tooltip has nothing to lead with.</summary>
    public string? TrayText { get; private set; }

    /// <summary>Whether there is anything to show.</summary>
    public bool IsShown => Text is not null;

    /// <summary>Whether an arriving event clears what is shown.</summary>
    public bool ClearsOnEvent { get; private set; }

    /// <summary>The two commands that register the plugin by hand, for <paramref name="pluginFolder"/>.</summary>
    public static string ManualCommands(string pluginFolder) =>
        $"claude plugin marketplace add \"{pluginFolder}\" — and then: claude plugin install {HookPlugin.Id}";

    /// <summary>Shows that the dashboard's plugin is turned off.</summary>
    public void ShowPluginDisabled() =>
        Show(HookNoticeKind.PluginDisabled, PluginDisabledText, PluginDisabledShort, clearsOnEvent: true);

    /// <summary>Shows that the operator removed the plugin and no start puts it back.</summary>
    public void ShowPluginRemoved() =>
        Show(HookNoticeKind.PluginRemoved, PluginRemovedText, NotConnectedShort, clearsOnEvent: true);

    /// <summary>Shows that no Claude Code install was detected.</summary>
    public void ShowClaudeCodeNotInstalled() =>
        Show(HookNoticeKind.ClaudeCodeNotInstalled, ClaudeCodeNotInstalledText, NoClaudeCodeShort, clearsOnEvent: true);

    /// <summary>Shows that the <c>claude</c> program was not found, with the commands to run by hand.</summary>
    /// <exception cref="ArgumentException"><paramref name="pluginFolder"/> is null, empty, or whitespace.</exception>
    public void ShowClaudeNotFound(string pluginFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFolder);

        Show(
            HookNoticeKind.ClaudeNotFound,
            "The dashboard could not find the claude program, so it could not connect itself to Claude " +
            $"Code. Run these two commands in a terminal: {ManualCommands(pluginFolder)}. {Restart} " +
            ClearsOnEventText,
            NotConnectedShort,
            clearsOnEvent: true);
    }

    /// <summary>Shows that Claude Code refused the plugin, with what it said and the commands to try.</summary>
    /// <param name="pluginFolder">The dashboard's plugin folder.</param>
    /// <param name="problem">
    /// What <c>claude</c> printed, or why the plugin files could not be written. Shortened: the
    /// notice is a row in a small window, and the log holds the whole of it.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="pluginFolder"/> is null, empty, or whitespace.</exception>
    public void ShowClaudeRefused(string pluginFolder, string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFolder);

        Show(
            HookNoticeKind.ClaudeRefused,
            $"Claude Code did not register the dashboard's plugin: {Shortened(problem)} To try it by " +
            $"hand, run these two commands in a terminal: {ManualCommands(pluginFolder)}. {Restart} " +
            ClearsOnEventText,
            NotConnectedShort,
            clearsOnEvent: true);
    }

    /// <summary>Shows that Claude Code's settings file could not be read.</summary>
    public void ShowSettingsUnreadable(string? problem) =>
        Show(
            HookNoticeKind.SettingsUnreadable,
            "The dashboard could not read Claude Code's settings file, so it cannot tell whether it is " +
            $"connected to Claude Code: {Shortened(problem)} It changed nothing. {ClearsOnEventText}",
            "cannot read Claude Code's settings",
            clearsOnEvent: true);

    /// <summary>Shows that the dashboard's own settings could not be read, so nothing was registered.</summary>
    public void ShowOptOutUnknown() =>
        Show(HookNoticeKind.OptOutUnknown, OptOutUnknownText, NotConnectedShort, clearsOnEvent: true);

    /// <summary>Shows that another data folder holds the plugin name.</summary>
    /// <exception cref="ArgumentException"><paramref name="foreignFolder"/> is null, empty, or whitespace.</exception>
    public void ShowOtherDataFolder(string foreignFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignFolder);

        Show(
            HookNoticeKind.OtherDataFolder,
            "Claude Code already has a Claude Dashboard plugin that belongs to another data folder " +
            $"({foreignFolder}), so this dashboard is not connected. Check {DashboardPaths.HomeVariable}.",
            NotConnectedShort,
            clearsOnEvent: true);
    }

    /// <summary>Shows that Claude Code's settings still hold a hook from before the plugin.</summary>
    /// <param name="pluginEnabled">
    /// Whether the plugin is enabled as well, in which case every event arrives twice and the text
    /// says so.
    /// </param>
    /// <remarks>
    /// Not cleared by an event. Events arrive normally in this state, through the old hook itself.
    /// </remarks>
    public void ShowOldHooks(bool pluginEnabled) =>
        Show(
            HookNoticeKind.OldHooks,
            pluginEnabled ? OldHooksTwiceText : OldHooksText,
            OldHooksShort,
            clearsOnEvent: false);

    /// <summary>Shows that this start registered the plugin and open sessions need a restart.</summary>
    /// <remarks>
    /// No tray text: nothing is wrong, so the tooltip has no fault to lead with.
    /// </remarks>
    public void ShowJustRegistered() =>
        Show(HookNoticeKind.JustRegistered, JustRegisteredText, trayText: null, clearsOnEvent: true);

    /// <summary>
    /// A hook event reached the dashboard. Clears a notice that said nothing was reporting.
    /// </summary>
    public void EventArrived()
    {
        if (IsShown && ClearsOnEvent)
        {
            Kind = HookNoticeKind.None;
            Text = null;
            TrayText = null;
            ClearsOnEvent = false;
            Raise();
        }
    }

    private void Show(HookNoticeKind kind, string text, string? trayText, bool clearsOnEvent)
    {
        Kind = kind;
        Text = text;
        TrayText = trayText;
        ClearsOnEvent = clearsOnEvent;
        Raise();
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
    }

    /// <summary>One line of at most 200 characters, ending in a full stop.</summary>
    private static string Shortened(string? problem)
    {
        var line = string.Join(' ', (problem ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (line.Length == 0)
        {
            return "no reason was given.";
        }

        if (line.Length > 200)
        {
            line = line[..200] + "…";
        }

        return line.EndsWith('.') || line.EndsWith('…') ? line : line + ".";
    }
}
