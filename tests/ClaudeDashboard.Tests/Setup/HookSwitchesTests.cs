using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// <c>--install-hooks</c> and <c>--remove-hooks</c>, against real files on disk and a
/// <c>claude</c> program that starts no process.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The switches register and remove the plugin, and do nothing else (the operator's ruling
/// of 2026-10-01).</strong> They wrote Claude Code's settings file until then. Every test here that
/// has such a file also asserts it is byte for byte what it was, where the fake <c>claude</c>
/// writes nothing — including the tests of failure, because a failure is exactly where a fallback
/// used to write it.
/// </para>
/// <para>
/// <strong>The report is asserted, because it is the remedy.</strong> Where a switch cannot do
/// what was asked there is no other route to take, so what it prints is what the operator has to
/// go on.
/// </para>
/// </remarks>
public sealed class HookSwitchesTests : IDisposable
{
    private const string Commented = "{\n  // mine, and no render keeps this line\n  \"model\": \"opus\"\n}\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;
    private readonly List<string> _report = [];

    public HookSwitchesTests()
    {
        var claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(claudeRoot);

        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);
        _claude = new ClaudeCodePaths(claudeRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HookCheck Check() => new(_claude, _paths, Logger.None);

    /// <summary>A <c>claude</c> that records in the settings what the real one was measured to record.</summary>
    private FakeClaudeCli Recording() => new(_claude);

    private int Run(string requested, FakeClaudeCli cli) =>
        HookSwitches.Run(requested, Check(), _report.Add, new PluginInstaller(cli, _paths, Logger.None));

    private string Report() => string.Join(Environment.NewLine, _report);

    private string SettingsText() =>
        File.Exists(_claude.UserSettingsFile) ? File.ReadAllText(_claude.UserSettingsFile) : string.Empty;

    private JsonObject Settings() => HookHandlers.Parse(SettingsText());

    private void Write(string settings) => File.WriteAllText(_claude.UserSettingsFile, settings);

    private string PluginSettings(string enabled, string? folder = null) =>
        $$"""
        {
          // mine, and no render keeps this line
          "extraKnownMarketplaces": {
            "claude-dashboard": { "source": { "source": "directory", "path": {{JsonSerializer.Serialize(folder ?? _paths.PluginFolder)}} } }
          },
          "enabledPlugins": { "claude-dashboard@claude-dashboard": {{enabled}} }
        }
        """;

    private string OldHookSettings()
    {
        var rendered = HookHandlers.Render(new JsonObject
        {
            ["model"] = "opus",
            [HookHandlers.HooksKey] = HookHandlers.ForEveryEvent(HookHandlers.Interpreter, _paths.HookScriptFile),
        });

        return rendered.Insert(rendered.IndexOf('{') + 1, "\n  // mine, and no render keeps this line");
    }

    // ---- Which switch ---------------------------------------------------------------------------

    [Theory]
    [InlineData("--install-hooks", HookSwitches.Install)]
    [InlineData("--INSTALL-HOOKS", HookSwitches.Install)]
    [InlineData("--remove-hooks", HookSwitches.Remove)]
    [InlineData("--Remove-Hooks", HookSwitches.Remove)]
    public void A_switch_is_recognised_whatever_its_case_and_comes_back_canonical(string typed, string canonical) =>
        Assert.Equal(canonical, HookSwitches.Requested(["first", typed, "last"]));

    [Theory]
    [InlineData("--install-hooks-please")]
    [InlineData("install-hooks")]
    [InlineData("--remove")]
    public void Only_a_whole_argument_is_a_switch(string argument) =>
        Assert.Null(HookSwitches.Requested([argument]));

    [Fact]
    public void With_both_switches_the_first_one_wins()
    {
        Assert.Equal(HookSwitches.Install, HookSwitches.Requested(["--install-hooks", "--remove-hooks"]));
        Assert.Equal(HookSwitches.Remove, HookSwitches.Requested(["--remove-hooks", "--install-hooks"]));
    }

    [Fact]
    public void No_arguments_name_no_switch()
    {
        Assert.Null(HookSwitches.Requested([]));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Requested(null!));
    }

    // ---- --install-hooks ------------------------------------------------------------------------

