using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Architecture;
using ClaudeDashboard.Tests.Fakes;
using Serilog;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The plugin's five files, and the two settings keys that say whether Claude Code has it
/// (issue #30). Two of the files are the usage mod (issue #133).
/// </summary>
/// <remarks>
/// The files are what Claude Code runs, so a wrong one fails the way a missing hook fails: the
/// dashboard receives nothing, and that looks like a quiet day. The settings reads decide whether
/// a start installs anything, and they are made at every start from a file the operator may have
/// edited by hand — so a value of the wrong type must read as "no", never throw.
/// </remarks>
public sealed class HookPluginTests : IDisposable
{
    private const string Interpreter = @"C:\Windows\System32\cmd.exe";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly RecordingLogSink _sink = new();
    private readonly Serilog.Core.Logger _logger;

    public HookPluginTests()
    {
        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);

        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    public void Dispose()
    {
        _logger.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- The files -------------------------------------------------------------------------------

    /// <summary>
    /// <strong>One handler on every accepted event, in the exec form.</strong> The shape is typed
    /// out here, field by field, because it is what Claude Code runs: a wrong field fails the way a
    /// missing hook fails, as a dashboard that receives nothing.
    /// </summary>
    [Fact]
    public void The_hooks_file_carries_one_handler_on_every_accepted_event()
    {
        var script = _paths.HookScriptFile;

        var plugin = (JsonObject)JsonNode.Parse(HookPlugin.HooksText(Interpreter, script))!;
        var hooks = Assert.IsType<JsonObject>(plugin["hooks"]);

        Assert.Equal(
            HookEventNames.Accepted.Order(StringComparer.Ordinal),
            hooks.Select(pair => pair.Key).Order(StringComparer.Ordinal));

        foreach (var pair in hooks)
        {
            var group = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(pair.Value)));
            var handler = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(group["hooks"])));

            // No matcher: the dashboard hears every occurrence of the event.
            Assert.Null(group["matcher"]);
            Assert.Equal("command", (string?)handler["type"]);
            Assert.Equal(Interpreter, (string?)handler["command"]);
            Assert.Equal(["/c", script], Assert.IsType<JsonArray>(handler["args"]).Select(argument => (string?)argument));
            Assert.True((bool?)handler["async"]);
        }

        // The same handler the old-hook read recognises, so the build and the read cannot drift.
        Assert.Equal(HookEventNames.Accepted.Count, HookHandlers.CountInSettings(plugin, script));
    }

    /// <summary>
    /// <strong>The script is named by absolute path, and the plugin root is not used.</strong> With
    /// an absolute path it does not matter where Claude Code runs the plugin from, which was
    /// observed and is not promised.
    /// </summary>
    [Fact]
    public void The_hooks_file_names_the_script_absolutely()
    {
        var text = HookPlugin.HooksText(Interpreter, _paths.HookScriptFile);

        Assert.DoesNotContain("CLAUDE_PLUGIN_ROOT", text, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(_paths.HookScriptFile));
        Assert.Contains(
            _paths.HookScriptFile.Replace(@"\", @"\\", StringComparison.Ordinal),
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifests_are_json_and_agree_on_the_name()
    {
        var marketplace = (JsonObject)JsonNode.Parse(HookPlugin.MarketplaceText)!;
        var manifest = (JsonObject)JsonNode.Parse(HookPlugin.ManifestText)!;
        var offered = Assert.Single((JsonArray)marketplace["plugins"]!);

        Assert.Equal(HookPlugin.Name, (string?)marketplace["name"]);
        Assert.Equal(HookPlugin.Name, (string?)offered!["name"]);
        Assert.Equal("./", (string?)offered["source"]);
        Assert.Equal(HookPlugin.Name, (string?)manifest["name"]);
        Assert.Equal("claude-dashboard@claude-dashboard", HookPlugin.Id);
    }

    /// <summary>
    /// <strong>The usage mod is named beside the eight handlers, and the handlers are as they
    /// were</strong> (issue #133). The key order is the guide's: <c>description</c>, then
    /// <c>modules</c>, then <c>hooks</c>. The name under <c>modules</c> is the file the plugin
    /// writes beside <c>hooks.json</c>.
    /// </summary>
    [Fact]
    public void The_hooks_file_names_the_module_beside_the_eight_handlers()
    {
        var plugin = (JsonObject)JsonNode.Parse(HookPlugin.HooksText(Interpreter, _paths.HookScriptFile))!;

        Assert.Equal(["description", "modules", "hooks"], plugin.Select(pair => pair.Key));
        Assert.Equal(["./register.ts"], Assert.IsType<JsonArray>(plugin["modules"]).Select(name => (string?)name));
        Assert.Equal(8, Assert.IsType<JsonObject>(plugin["hooks"]).Count);
        Assert.Equal(HookEventNames.Accepted.Count, HookHandlers.CountInSettings(plugin, _paths.HookScriptFile));

        Assert.Equal(
            Path.GetDirectoryName(HookPlugin.HooksFile(_paths)),
            Path.GetDirectoryName(HookPlugin.ModuleFile(_paths)));
        Assert.Equal("register.ts", Path.GetFileName(HookPlugin.ModuleFile(_paths)));
    }

    [Fact]
    public void Writing_puts_five_files_in_the_data_folder_and_a_second_write_changes_nothing()
    {
        Assert.False(HookPlugin.Matches(_paths));

        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        Assert.True(HookPlugin.Matches(_paths));
        Assert.StartsWith(_paths.Root, _paths.PluginFolder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HookPlugin.MarketplaceText, File.ReadAllText(HookPlugin.MarketplaceFile(_paths)));
        Assert.Equal(HookPlugin.ManifestText, File.ReadAllText(HookPlugin.ManifestFile(_paths)));
        Assert.Equal(HookPlugin.ModuleText, File.ReadAllText(HookPlugin.ModuleFile(_paths)));
        Assert.Equal(HookPlugin.ListeningModuleText(_paths), File.ReadAllText(HookPlugin.ListeningModuleFile(_paths)));
        Assert.Equal(
            HookPlugin.HooksText(HookHandlers.Interpreter, _paths.HookScriptFile),
            File.ReadAllText(HookPlugin.HooksFile(_paths)));
        Assert.Equal(5, Directory.EnumerateFiles(_paths.PluginFolder, "*", SearchOption.AllDirectories).Count());

        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        // One "Wrote" line, not two: the second call found nothing to do.
        Assert.Single(_sink.Matching("Wrote the Claude Code plugin"));
        Assert.Empty(Directory.EnumerateFiles(_paths.PluginFolder, "*.tmp*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// <strong>The plugin carries the repository's <c>register.ts</c>, byte for byte</strong>
    /// (ruling R2): the file that <c>claude plugin validate</c> and <c>claude plugin test</c> check
    /// is the file a session loads. Read from the repository here, not from the build's resource.
    /// </summary>
    [Fact]
    public void The_module_file_is_the_repositorys_register_ts_byte_for_byte()
    {
        var repository = Path.Combine(RepoLayout.Root.FullName, "mods", "usage", "hooks", "register.ts");

        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        Assert.Equal(File.ReadAllBytes(repository), File.ReadAllBytes(HookPlugin.ModuleFile(_paths)));
    }

    /// <summary>
    /// <strong>A <c>hooks.json</c> that names the module is never on disk before the module
    /// is</strong>, for each of the module's two files. A folder stands where the file under test
    /// goes, so its write fails: the files before it are written, and <c>hooks.json</c>, which
    /// comes after both, is not.
    /// </summary>
    [Theory]
    [InlineData("register.ts")]
    [InlineData("listening-file.ts")]
    public void A_hooks_file_that_names_the_module_is_never_written_before_the_module(string moduleFile)
    {
        var blocked = Path.Combine(Path.GetDirectoryName(HookPlugin.HooksFile(_paths))!, moduleFile);
        Assert.Contains(blocked, new[] { HookPlugin.ModuleFile(_paths), HookPlugin.ListeningModuleFile(_paths) });

        Directory.CreateDirectory(blocked);

        Assert.False(HookPlugin.EnsureWritten(_paths, _logger));

        Assert.True(File.Exists(HookPlugin.ManifestFile(_paths)));
        Assert.False(File.Exists(HookPlugin.HooksFile(_paths)));
    }

    [Fact]
    public void A_file_that_was_edited_is_put_back()
    {
        HookPlugin.EnsureWritten(_paths, _logger);
        File.WriteAllText(HookPlugin.HooksFile(_paths), "{}");

        Assert.False(HookPlugin.Matches(_paths));
        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        Assert.True(HookPlugin.Matches(_paths));
    }

    /// <summary>
    /// <strong>Each module file is put back</strong>: half a <c>register.ts</c>, one of the faults
    /// the guide planted, and a <c>listening-file.ts</c> that names a relative path.
    /// </summary>
    [Fact]
    public void A_module_file_that_was_edited_is_put_back()
    {
        HookPlugin.EnsureWritten(_paths, _logger);
        File.WriteAllText(HookPlugin.ModuleFile(_paths), HookPlugin.ModuleText[..(HookPlugin.ModuleText.Length / 2)]);
        File.WriteAllText(HookPlugin.ListeningModuleFile(_paths), "export const listeningFile = '.mod-lab/listening.txt'\n");

        Assert.False(HookPlugin.Matches(_paths));
        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        Assert.Equal(HookPlugin.ModuleText, File.ReadAllText(HookPlugin.ModuleFile(_paths)));
        Assert.Equal(HookPlugin.ListeningModuleText(_paths), File.ReadAllText(HookPlugin.ListeningModuleFile(_paths)));
        Assert.True(HookPlugin.Matches(_paths));
    }

    /// <summary>
    /// <strong>The mod finds <c>listening.txt</c> at any data folder's name.</strong> The folder
    /// here has a space, an apostrophe and a letter outside ASCII. The text after <c>=</c> is a
    /// JSON string, which is a correct TypeScript string, and it reads back as the same absolute
    /// path. Every character is ASCII: the rest are escapes.
    /// </summary>
    [Fact]
    public void The_listening_file_module_names_listening_txt_absolutely()
    {
        var paths = new DashboardPaths(Path.Combine(_root, $"D{(char)0xE9}j{(char)0xE0} vu's data"));
        Directory.CreateDirectory(paths.Root);

        Assert.True(HookPlugin.EnsureWritten(paths, _logger));
        var text = File.ReadAllText(HookPlugin.ListeningModuleFile(paths));

        Assert.Equal(HookPlugin.ListeningModuleText(paths), text);
        Assert.StartsWith("export const listeningFile = \"", text, StringComparison.Ordinal);
        Assert.EndsWith("\"\n", text, StringComparison.Ordinal);
        Assert.All(text, character => Assert.True(character < 128, $"U+{(int)character:X4} is not ASCII."));

        var named = System.Text.Json.JsonSerializer.Deserialize<string>(text[(text.IndexOf('=', StringComparison.Ordinal) + 1)..]);

        Assert.Equal(paths.ListeningFile, named);
        Assert.True(Path.IsPathFullyQualified(named!));
        Assert.Contains(" vu's data", named, StringComparison.Ordinal);
    }

    // ---- Reading Claude Code's settings ----------------------------------------------------------

    /// <summary>
    /// <strong>The shape Claude Code wrote on 2026-09-30, read back.</strong> Typed out here from
    /// the measurement rather than produced by this code, so a change to either key's name fails
    /// against what Claude Code actually writes.
    /// </summary>
    [Fact]
    public void The_settings_Claude_Code_wrote_read_as_enabled_from_that_folder()
    {
        var settings = HookHandlers.Parse(
            """
            {
              "extraKnownMarketplaces": {
                "claude-dashboard": {
                  "source": {
                    "source": "directory",
                    "path": "C:\\Data\\ClaudeDashboard\\plugin"
                  }
                }
              },
              "enabledPlugins": {
                "claude-dashboard@claude-dashboard": true
              }
            }
            """);

        Assert.True(HookPlugin.IsEnabled(settings));
        Assert.Equal(@"C:\Data\ClaudeDashboard\plugin", HookPlugin.MarketplaceFolder(settings));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{ "enabledPlugins": {} }""")]
    [InlineData("""{ "enabledPlugins": { "claude-dashboard@claude-dashboard": false } }""")]
    [InlineData("""{ "enabledPlugins": { "claude-dashboard@claude-dashboard": "true" } }""")]
    [InlineData("""{ "enabledPlugins": { "claude-dashboard@claude-dashboard": 1 } }""")]
    [InlineData("""{ "enabledPlugins": { "claude-dashboard@claude-dashboard": null } }""")]
    [InlineData("""{ "enabledPlugins": { "other@claude-dashboard": true } }""")]
    [InlineData("""{ "enabledPlugins": [ "claude-dashboard@claude-dashboard" ] }""")]
    [InlineData("""{ "enabledPlugins": true }""")]
    public void Anything_but_a_true_under_our_name_is_not_enabled(string json) =>
        Assert.False(HookPlugin.IsEnabled(HookHandlers.Parse(json)));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{ "extraKnownMarketplaces": [] }""")]
    [InlineData("""{ "extraKnownMarketplaces": { "other": { "source": { "path": "C:\\x" } } } }""")]
    [InlineData("""{ "extraKnownMarketplaces": { "claude-dashboard": "C:\\x" } }""")]
    [InlineData("""{ "extraKnownMarketplaces": { "claude-dashboard": { "source": "C:\\x" } } }""")]
    [InlineData("""{ "extraKnownMarketplaces": { "claude-dashboard": { "source": { "source": "github", "repo": "a/b" } } } }""")]
    [InlineData("""{ "extraKnownMarketplaces": { "claude-dashboard": { "source": { "path": 7 } } } }""")]
    [InlineData("""{ "extraKnownMarketplaces": { "claude-dashboard": { "source": { "path": "  " } } } }""")]
    public void A_marketplace_with_no_folder_reads_as_none(string json) =>
        Assert.Null(HookPlugin.MarketplaceFolder(HookHandlers.Parse(json)));

    [Fact]
    public void A_folder_is_ours_as_Windows_compares_paths()
    {
        var ours = _paths.PluginFolder;

        Assert.True(HookPlugin.IsFolderOf(_paths, ours));
        Assert.True(HookPlugin.IsFolderOf(_paths, ours.ToUpperInvariant()));
        Assert.True(HookPlugin.IsFolderOf(_paths, ours + Path.DirectorySeparatorChar));
        Assert.True(HookPlugin.IsFolderOf(_paths, ours.Replace('\\', '/')));

        Assert.False(HookPlugin.IsFolderOf(_paths, _paths.Root));
        Assert.False(HookPlugin.IsFolderOf(_paths, Path.Combine(_root, "other", "plugin")));
        Assert.False(HookPlugin.IsFolderOf(_paths, null));
        Assert.False(HookPlugin.IsFolderOf(_paths, "  "));
        Assert.False(HookPlugin.IsFolderOf(_paths, "C:\\bad\0path"));
    }
}
