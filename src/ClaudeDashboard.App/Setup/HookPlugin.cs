using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// The Claude Code plugin that carries the dashboard's hook and its usage mod: its five files, and
/// how to read whether Claude Code has it enabled (issue #30).
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
/// <strong>The usage mod rides in the same plugin</strong> (issue #133, Impl §9.5). Its
/// <c>hooks.json</c> also names <c>register.ts</c> under <c>modules</c>, a function that Claude
/// Code calls inside its own process at <c>session.measure</c>, and that posts the plan's usage
/// to the dashboard. <c>register.ts</c> is <c>mods/usage/hooks/register.ts</c>, embedded in this
/// assembly; <c>listening-file.ts</c> holds one constant, the absolute path of
/// <c>listening.txt</c>, for the reason the script is named by absolute path. The two module
/// files are written before <c>hooks.json</c>, so a <c>hooks.json</c> that names the module is
/// never on disk before the module is. A broken mod costs the usage reading and nothing more: the
/// command hooks still fire (measured in the lab on Claude Code 2.1.294, the Usage Mod
/// Development Guide, "Shipping it").
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

    private const string HooksFolder = "hooks";

    /// <summary>The usage mod's file name, as <c>hooks.json</c> names it under <c>modules</c>.</summary>
    private const string ModuleName = "register.ts";

    /// <summary>The name of the embedded <c>mods/usage/hooks/register.ts</c>; the project item sets it.</summary>
    private const string ModuleResource = "ClaudeDashboard.App.Setup.register.ts";

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

    /// <summary>
    /// The usage mod: <c>mods/usage/hooks/register.ts</c> as this build embedded it, with its
    /// line ends made LF. Read one time.
    /// </summary>
    /// <remarks>
    /// The project links the repository's file as an embedded resource, so there is one copy of
    /// the text, the one that <c>claude plugin validate</c> and <c>claude plugin test</c> check
    /// (ruling R2). A missing file stops the build, so a built assembly always holds it.
    /// </remarks>
    public static string ModuleText { get; } = ReadModule();

    /// <summary>Where the hooks file goes.</summary>
    public static string HooksFile(DashboardPaths paths) =>
        Path.Combine(Folder(paths), HooksFolder, "hooks.json");

    /// <summary>Where the usage mod goes, beside the hooks file that names it.</summary>
    public static string ModuleFile(DashboardPaths paths) =>
        Path.Combine(Folder(paths), HooksFolder, ModuleName);

    /// <summary>Where the mod's one constant, the path of <c>listening.txt</c>, goes.</summary>
    public static string ListeningModuleFile(DashboardPaths paths) =>
        Path.Combine(Folder(paths), HooksFolder, "listening-file.ts");

    /// <summary>
    /// The mod's <c>listening-file.ts</c>: one constant, the absolute path of this data folder's
    /// <c>listening.txt</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The path is a JSON string</strong>, made by <see cref="JsonSerializer"/>. A JSON
    /// string is a correct TypeScript string: each backslash is doubled, and each character
    /// outside ASCII, and the apostrophe, is an escape. So a data folder with any name gives a
    /// module that names it exactly. The lab read <c>listening.txt</c> through such a file at a
    /// path with a space, an apostrophe and an accented letter (the Usage Mod Execution Plan, MOD.2).
    /// </para>
    /// <para>
    /// <strong>Absolute</strong>, for the reason the script is: it then does not matter where
    /// Claude Code runs the plugin from. The token is not in this file; the mod reads it from
    /// <c>listening.txt</c> at every call.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static string ListeningModuleText(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return "export const listeningFile = " + JsonSerializer.Serialize(paths.ListeningFile) + "\n";
    }

    /// <summary>
    /// The plugin's <c>hooks.json</c>: the usage mod under <c>modules</c>, and the dashboard's
    /// handler on every accepted event under <c>hooks</c>.
    /// </summary>
    /// <remarks>
    /// One file holds both keys, and both ran side by side in the lab. A Claude Code that does
    /// not read <c>modules</c> (2.1.241 in the lab) still runs the handlers, so no version check
    /// is needed before this is written (the guide, "Shipping it").
    /// </remarks>
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
                "and does nothing when it is not. The module posts the plan's usage at the end of " +
                "each turn, in the same way.",
            ["modules"] = new JsonArray("./" + ModuleName),
            [HookHandlers.HooksKey] = HookHandlers.ForEveryEvent(interpreter, scriptPath),
        };

        return HookHandlers.Render(file).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    /// Writes the five plugin files when any of them differs from what this build expects.
    /// </summary>
    /// <remarks>
    /// Temporary-then-rename for each, as <see cref="HookScript.EnsureWritten"/> does, so Claude
    /// Code never reads half a file. A failure is logged and reported; the copies already there
    /// are the ones Claude Code goes on using.
    /// </remarks>
    /// <returns>Whether all five files now hold what this build expects.</returns>
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

    /// <summary>Whether all five files hold exactly what this build expects.</summary>
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

    /// <summary>The five files, in the order they are written.</summary>
    /// <remarks>
    /// The two module files come before <c>hooks.json</c>: a session that starts while the files
    /// are written then never finds a <c>hooks.json</c> that names a module not yet on disk.
    /// </remarks>
    private static (string File, string Text)[] Files(DashboardPaths paths) =>
    [
        (MarketplaceFile(paths), MarketplaceText),
        (ManifestFile(paths), ManifestText),
        (ModuleFile(paths), ModuleText),
        (ListeningModuleFile(paths), ListeningModuleText(paths)),
        (HooksFile(paths), HooksText(HookHandlers.Interpreter, paths.HookScriptFile)),
    ];

    private static string ReadModule()
    {
        using var stream = typeof(HookPlugin).Assembly.GetManifestResourceStream(ModuleResource)
            ?? throw new InvalidOperationException($"The assembly holds no resource {ModuleResource}.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