    [Fact]
    public void Installing_registers_the_plugin_and_says_what_it_did()
    {
        var cli = Recording();

        var code = Run(HookSwitches.Install, cli);

        Assert.Equal(0, code);
        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.Equal(
            [
                $"plugin marketplace add {_paths.PluginFolder}",
                "plugin install claude-dashboard@claude-dashboard",
            ],
            cli.Commands);
        Assert.Contains("claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
        Assert.Contains(_paths.PluginFolder, Report(), StringComparison.Ordinal);
        Assert.Contains(_paths.HookScriptFile, Report(), StringComparison.Ordinal);
        Assert.Contains(
            string.Join(", ", HookEventNames.Accepted.Order(StringComparer.Ordinal)),
            Report(),
            StringComparison.Ordinal);
        Assert.Contains("Restart every Claude Code session", Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Claude Code's settings are Claude Code's to write.</strong> With a <c>claude</c> that
    /// writes nothing, a successful install leaves a hand-formatted file exactly as it was.
    /// </summary>
    [Fact]
    public void Installing_does_not_write_Claude_Codes_settings()
    {
        Write(Commented);

        var code = Run(HookSwitches.Install, new FakeClaudeCli());

        Assert.Equal(0, code);
        Assert.Equal(Commented, SettingsText());
    }

    [Fact]
    public void Installing_twice_succeeds_twice()
    {
        var cli = Recording();

        Assert.Equal(0, Run(HookSwitches.Install, cli));
        Assert.Equal(0, Run(HookSwitches.Install, cli));

        Assert.True(HookPlugin.IsEnabled(Settings()));
    }

    /// <summary>
    /// <strong>No <c>claude</c> program is a failure, and the report is the way out.</strong> The
    /// switch wrote the settings file in this case until the ruling. Now it writes nothing and
    /// prints the two commands, with the folder they need already in place.
    /// </summary>
    [Fact]
    public void Without_the_claude_program_installing_fails_and_gives_the_commands_to_run()
    {
        Write(Commented);

        var code = Run(HookSwitches.Install, new FakeClaudeCli { Found = false });

        Assert.Equal(1, code);
        Assert.Equal(Commented, SettingsText());
        Assert.Contains("claude program was not found", Report(), StringComparison.Ordinal);
        Assert.Contains($"claude plugin marketplace add \"{_paths.PluginFolder}\"", Report(), StringComparison.Ordinal);
        Assert.Contains("claude plugin install claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
        Assert.True(HookPlugin.Matches(_paths), "The report names a folder that holds no plugin.");
    }

    [Fact]
    public void A_refusal_from_Claude_Code_fails_the_install_and_is_printed()
    {
        Write(Commented);
        var cli = new FakeClaudeCli { Answer = _ => new ClaudeCliResult(true, 1, "refused by claude") };

        var code = Run(HookSwitches.Install, cli);

        Assert.Equal(1, code);
        Assert.Equal(Commented, SettingsText());
        Assert.Contains("FAILED", Report(), StringComparison.Ordinal);
        Assert.Contains("refused by claude", Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A claude that records the plugin and then fails has registered it (the issue #30
    /// review, M2).</strong>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_claude_that_records_the_plugin_and_then_fails_still_registered_it(int exitCode)
    {
        var cli = Recording();
        cli.FailAfterRecording = arguments =>
            arguments[1] == "install" ? new ClaudeCliResult(true, exitCode, "failed after the write") : null;

        Assert.Equal(0, Run(HookSwitches.Install, cli));
        Assert.True(HookPlugin.IsEnabled(Settings()));
    }

    /// <summary>
    /// <strong>An old hook is the operator's to remove, and the plugin waits for it.</strong> The
    /// switch moved such a hook to the plugin until the ruling, by writing the settings file. Now
    /// it leaves the file as it is, registers nothing, and says how to remove the hook.
    /// </summary>
    [Fact]
    public void Installing_over_an_old_hook_fails_and_says_how_to_remove_it()
    {
        var settings = OldHookSettings();
        Write(settings);
        var cli = new FakeClaudeCli();

        var code = Run(HookSwitches.Install, cli);

        Assert.Equal(1, code);
        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.Contains("old Claude Dashboard hook", Report(), StringComparison.Ordinal);
        Assert.Contains("remove all hooks for Claude Dashboard from my settings", Report(), StringComparison.Ordinal);
        Assert.Contains("/hooks", Report(), StringComparison.Ordinal);
        Assert.Contains("run --install-hooks again", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_when_another_data_folder_holds_the_plugin_fails_and_names_it()
    {
        var settings = PluginSettings("true", @"C:\Elsewhere\plugin");
        Write(settings);
        var cli = new FakeClaudeCli();

        var code = Run(HookSwitches.Install, cli);

        Assert.Equal(1, code);
        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.Contains(@"C:\Elsewhere\plugin", Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The switch turns a turned-off plugin back on, and says that it does.</strong> A
    /// start never does. The operator who types the switch is asking.
    /// </summary>
    [Fact]
    public void Installing_turns_a_plugin_that_was_off_back_on_and_says_so()
    {
        Write(PluginSettings("false"));

        var code = Run(HookSwitches.Install, Recording());

        Assert.Equal(0, code);
        Assert.True(HookPlugin.IsEnabled(Settings()));
        Assert.Contains("turns it back on", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_fails_either_switch_and_is_left_alone()
    {
        const string Broken = "{ \"hooks\": ";
        Write(Broken);
        var cli = new FakeClaudeCli();

        Assert.Equal(1, Run(HookSwitches.Install, cli));
        Assert.Equal(1, Run(HookSwitches.Remove, cli));

        Assert.Empty(cli.Calls);
        Assert.Equal(Broken, SettingsText());
        Assert.Contains("could not be read", Report(), StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", Report(), StringComparison.Ordinal);
    }

    // ---- --remove-hooks -------------------------------------------------------------------------

    [Fact]
    public void Removing_takes_the_plugin_out_and_names_it()
    {
        var cli = Recording();
        Run(HookSwitches.Install, cli);
        _report.Clear();

        var code = Run(HookSwitches.Remove, cli);

        Assert.Equal(0, code);
        Assert.False(HookPlugin.IsEnabled(Settings()));
        Assert.Null(HookPlugin.MarketplaceFolder(Settings()));
        Assert.Equal(
            ["plugin uninstall claude-dashboard@claude-dashboard", "plugin marketplace remove claude-dashboard"],
            cli.Commands.TakeLast(2));
        Assert.Contains("Removed plugin:    claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
        Assert.Contains(_paths.PluginFolder, Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A plugin that is turned off is still registered, and is removed too.</strong>
    /// Otherwise the switch would report "nothing to remove" over a plugin the next
    /// <c>claude plugin enable</c> brings back.
    /// </summary>
    [Fact]
    public void Removing_takes_out_a_plugin_that_is_turned_off()
    {
        Write(PluginSettings("false"));
        var cli = Recording();

        var code = Run(HookSwitches.Remove, cli);

        Assert.Equal(0, code);
        Assert.Equal(2, cli.Calls.Count);
        Assert.False(HookPlugin.IsDisabled(Settings()));
        Assert.Null(HookPlugin.MarketplaceFolder(Settings()));
    }

    [Fact]
    public void Removing_what_was_never_there_asks_nothing_and_says_so()
    {
        Write(Commented);
        var cli = new FakeClaudeCli();

        var code = Run(HookSwitches.Remove, cli);

        Assert.Equal(0, code);
        Assert.Empty(cli.Calls);
        Assert.Equal(Commented, SettingsText());
        Assert.Contains("Nothing to remove", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_on_a_machine_with_no_settings_file_succeeds_and_creates_none()
    {
        var code = Run(HookSwitches.Remove, new FakeClaudeCli());

        Assert.Equal(0, code);
        Assert.False(File.Exists(_claude.UserSettingsFile));
    }

    /// <summary>
    /// <strong>A plugin that is still there is a failed removal.</strong> Claude Code would go on
    /// running the script, and exit code zero would say it does not.
    /// </summary>
    [Fact]
    public void A_refused_removal_fails_the_switch()
    {
        var cli = Recording();
        Run(HookSwitches.Install, cli);
        _report.Clear();
        cli.Answer = _ => new ClaudeCliResult(true, 1, "refused by claude");

        var code = Run(HookSwitches.Remove, cli);

        Assert.Equal(1, code);
        Assert.Contains("FAILED", Report(), StringComparison.Ordinal);
        Assert.Contains("refused by claude", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_claude_program_removing_fails_and_gives_the_command_to_run()
    {
        var cli = Recording();
        Run(HookSwitches.Install, cli);
        _report.Clear();
        cli.Found = false;

        var code = Run(HookSwitches.Remove, cli);

        Assert.Equal(1, code);
        Assert.Contains("claude plugin uninstall claude-dashboard@claude-dashboard", Report(), StringComparison.Ordinal);
        Assert.True(HookPlugin.IsEnabled(Settings()));
    }

    /// <summary>
    /// <strong>The removal switch does not take an old hook out of the settings file, and says
    /// that it does not.</strong> It did until the ruling. Leaving it silently would leave the
    /// operator believing the dashboard no longer hears anything, while the old hook goes on
    /// delivering every event.
    /// </summary>
    [Fact]
    public void Removing_leaves_an_old_hook_where_it_is_and_says_so()
    {
        var settings = OldHookSettings();
        Write(settings);
        var cli = new FakeClaudeCli();

        var code = Run(HookSwitches.Remove, cli);

        Assert.Equal(0, code);
        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.Contains("This switch does not remove it", Report(), StringComparison.Ordinal);
        Assert.Contains("remove all hooks for Claude Dashboard from my settings", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_leaves_the_script_and_the_plugin_files_on_disk()
    {
        var cli = Recording();
        Run(HookSwitches.Install, cli);

        Run(HookSwitches.Remove, cli);

        Assert.True(HookScript.Matches(_paths));
        Assert.True(HookPlugin.Matches(_paths));
    }

    // ---- The arguments --------------------------------------------------------------------------

    [Fact]
    public void It_needs_its_arguments()
    {
        var plugin = new PluginInstaller(new FakeClaudeCli(), _paths, Logger.None);

        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(null!, Check(), _report.Add, plugin));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(HookSwitches.Install, null!, _report.Add, plugin));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(HookSwitches.Install, Check(), null!, plugin));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(HookSwitches.Install, Check(), _report.Add, null!));
        Assert.Throws<ArgumentException>(() => HookSwitches.Run("--something-else", Check(), _report.Add, plugin));
    }
}
