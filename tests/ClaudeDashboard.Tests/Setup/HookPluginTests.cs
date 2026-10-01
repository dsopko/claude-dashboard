using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The plugin's three files, and the two settings keys that say whether Claude Code has it
/// (issue #30).
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
    /// <strong>The plugin's handler is the settings handler, on the same events.</strong> Compared
    /// against what <c>HookRegistration.Register</c> writes rather than against a shape typed out
    /// here, because the claim is that the two routes run one identical command.
    /// </summary>
    [Fact]
    public void The_hooks_file_carries_the_settings_handler_on_every_accepted_event()
    {
        var script = _paths.HookScriptFile;

        var plugin = (JsonObject)JsonNode.Parse(HookPlugin.HooksText(Interpreter, script))!;
        var settings = new JsonObject();
        HookRegistration.Register(settings, Interpreter, script);

        var hooks = Assert.IsType<JsonObject>(plugin["hooks"]);

        Assert.Equal(
            HookEventNames.Accepted.Order(StringComparer.Ordinal),
            hooks.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(settings["hooks"]!.ToJsonString(), hooks.ToJsonString());
        Assert.Equal(HookEventNames.Accepted.Count, HookRegistration.CountInstalled(plugin, script));
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

    [Fact]
    public void Writing_puts_three_files_in_the_data_folder_and_a_second_write_changes_nothing()
    {
        Assert.False(HookPlugin.Matches(_paths));

        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        Assert.True(HookPlugin.Matches(_paths));
        Assert.StartsWith(_paths.Root, _paths.PluginFolder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HookPlugin.MarketplaceText, File.ReadAllText(HookPlugin.MarketplaceFile(_paths)));
        Assert.Equal(HookPlugin.ManifestText, File.ReadAllText(HookPlugin.ManifestFile(_paths)));
        Assert.Equal(
            HookPlugin.HooksText(HookInstaller.Interpreter, _paths.HookScriptFile),
            File.ReadAllText(HookPlugin.HooksFile(_paths)));

        Assert.True(HookPlugin.EnsureWritten(_paths, _logger));

        // One "Wrote" line, not two: the second call found nothing to do.
        Assert.Single(_sink.Matching("Wrote the Claude Code plugin"));
        Assert.Empty(Directory.EnumerateFiles(_paths.PluginFolder, "*.tmp*", SearchOption.AllDirectories));
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

    // ---- Reading Claude Code's settings ----------------------------------------------------------

    /// <summary>
    /// <strong>The shape Claude Code wrote on 2026-09-30, read back.</strong> Typed out here from
    /// the measurement rather than produced by this code, so a change to either key's name fails
    /// against what Claude Code actually writes.
    /// </summary>
    [Fact]
    public void The_settings_Claude_Code_wrote_read_as_enabled_from_that_folder()
    {
        var settings = HookRegistration.Parse(
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
        Assert.False(HookPlugin.IsEnabled(HookRegistration.Parse(json)));

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
        Assert.Null(HookPlugin.MarketplaceFolder(HookRegistration.Parse(json)));

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
