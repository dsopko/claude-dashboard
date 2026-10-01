using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// The Claude Code plugin that carries the dashboard's hook: its three files, and how to read
/// whether Claude Code has it enabled (issue #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a plugin at all.</strong> <c>~/.claude/settings.json</c> belongs to Claude Code,
/// which writes it too. A second writer can lose the first one's change, and its mistakes break
/// Claude Code rather than the dashboard. A plugin is the door Claude Code provides for exactly
/// this: the hook lives in files the dashboard owns, and Claude Code itself records that the
/// plugin is enabled. The dashboard asks through the <c>claude</c> program
/// (<see cref="PluginInstaller"/>) and writes nothing outside its own folder.
/// </para>
/// <para>
/// <strong>The plugin is a pointer and nothing else.</strong> Its <c>hooks.json</c> holds one
/// handler on every accepted event (<see cref="HookHandlers.ForEveryEvent"/>), and the handler
/// names <c>post-status.cmd</c> by absolute path. The script does not move and does not change, so it
/// still finds <c>listening.txt</c> beside itself and T1.48's token logic is untouched.
/// <c>${CLAUDE_PLUGIN_ROOT}</c> is deliberately not used: with an absolute path it does not
/// matter where Claude Code runs the plugin from.
/// </para>
/// <para>
/// <strong>Measured on 2026-09-30 against Claude Code 2.1.286</strong>, with a throwaway plugin:
/// adding a marketplace from a folder and installing from it both run with no person present and
/// exit 0, a second run of either also exits 0, and Claude Code reports that such a plugin "loads
/// in place" from the folder. A hook whose folder had been moved away did not run, silently. A
/// session that was already open did not see a newly installed plugin; a new session did.
/// </para>
/// </remarks>
public static class HookPlugin
{
    /// <summary>The name of the plugin, and of the one-plugin marketplace that offers it.</summary>
    public const string Name = "claude-dashboard";

    /// <summary>How Claude Code names the plugin: <c>plugin@marketplace</c>.</summary>
    public const string Id = Name + "@" + Name;

    /// <summary>The settings key under which Claude Code records enabled plugins.</summary>
    public const string EnabledKey = "enabledPlugins";

    /// <summary>The settings key under which Claude Code records marketplaces it was given.</summary>
    public const string MarketplacesKey = "extraKnownMarketplaces";

    private const string ManifestFolder = ".claude-plugin";

    /// <summary>The marketplace manifest: one plugin, in this same folder.</summary>
    public static string MarketplaceText { get; } =
        """
        {
          "name": "claude-dashboard",
          "owner": {
            "name": "David Sopko"
          },
          "description": "Written by Claude Dashboard. Offers the one plugin that reports Claude Code session events to it.",
          "plugins": [
            {
              "name": "claude-dashboard",
              "source": "./",
              "description": "Reports Claude Code session events to Claude Dashboard on this computer."
            }
          ]
        }

        """.ReplaceLineEndings("\n");

    /// <summary>The plugin manifest.</summary>
    /// <remarks>
    /// The version never changes. Claude Code loads this plugin in place, so a change to the
    /// files reaches the next session with no update step, and a version that moved would only
    /// add copies to Claude Code's cache.
    /// </remarks>
    public static string ManifestText { get; } =
        """
        {
          "name": "claude-dashboard",
          "displayName": "Claude Dashboard",
          "version": "1.0.0",
          "description": "Reports Claude Code session events to Claude Dashboard on this computer. Written by Claude Dashboard at every start; an edit here is reverted.",
          "author": {
            "name": "David Sopko"
          },
          "repository": "https://github.com/dsopko/claude-dashboard",
          "license": "MIT"
        }

        """.ReplaceLineEndings("\n");

    /// <summary>Where the marketplace manifest goes.</summary>
    public static string MarketplaceFile(DashboardPaths paths) =>
        Path.Combine(Folder(paths), ManifestFolder, "marketplace.json");

    /// <summary>Where the plugin manifest goes.</summary>
    public static string ManifestFile(DashboardPaths paths) =>
        Path.Combine(Folder(paths), ManifestFolder, "plugin.json");

    /// <summary>Where the hooks file goes.</summary>
    public static string HooksFile(DashboardPaths paths) =>
        Path.Combine(Folder(paths), "hooks", "hooks.json");

