using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The read of Claude Code's settings, against real files on disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two roots, kept apart.</strong> There are two files called <c>settings.json</c> in this
/// system — Claude Code's, which this reads, and the dashboard's own. Reaching for the wrong one
/// throws nothing, so they live in separate temporary folders here and a confusion between them
/// presents as a failure.
/// </para>
/// <para>
/// <strong>Every test also holds that the file was not written.</strong> The text after the check
/// is the text before it, byte for byte, for a file with a comment in it that no render would
/// keep. The check has no call that could write (<c>ClaudeSettingsReadOnlyGuardTests</c> holds
/// that); this holds the outcome.
/// </para>
/// </remarks>
public sealed class HookCheckTests : IDisposable
{
    private const string Marker = "MARKER-OUT-OF-THE-SETTINGS-FILE";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;
    private readonly RecordingLogSink _sink = new();
    private readonly Serilog.Core.Logger _logger;

    public HookCheckTests()
    {
        var claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(claudeRoot);

        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        _claude = new ClaudeCodePaths(claudeRoot);

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

    private HookCheck Check() => new(_claude, _paths, _logger);

    /// <summary>Writes the settings, checks, and asserts the file is byte for byte what was written.</summary>
    private HookPresence CheckOf(string settings)
    {
        File.WriteAllText(_claude.UserSettingsFile, settings);

        var presence = Check().Check();

        Assert.Equal(settings, File.ReadAllText(_claude.UserSettingsFile));

        return presence;
    }

    private string Plugin(string enabled, string? folder = null) =>
        $$"""
        {
          // mine, and no render keeps this line
          "model": "{{Marker}}",
          "extraKnownMarketplaces": {
            "claude-dashboard": { "source": { "source": "directory", "path": {{JsonSerializer.Serialize(folder ?? _paths.PluginFolder)}} } }
          },
          "enabledPlugins": { "claude-dashboard@claude-dashboard": {{enabled}} }
        }
        """;

    private string OldHooks(string? script = null)
    {
        var rendered = HookHandlers.Render(new JsonObject
        {
            ["model"] = Marker,
            [HookHandlers.HooksKey] = HookHandlers.ForEveryEvent(HookHandlers.Interpreter, script ?? _paths.HookScriptFile),
        });

        return rendered.Insert(rendered.IndexOf('{') + 1, "\n  // mine, and no render keeps this line");
    }

    [Fact]
    public void A_machine_with_no_Claude_Code_directory_has_no_Claude_Code()
    {
        Directory.Delete(_claude.ConfigDirectory);

        var presence = Check().Check();

        Assert.False(presence.ClaudeCodeInstalled);
        Assert.Equal(new HookPresence(0, ClaudeCodeInstalled: false), presence);
        Assert.False(Directory.Exists(_claude.ConfigDirectory));
    }

    [Fact]
    public void A_fresh_Claude_Code_with_no_settings_file_has_nothing_of_ours()
    {
        var presence = Check().Check();

        Assert.Equal(new HookPresence(0), presence);
        Assert.False(File.Exists(_claude.UserSettingsFile));
    }

    [Theory]
    [InlineData("{ \"hooks\": ")]
    [InlineData("""{ "hooks": { "Stop": [], "Stop": [] } }""")]
    [InlineData("[]")]
    public void A_file_that_will_not_read_is_a_problem_and_nothing_else_is_claimed(string text)
    {
        var presence = CheckOf(text);

        Assert.NotNull(presence.Problem);
        Assert.Equal(new HookPresence(0, Problem: presence.Problem), presence);
        Assert.NotEmpty(_sink.Matching("Could not read Claude Code's settings"));
    }

    [Fact]
    public void An_enabled_plugin_from_this_data_folder_is_ours()
    {
        var presence = CheckOf(Plugin("true"));

        Assert.Equal(new HookPresence(0, PluginEnabled: true), presence);
    }

    [Fact]
    public void A_plugin_set_to_false_is_turned_off_not_missing()
    {
        var presence = CheckOf(Plugin("false"));

        Assert.Equal(new HookPresence(0, PluginDisabled: true), presence);
    }

    /// <summary>
    /// <strong>"Ours" is the folder, never the name.</strong> A second data folder registers a
    /// plugin of the same name from its own folder, and treating that as ours would leave this
    /// dashboard believing it is connected while every event goes to the other one.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void A_plugin_of_our_name_from_another_folder_is_somebody_elses(string enabled)
    {
        var presence = CheckOf(Plugin(enabled, @"C:\Elsewhere\plugin"));

        Assert.Equal(new HookPresence(0, ForeignPlugin: @"C:\Elsewhere\plugin"), presence);
    }

    [Fact]
    public void An_enabled_flag_with_no_marketplace_folder_is_not_ours()
    {
        var presence = CheckOf("""{ "enabledPlugins": { "claude-dashboard@claude-dashboard": true } }""");

        Assert.Equal(new HookPresence(0), presence);
    }

    [Fact]
    public void A_hook_from_before_the_plugin_is_found_and_counted()
    {
        var presence = CheckOf(OldHooks());

        Assert.Equal(new HookPresence(OldHookEvents: HookEventNames.Accepted.Count), presence);
        Assert.NotEmpty(_sink.Matching("still carry a hook"));
    }

    [Fact]
    public void Another_data_folders_old_hook_is_not_ours()
    {
        var presence = CheckOf(OldHooks(@"C:\Elsewhere\post-status.cmd"));

        Assert.Equal(0, presence.OldHookEvents);
    }

    /// <summary>
    /// <strong>The log says what was found and nothing out of the file.</strong> The settings are
    /// the operator's, and one of their hooks may carry their prompt text (T1.24).
    /// </summary>
    [Fact]
    public void Nothing_out_of_the_file_reaches_the_log()
    {
        CheckOf(OldHooks());
        CheckOf(Plugin("true"));
        CheckOf(Plugin("false"));
        CheckOf("{ \"model\": \"" + Marker + "\", ");

        Assert.NotEmpty(_sink.Messages);
        Assert.Empty(_sink.Matching(Marker));
    }

    [Fact]
    public void It_needs_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new HookCheck(null!, _paths, _logger));
        Assert.Throws<ArgumentNullException>(() => new HookCheck(_claude, null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new HookCheck(_claude, _paths, null!));
    }
}
