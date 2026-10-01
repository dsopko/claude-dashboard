using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Which route a start and a switch take — the plugin or the settings file — against real files
/// on disk and a <c>claude</c> program that starts no process (issue #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim under test is "the dashboard does not write Claude Code's settings"</strong>,
/// and the only honest check of it is a settings file that is byte-for-byte what it was. So the
/// tests that make the claim use a <c>claude</c> fake that writes nothing, against a
/// hand-formatted file whose comment no render would keep.
/// </para>
/// <para>
/// <strong>The other tests go round the loop.</strong> There the fake writes what Claude Code was
/// measured to write, and the next call reads it back through <c>HookInstaller.Check</c> — which
/// is how "a second start asks nothing" and "the removal switch finds the plugin" are held.
/// </para>
/// <para>
/// Every existing test of the start and of the switches passes no plugin, and still runs. Those
/// are now the tests of the fallback.
/// </para>
/// </remarks>
public sealed class PluginRouteTests : IDisposable
{
    private const string Commented = "{\n  // mine, and no render keeps this line\n  \"model\": \"opus\"\n}\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;
    private readonly RecordingLogSink _sink = new();
    private readonly Serilog.Core.Logger _logger;
    private readonly List<string> _report = [];

    public PluginRouteTests()
    {
        var claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(claudeRoot);

        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);
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

    private HookInstaller Installer() => new(_claude, _paths, new FakeClock(), _logger);

    private PluginInstaller Plugin(FakeClaudeCli cli) => new(cli, _paths, _logger);

    /// <summary>A <c>claude</c> that records in the settings what the real one was measured to record.</summary>
    private FakeClaudeCli Recording() => new(_claude);

    private string SettingsText() =>
        File.Exists(_claude.UserSettingsFile) ? File.ReadAllText(_claude.UserSettingsFile) : string.Empty;

    private JsonObject Settings() => HookRegistration.Parse(SettingsText());

    private int InSettings() => HookRegistration.CountInstalled(Settings(), _paths.HookScriptFile);

    private SettingsWriteResult? Start(FakeClaudeCli cli, bool installAtStart = true) =>
        StartupHookInstall.Run(Installer(), installAtStart, SettingsLoadOutcome.Loaded, _logger, Plugin(cli));

    private int Switch(string requested, FakeClaudeCli cli) =>
        HookSwitches.Run(requested, Installer(), _report.Add, Plugin(cli));

    private string Report() => string.Join(Environment.NewLine, _report);

    private void WriteForeignPlugin() => File.WriteAllText(
        _claude.UserSettingsFile,
        """
        {
          "extraKnownMarketplaces": {
            "claude-dashboard": { "source": { "source": "directory", "path": "C:\\Elsewhere\\plugin" } }
          },
          "enabledPlugins": { "claude-dashboard@claude-dashboard": true }
        }
        """);

    // ---- The start ------------------------------------------------------------------------------