    /// <summary>
    /// The plugin's <c>hooks.json</c>: the dashboard's handler on every accepted event.
    /// </summary>
    /// <param name="interpreter">The absolute path to <c>cmd.exe</c>.</param>
    /// <param name="scriptPath">The absolute path to <c>post-status.cmd</c>.</param>
    /// <exception cref="ArgumentException">Either path is null, empty, or whitespace.</exception>
    public static string HooksText(string interpreter, string scriptPath)
    {
        var file = new JsonObject
        {
            ["description"] =
                "Written by Claude Dashboard at every start; an edit here is reverted. Each event runs " +
                "the dashboard's forwarder, which posts the event to the dashboard when it is running " +
                "and does nothing when it is not.",
            [HookHandlers.HooksKey] = HookHandlers.ForEveryEvent(interpreter, scriptPath),
        };

        return HookHandlers.Render(file).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    /// Writes the three plugin files when any of them differs from what this build expects.
    /// </summary>
    /// <remarks>
    /// Temporary-then-rename for each, as <see cref="HookScript.EnsureWritten"/> does, so Claude
    /// Code never reads half a file. A failure is logged and reported; the copies already there
    /// are the ones Claude Code goes on using.
    /// </remarks>
    /// <returns>Whether all three files now hold what this build expects.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool EnsureWritten(DashboardPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        if (Matches(paths))
        {
            return true;
        }

        try
        {
            foreach (var (file, text) in Files(paths))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);

                var temporary = $"{file}{ListeningFile.TemporarySuffix}{Guid.NewGuid():N}";

                File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporary, file, overwrite: true);
            }

            logger.Information("Wrote the Claude Code plugin to {Folder}.", Folder(paths));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(
                ex,
                "Could not write the Claude Code plugin to {Folder}. The copy already there is the one " +
                "Claude Code will use, and this is retried at the next start.",
                Folder(paths));

            return false;
        }
    }

    /// <summary>Whether all three files hold exactly what this build expects.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static bool Matches(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            return Files(paths).All(entry =>
                File.Exists(entry.File)
                && string.Equals(File.ReadAllText(entry.File), entry.Text, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether Claude Code's settings say this plugin is enabled.</summary>
    /// <remarks>
    /// Read from the settings file the start check already parses, so the answer costs no process
    /// start. A value of the wrong type is "not enabled", never an exception: this is read at
    /// every start, from a file the operator may have edited by hand.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static bool IsEnabled(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings[EnabledKey] is JsonObject enabled
            && enabled[Id] is JsonValue value
            && value.TryGetValue<bool>(out var on)
            && on;
    }

    /// <summary>
    /// Whether Claude Code's settings say this plugin is <strong>turned off</strong>: named, and set
    /// to <see langword="false"/>, which is what <c>claude plugin disable</c> writes (measured on
    /// Claude Code 2.1.286).
    /// </summary>
    /// <remarks>
    /// An absent entry is not "turned off": it is a plugin that was never installed, or was
    /// uninstalled, and installing it is not overriding anyone. Only an explicit
    /// <see langword="false"/> records a choice.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static bool IsDisabled(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings[EnabledKey] is JsonObject enabled
            && enabled[Id] is JsonValue value
            && value.TryGetValue<bool>(out var on)
            && !on;
    }

    /// <summary>
    /// The folder Claude Code's settings give for the <see cref="Name"/> marketplace, or null when
    /// they name none.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static string? MarketplaceFolder(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings[MarketplacesKey] is JsonObject marketplaces
            && marketplaces[Name] is JsonObject marketplace
            && marketplace["source"] is JsonObject source
            && source["path"] is JsonValue value
            && value.TryGetValue<string>(out var path)
            && !string.IsNullOrWhiteSpace(path)
                ? path
                : null;
    }

    /// <summary>Whether <paramref name="folder"/> is this data folder's plugin folder.</summary>
    /// <remarks>
    /// Compared as Windows compares paths: without regard to case, to a trailing separator, or to
    /// which separator was typed. A folder that cannot be made into a path is not ours.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static bool IsFolderOf(DashboardPaths paths, string? folder)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            return string.Equals(Normal(folder), Normal(Folder(paths)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Normal(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static string Folder(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return paths.PluginFolder;
    }

    private static (string File, string Text)[] Files(DashboardPaths paths) =>
    [
        (MarketplaceFile(paths), MarketplaceText),
        (ManifestFile(paths), ManifestText),
        (HooksFile(paths), HooksText(HookHandlers.Interpreter, paths.HookScriptFile)),
    ];
}