    /// <summary>
    /// <strong>The issue in one test.</strong> A machine with no hook is given one, and Claude
    /// Code's settings file is exactly what it was — comment included.
    /// </summary>
    [Fact]
    public void A_machine_with_no_hook_gets_the_plugin_and_the_dashboard_writes_no_settings()
    {
        File.WriteAllText(_claude.UserSettingsFile, Commented);
        var cli = new FakeClaudeCli();

        var result = Start(cli);

        Assert.Null(result);
        Assert.Equal(Commented, SettingsText());
        Assert.Equal(
            [
                $"plugin marketplace add {_paths.PluginFolder}",
                "plugin install claude-dashboard@claude-dashboard",
            ],
            cli.Commands);
        Assert.True(HookScript.Matches(_paths));
        Assert.True(HookPlugin.Matches(_paths));

        // The line says what was done and what it costs: an open session does not see it.
        var line = Assert.Single(_sink.Matching("registered the Claude Code plugin"));
        Assert.Contains("already open", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_Claude_Code_with_no_settings_file_gets_the_plugin_too()
    {
        var cli = new FakeClaudeCli();

        Assert.Null(Start(cli));

        Assert.Equal(2, cli.Calls.Count);
        Assert.False(File.Exists(_claude.UserSettingsFile));
    }

    /// <summary>
    /// <strong>A registered plugin is a hook that is present.</strong> Without that, every start
    /// would install the settings handler beside the plugin and every event would post twice.
    /// </summary>
    [Fact]
    public void A_second_start_asks_nothing_and_writes_nothing()
    {
        var cli = Recording();
        Start(cli);
        var after = SettingsText();

        Assert.Null(Start(cli));

        Assert.Equal(2, cli.Calls.Count);
        Assert.Equal(after, SettingsText());
        Assert.Equal(0, InSettings());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void An_enabled_plugin_means_nothing_is_wanted(int events)
    {
        var presence = new HookPresence(events, 8, [], PluginEnabled: true);

        Assert.False(StartupHookInstall.Wanted(presence, installAtStart: true, SettingsLoadOutcome.Loaded));
    }

    /// <summary>
    /// <strong>The plugin files are kept current at every start.</strong> Claude Code loads the
    /// plugin in place, so these files are the only way a build that changes the event set reaches
    /// a machine that is already registered.
    /// </summary>
    [Fact]
    public void A_start_puts_back_a_plugin_file_that_no_longer_matches()
    {
        var cli = Recording();
        Start(cli);
        File.WriteAllText(HookPlugin.HooksFile(_paths), "{}");

        Start(cli);

        Assert.True(HookPlugin.Matches(_paths));
        Assert.Equal(2, cli.Calls.Count);
    }

    /// <summary>
    /// <strong>An install from before this issue is left on the settings route.</strong> Moving it
    /// would blind every session that is open, because an open session does not see a new plugin.
    /// </summary>
    [Fact]
    public void An_existing_settings_handler_is_left_alone()
    {
        Installer().Install();
        var before = SettingsText();
        var cli = new FakeClaudeCli();

        Assert.Null(Start(cli));

        Assert.Empty(cli.Calls);
        Assert.Equal(before, SettingsText());
    }

    [Fact]
    public void A_partial_settings_handler_is_topped_up_where_it_is()
    {
        Installer().Install();
        var settings = Settings();
        var hooks = (JsonObject)settings[HookRegistration.HooksKey]!;
        hooks.Remove(HookEventNames.Stop);
        hooks.Remove(HookEventNames.SessionEnd);
        File.WriteAllText(_claude.UserSettingsFile, HookRegistration.Render(settings));
        var cli = new FakeClaudeCli();

        var result = Start(cli);

        Assert.Equal(SettingsWriteOutcome.Written, result!.Value.Outcome);
        Assert.Empty(cli.Calls);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
    }

    /// <summary>
    /// <strong>No <c>claude</c> program is not a reason to receive nothing.</strong> The settings
    /// file is written as before, and the log says why.
    /// </summary>
    [Fact]
    public void Without_the_claude_program_a_start_writes_the_settings_file()
    {
        var cli = new FakeClaudeCli { Found = false };

        var result = Start(cli);

        Assert.Equal(SettingsWriteOutcome.Written, result!.Value.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
        Assert.NotEmpty(_sink.Matching("writes the hook into Claude Code's settings instead"));
    }

    [Fact]
    public void A_refusal_from_Claude_Code_also_ends_in_the_settings_file()
    {
        var cli = new FakeClaudeCli { Answer = _ => new ClaudeCliResult(true, 1, "refused") };

        var result = Start(cli);

        Assert.Equal(SettingsWriteOutcome.Written, result!.Value.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
        Assert.Contains(
            _sink.Events,
            entry => entry.Level == LogEventLevel.Warning
                && entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
                    .Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public void An_opted_out_start_asks_nothing()
    {
        File.WriteAllText(_claude.UserSettingsFile, Commented);
        var cli = new FakeClaudeCli();

        Assert.Null(Start(cli, installAtStart: false));

        Assert.Empty(cli.Calls);
        Assert.Equal(Commented, SettingsText());
        Assert.False(Directory.Exists(_paths.PluginFolder));
    }

    [Fact]
    public void An_opted_out_start_with_the_plugin_enabled_does_not_say_the_hook_is_missing()
    {
        var cli = Recording();
        Start(cli);
        _sink.Clear();

        Start(cli, installAtStart: false);

        Assert.Empty(_sink.Matching("nothing was installed"));
        Assert.DoesNotContain(_sink.Events, entry => entry.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public void A_machine_without_Claude_Code_is_asked_nothing_and_given_nothing()
    {
        Directory.Delete(_claude.ConfigDirectory, recursive: true);
        var cli = new FakeClaudeCli();

        Assert.Null(Start(cli));

        Assert.Empty(cli.Calls);
        Assert.False(Directory.Exists(_claude.ConfigDirectory));
        Assert.False(Directory.Exists(_paths.PluginFolder));
    }

    [Fact]
    public void An_unreadable_settings_file_is_neither_written_nor_routed_round()
    {
        File.WriteAllText(_claude.UserSettingsFile, "{ \"hooks\": ");
        var cli = new FakeClaudeCli();

        Assert.Null(Start(cli));

        Assert.Empty(cli.Calls);
        Assert.Equal("{ \"hooks\": ", SettingsText());
    }

    /// <summary>
    /// <strong>Two data folders cannot both register a plugin of one name.</strong> The second one
    /// keeps to the settings file, where two handlers have always been able to sit side by side,
    /// and says which folder holds the name.
    /// </summary>
    [Fact]
    public void A_plugin_that_belongs_to_another_data_folder_keeps_this_one_on_the_settings_file()
    {
        WriteForeignPlugin();
        var cli = new FakeClaudeCli();

        var result = Start(cli);

        Assert.Equal(SettingsWriteOutcome.Written, result!.Value.Outcome);
        Assert.Empty(cli.Calls);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.NotEmpty(_sink.Matching("Elsewhere"));
    }

    // ---- The install switch ---------------------------------------------------------------------

    [Fact]
    public void The_install_switch_registers_the_plugin_and_says_what_it_did()
    {
        var code = Switch(HookSwitches.Install, Recording());

        Assert.Equal(0, code);
        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(0, InSettings());
        Assert.Contains("claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
        Assert.Contains(_paths.PluginFolder, Report(), StringComparison.Ordinal);
        Assert.Contains(_paths.HookScriptFile, Report(), StringComparison.Ordinal);
        Assert.Contains("Restart every Claude Code session", Report(), StringComparison.Ordinal);
        Assert.DoesNotContain("old entr", Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The switch is the migration.</strong> The operator asked, so the old settings
    /// entries go once the plugin is registered — otherwise every event would post twice — and
    /// each one that left the file is printed by name.
    /// </summary>
    [Fact]
    public void The_install_switch_takes_the_old_settings_entries_out()
    {
        Installer().Install();
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());

        var code = Switch(HookSwitches.Install, Recording());

        Assert.Equal(0, code);
        Assert.Equal(0, InSettings());
        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(
            HookEventNames.Accepted.Count,
            _report.Count(line => line.StartsWith("Removed hook:", StringComparison.Ordinal)));
        Assert.Contains("Took 8 old entries", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_switch_run_twice_succeeds_twice()
    {
        var cli = Recording();

        Assert.Equal(0, Switch(HookSwitches.Install, cli));
        Assert.Equal(0, Switch(HookSwitches.Install, cli));

        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(0, InSettings());
    }

    [Fact]
    public void Without_the_claude_program_the_install_switch_writes_the_settings_and_says_why()
    {
        var code = Switch(HookSwitches.Install, new FakeClaudeCli { Found = false });

        Assert.Equal(0, code);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
        Assert.Contains("claude program was not found", Report(), StringComparison.Ordinal);
        Assert.Contains("settings file instead", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_plugin_makes_the_install_switch_write_the_settings_and_print_the_refusal()
    {
        var cli = new FakeClaudeCli { Answer = _ => new ClaudeCliResult(true, 1, "refused by claude") };

        var code = Switch(HookSwitches.Install, cli);

        Assert.Equal(0, code);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
        Assert.Contains("refused by claude", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_switch_uses_the_settings_when_another_data_folder_holds_the_plugin()
    {
        WriteForeignPlugin();
        var cli = new FakeClaudeCli();

        var code = Switch(HookSwitches.Install, cli);

        Assert.Equal(0, code);
        Assert.Empty(cli.Calls);
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());
        Assert.Contains("Elsewhere", Report(), StringComparison.Ordinal);
    }

    // ---- The remove switch ----------------------------------------------------------------------

    [Fact]
    public void The_remove_switch_takes_the_plugin_out_and_names_it()
    {
        var cli = Recording();
        Switch(HookSwitches.Install, cli);
        _report.Clear();

        var code = Switch(HookSwitches.Remove, cli);

        Assert.Equal(0, code);
        Assert.False(HookPlugin.IsEnabled(Settings()));
        Assert.Null(HookPlugin.MarketplaceFolder(Settings()));
        Assert.Equal(
            ["plugin uninstall claude-dashboard@claude-dashboard", "plugin marketplace remove claude-dashboard"],
            cli.Commands.TakeLast(2));
        Assert.Contains("Removed plugin:    claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>One switch leaves nothing of the dashboard's behind, whichever route put it
    /// there.</strong> Both at once is the state a hand-registered plugin beside an old install
    /// produces.
    /// </summary>
    [Fact]
    public void The_remove_switch_takes_out_both_routes_at_once()
    {
        var cli = Recording();
        Start(cli);
        Installer().Install();
        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(HookEventNames.Accepted.Count, InSettings());

        var code = Switch(HookSwitches.Remove, cli);

        // The check the switch made on its way in found both, and said what that costs.
        Assert.NotEmpty(_sink.Matching("posted twice"));
        Assert.Equal(0, code);
        Assert.False(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(0, InSettings());
    }

    [Fact]
    public void The_remove_switch_asks_Claude_Code_nothing_when_no_plugin_is_there()
    {
        Installer().Install();
        var cli = new FakeClaudeCli();

        var code = Switch(HookSwitches.Remove, cli);

        Assert.Equal(0, code);
        Assert.Empty(cli.Calls);
        Assert.Equal(0, InSettings());
    }

    /// <summary>
    /// <strong>A plugin that is still there is a failed removal.</strong> Claude Code would go on
    /// running the script, and exit code zero would say it does not.
    /// </summary>
    [Fact]
    public void A_refused_plugin_removal_fails_the_switch()
    {
        var cli = Recording();
        Switch(HookSwitches.Install, cli);
        _report.Clear();
        cli.Answer = _ => new ClaudeCliResult(true, 1, "refused by claude");

        var code = Switch(HookSwitches.Remove, cli);

        Assert.Equal(1, code);
        Assert.Contains("FAILED", Report(), StringComparison.Ordinal);
        Assert.Contains("refused by claude", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_claude_program_the_remove_switch_fails_and_gives_the_command_to_run()
    {
        var cli = Recording();
        Switch(HookSwitches.Install, cli);
        _report.Clear();
        cli.Found = false;

        var code = Switch(HookSwitches.Remove, cli);

        Assert.Equal(1, code);
        Assert.Contains("claude plugin uninstall claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
        Assert.True(HookPlugin.IsEnabled(Settings()));
    }

    /// <summary>
    /// <strong>The removal still survives the next start.</strong> T1.32's rule, on the new route:
    /// a removal the next start undid would mean nothing.
    /// </summary>
    [Fact]
    public void A_plugin_removal_survives_the_next_start()
    {
        var cli = Recording();
        var store = new SettingsStore(_paths);
        Start(cli);

        var code = Switch(HookSwitches.Remove, cli);
        StartupHookInstall.RecordSwitch(HookSwitches.Remove, code, store, _logger);
        var asked = cli.Calls.Count;

        var loaded = store.Load();
        StartupHookInstall.Run(
            Installer(), loaded.Settings.InstallHooksAtStart, loaded.Outcome, _logger, Plugin(cli));

        Assert.Equal(asked, cli.Calls.Count);
        Assert.False(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(0, InSettings());
    }
}
