using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// What a start does about the dashboard's connection to Claude Code, against real settings files
/// on disk and a <c>claude</c> program that starts no process (issues #39 and #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this covers is a decision, and every branch of it fails silently.</strong> A
/// dashboard that receives no events looks exactly like a quiet day. So each outcome is held
/// three ways: what was asked of Claude Code, what the operator is shown, and that Claude Code's
/// settings file is byte for byte what it was.
/// </para>
/// <para>
/// <strong>The plugin is the only route (the operator's ruling of 2026-10-01).</strong> Until then
/// a start that could not register the plugin wrote the hook into Claude Code's settings instead,
/// and the tests of that are gone with it. Where the plugin cannot be registered now, the
/// assertion is that nothing else was done and the notice gives the remedy.
/// </para>
/// <para>
/// <strong>Two roots, kept apart.</strong> There are two files called <c>settings.json</c> in this
/// system — Claude Code's, which is read, and the dashboard's own, which holds the opt-out. They
/// live in separate temporary folders here, so a confusion between them presents as a failure.
/// </para>
/// </remarks>
public sealed class StartupHookInstallTests : IDisposable
{
    private const string Marker = "MARKER-OUT-OF-THE-SETTINGS-FILE";

    private const string Commented =
        "{\n  // mine, and no render keeps this line\n  \"model\": \"" + Marker + "\"\n}\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;
    private readonly RecordingLogSink _sink = new();
    private readonly Serilog.Core.Logger _logger;
    private readonly HookNotice _notice = new();

    public StartupHookInstallTests()
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

    private HookCheck Check() => new(_claude, _paths, _logger);

    private PluginInstaller Plugin(FakeClaudeCli cli) => new(cli, _paths, _logger);

    private SettingsStore Store() => new(_paths);

    /// <summary>A <c>claude</c> that records in the settings what the real one was measured to record.</summary>
    private FakeClaudeCli Recording() => new(_claude);

    private string SettingsText() =>
        File.Exists(_claude.UserSettingsFile) ? File.ReadAllText(_claude.UserSettingsFile) : string.Empty;

    private HookStartOutcome Start(
        FakeClaudeCli cli,
        bool installAtStart = true,
        SettingsLoadOutcome outcome = SettingsLoadOutcome.Loaded) =>
        StartupHookInstall.Run(Check(), installAtStart, outcome, _logger, Plugin(cli), _notice);

    private string PluginSettings(string enabled, string? folder = null) =>
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

    /// <summary>Settings as a build from before the plugin left them: its handler on every event.</summary>
    private string OldHookSettings(bool withPlugin = false)
    {
        var settings = withPlugin
            ? HookHandlers.Parse(PluginSettings("true"))
            : new JsonObject { ["model"] = Marker };

        settings[HookHandlers.HooksKey] =
            HookHandlers.ForEveryEvent(HookHandlers.Interpreter, _paths.HookScriptFile);

        var rendered = HookHandlers.Render(settings);

        return rendered.Insert(rendered.IndexOf('{') + 1, "\n  // mine, and no render keeps this line");
    }

    private void Write(string settings) => File.WriteAllText(_claude.UserSettingsFile, settings);

    // ---- The decision ---------------------------------------------------------------------------

    /// <summary>
    /// <strong>The order, stated once, so no finding wins only by accident.</strong>
    /// </summary>
    /// <remarks>
    /// Several findings can hold at once and the operator is shown one notice, so the order is the
    /// behaviour. The rows that would go unnoticed are the pairs: an old hook beside an enabled
    /// plugin is still "old hook", because that is the state that posts every event twice; and an
    /// unreadable file beside anything claims nothing else, because nothing else is known.
    /// <strong>Missing is not Unreadable</strong>: a first run has no settings file of the
    /// dashboard's own, the default stands in for nothing, and it must register.
    /// </remarks>
    [Theory]

    // old hooks, problem, Claude Code, enabled, disabled, foreign, opted in, own settings → refusal (null = register)
    [InlineData(0, null, true, false, false, null, true, SettingsLoadOutcome.Loaded, null)]
    [InlineData(0, null, true, false, false, null, true, SettingsLoadOutcome.Missing, null)]
    [InlineData(0, null, true, true, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.Connected)]
    [InlineData(0, null, true, true, false, null, false, SettingsLoadOutcome.Loaded, HookStartOutcome.Connected)]
    [InlineData(0, null, true, true, false, null, true, SettingsLoadOutcome.Unreadable, HookStartOutcome.Connected)]
    [InlineData(8, null, true, false, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.OldHooks)]
    [InlineData(1, null, true, false, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.OldHooks)]
    [InlineData(8, null, true, true, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.OldHooks)]
    [InlineData(8, null, true, false, true, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.OldHooks)]
    [InlineData(8, null, true, false, false, null, false, SettingsLoadOutcome.Loaded, HookStartOutcome.OldHooks)]
    [InlineData(0, null, true, false, true, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.PluginDisabled)]
    [InlineData(0, null, true, false, true, null, false, SettingsLoadOutcome.Loaded, HookStartOutcome.PluginDisabled)]
    [InlineData(0, null, true, false, false, @"C:\Elsewhere\plugin", true, SettingsLoadOutcome.Loaded, HookStartOutcome.OtherDataFolder)]
    [InlineData(0, null, true, false, false, null, true, SettingsLoadOutcome.Unreadable, HookStartOutcome.OptOutUnknown)]
    [InlineData(0, null, true, false, false, null, false, SettingsLoadOutcome.Loaded, HookStartOutcome.Removed)]
    [InlineData(0, "broken", true, false, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.SettingsUnreadable)]
    [InlineData(8, "broken", true, true, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.SettingsUnreadable)]
    [InlineData(0, null, false, false, false, null, true, SettingsLoadOutcome.Missing, HookStartOutcome.NoClaudeCode)]
    [InlineData(8, "broken", false, true, false, null, true, SettingsLoadOutcome.Loaded, HookStartOutcome.NoClaudeCode)]
    public void A_start_registers_only_when_nothing_stands_in_the_way(
        int oldHooks,
        string? problem,
        bool claudeCodeInstalled,
        bool enabled,
        bool disabled,
        string? foreign,
        bool installAtStart,
        SettingsLoadOutcome settingsOutcome,
        HookStartOutcome? refusal)
    {
        var presence = new HookPresence(oldHooks, problem, claudeCodeInstalled, enabled, disabled, foreign);

        Assert.Equal(refusal, StartupHookInstall.Refusal(presence, installAtStart, settingsOutcome));
    }

    // ---- Registering ----------------------------------------------------------------------------

    /// <summary>
    /// <strong>The issue in one test.</strong> A machine with no plugin is given one by Claude
    /// Code, and Claude Code's settings file is exactly what it was — comment included.
    /// </summary>
    [Fact]
    public void A_machine_with_no_plugin_gets_one_and_the_dashboard_writes_no_settings()
    {
        Write(Commented);
        var cli = new FakeClaudeCli();

        var outcome = Start(cli);

        Assert.Equal(HookStartOutcome.Registered, outcome);
        Assert.Equal(Commented, SettingsText());
        Assert.Equal(
            [
                $"plugin marketplace add {_paths.PluginFolder}",
                "plugin install claude-dashboard@claude-dashboard",
            ],
            cli.Commands);
        Assert.True(HookScript.Matches(_paths));
        Assert.True(HookPlugin.Matches(_paths));
    }

    /// <summary>
    /// <strong>A start that registered says what that costs.</strong> A session that is already
    /// open does not see a new plugin — measured — so the window says to restart it, and stops
    /// saying so when a session reports.
    /// </summary>
    [Fact]
    public void A_start_that_registered_tells_the_operator_to_restart_open_sessions()
    {
        Start(new FakeClaudeCli());

        Assert.Equal(HookNoticeKind.JustRegistered, _notice.Kind);
        Assert.Equal(HookNotice.JustRegisteredText, _notice.Text);
        Assert.Null(_notice.TrayText);

        _notice.EventArrived();

        Assert.False(_notice.IsShown);

        var line = Assert.Single(_sink.Matching("this start registered"));
        Assert.Contains("already open", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_Claude_Code_with_no_settings_file_gets_the_plugin_too()
    {
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.Registered, Start(cli, outcome: SettingsLoadOutcome.Missing));

        Assert.Equal(2, cli.Calls.Count);
        Assert.False(File.Exists(_claude.UserSettingsFile));
    }

    /// <summary>
    /// <strong>An enabled plugin is a connection that is there.</strong> A second start asks
    /// nothing, changes nothing and shows nothing.
    /// </summary>
    [Fact]
    public void A_second_start_asks_nothing_and_shows_nothing()
    {
        var cli = Recording();
        Start(cli);
        var after = SettingsText();
        _notice.EventArrived();
        _sink.Clear();

        Assert.Equal(HookStartOutcome.Connected, Start(cli));

        Assert.Equal(2, cli.Calls.Count);
        Assert.Equal(after, SettingsText());
        Assert.False(_notice.IsShown);
        Assert.DoesNotContain(_sink.Events, entry => entry.Level >= LogEventLevel.Warning);
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
    /// <strong>A claude that records the plugin and then fails has registered it (the issue #30
    /// review, M2).</strong> The settings are read again, and the operator is not told that a
    /// connected dashboard is unconnected.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_claude_that_records_the_plugin_and_then_fails_still_registered_it(int exitCode)
    {
        var cli = Recording();
        cli.FailAfterRecording = arguments =>
            arguments[1] == "install" ? new ClaudeCliResult(true, exitCode, "failed after the write") : null;

        Assert.Equal(HookStartOutcome.Registered, Start(cli));
        Assert.Equal(HookNoticeKind.JustRegistered, _notice.Kind);
    }

    // ---- Not registering, and saying why --------------------------------------------------------

    /// <summary>
    /// <strong>No <c>claude</c> program, and no other door.</strong> The settings file is not
    /// written — it never is — and the notice gives the two commands to run by hand, with the
    /// folder they name already written and ready.
    /// </summary>
    [Fact]
    public void Without_the_claude_program_nothing_is_written_and_the_notice_gives_the_commands()
    {
        Write(Commented);
        var cli = new FakeClaudeCli { Found = false };

        var outcome = Start(cli);

        Assert.Equal(HookStartOutcome.ClaudeNotFound, outcome);
        Assert.Equal(Commented, SettingsText());
        Assert.Equal(HookNoticeKind.ClaudeNotFound, _notice.Kind);
        Assert.Contains($"claude plugin marketplace add \"{_paths.PluginFolder}\"", _notice.Text, StringComparison.Ordinal);
        Assert.Contains("claude plugin install claude-dashboard@claude-dashboard", _notice.Text, StringComparison.Ordinal);
        Assert.True(HookPlugin.Matches(_paths), "The notice names a folder that holds no plugin.");
        Assert.Contains(
            _sink.Events,
            entry => entry.Level == LogEventLevel.Warning
                && entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
                    .Contains("claude program was not found", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_from_Claude_Code_is_shown_with_its_reason_and_nothing_is_written()
    {
        Write(Commented);
        var cli = new FakeClaudeCli { Answer = _ => new ClaudeCliResult(true, 1, "refused by claude") };

        var outcome = Start(cli);

        Assert.Equal(HookStartOutcome.ClaudeRefused, outcome);
        Assert.Equal(Commented, SettingsText());
        Assert.Equal(HookNoticeKind.ClaudeRefused, _notice.Kind);
        Assert.Contains("refused by claude", _notice.Text, StringComparison.Ordinal);
        Assert.Contains("claude plugin marketplace add", _notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>An old hook is warned about and left exactly where it is, and the plugin waits for
    /// it to go.</strong> The dashboard does not edit that file, and registering the plugin beside
    /// the old hook would post every event twice.
    /// </summary>
    [Fact]
    public void An_old_hook_in_the_settings_is_warned_about_and_the_plugin_waits()
    {
        var settings = OldHookSettings();
        Write(settings);
        var cli = new FakeClaudeCli();

        var outcome = Start(cli);

        Assert.Equal(HookStartOutcome.OldHooks, outcome);
        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.False(Directory.Exists(_paths.PluginFolder));
        Assert.Equal(HookNoticeKind.OldHooks, _notice.Kind);
        Assert.Equal(HookNotice.OldHooksText, _notice.Text);
    }

    /// <summary>
    /// <strong>Events do not clear the old-hook notice.</strong> They arrive normally in this
    /// state, through the old hook itself, so they prove nothing about it.
    /// </summary>
    [Fact]
    public void The_old_hook_notice_outlives_the_events_that_arrive_through_it()
    {
        Write(OldHookSettings());
        Start(new FakeClaudeCli());

        _notice.EventArrived();
        _notice.EventArrived();

        Assert.Equal(HookNoticeKind.OldHooks, _notice.Kind);
    }

    [Fact]
    public void Once_the_old_hook_is_gone_the_next_start_registers_the_plugin()
    {
        Write(OldHookSettings());
        var cli = new FakeClaudeCli();
        Start(cli);

        Write(Commented);

        Assert.Equal(HookStartOutcome.Registered, Start(cli));
        Assert.Equal(2, cli.Calls.Count);
        Assert.Equal(HookNoticeKind.JustRegistered, _notice.Kind);
    }

    [Fact]
    public void An_old_hook_beside_the_plugin_says_every_event_arrives_twice()
    {
        var settings = OldHookSettings(withPlugin: true);
        Write(settings);
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.OldHooks, Start(cli));

        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.Equal(HookNotice.OldHooksTwiceText, _notice.Text);
        Assert.NotEmpty(_sink.Matching("posted twice"));
    }

    /// <summary>
    /// <strong>A plugin the operator turned off stays off (the ruling of 2026-10-01).</strong>
    /// <c>claude plugin install</c> would turn it back on, so a start does not run it, and says on
    /// screen how to.
    /// </summary>
    [Fact]
    public void A_plugin_the_operator_turned_off_stays_off_and_the_notice_shows()
    {
        var settings = PluginSettings("false");
        Write(settings);
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.PluginDisabled, Start(cli));

        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.Equal(HookNotice.PluginDisabledText, _notice.Text);
        Assert.Equal(HookNotice.PluginDisabledShort, _notice.TrayText);
    }

    [Fact]
    public void An_opted_out_start_asks_nothing_and_says_how_to_reconnect()
    {
        Write(Commented);
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.Removed, Start(cli, installAtStart: false));

        Assert.Empty(cli.Calls);
        Assert.Equal(Commented, SettingsText());
        Assert.False(Directory.Exists(_paths.PluginFolder));
        Assert.Equal(HookNotice.PluginRemovedText, _notice.Text);
    }

    [Fact]
    public void An_unknown_opt_out_is_not_consent()
    {
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.OptOutUnknown, Start(cli, outcome: SettingsLoadOutcome.Unreadable));

        Assert.Empty(cli.Calls);
        Assert.Equal(HookNotice.OptOutUnknownText, _notice.Text);
        Assert.NotEmpty(_sink.Matching("opt-out is unknown"));
    }

    /// <summary>
    /// <strong>No Claude Code: nothing asked, nothing created, and the window says so (T1.33, and
    /// the ruling of 2026-10-01).</strong> It was one quiet log line until then.
    /// </summary>
    [Fact]
    public void A_machine_without_Claude_Code_is_told_so_and_given_nothing()
    {
        Directory.Delete(_claude.ConfigDirectory, recursive: true);
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.NoClaudeCode, Start(cli, outcome: SettingsLoadOutcome.Missing));

        Assert.Empty(cli.Calls);
        Assert.False(Directory.Exists(_claude.ConfigDirectory));
        Assert.False(Directory.Exists(_paths.PluginFolder));
        Assert.Equal(HookNotice.ClaudeCodeNotInstalledText, _notice.Text);
        Assert.Equal(HookNotice.NoClaudeCodeShort, _notice.TrayText);

        // Information, not a warning: a machine without Claude Code is an ordinary machine.
        Assert.DoesNotContain(_sink.Events, entry => entry.Level >= LogEventLevel.Warning);
        Assert.Contains(_claude.ConfigDirectory, Assert.Single(_sink.Matching("No Claude Code install was detected")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_file_that_will_not_read_is_left_alone_and_nothing_is_asked()
    {
        const string Broken = "{ \"model\": \"" + Marker + "\", ";
        Write(Broken);
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.SettingsUnreadable, Start(cli));

        Assert.Empty(cli.Calls);
        Assert.Equal(Broken, SettingsText());
        Assert.Equal(HookNoticeKind.SettingsUnreadable, _notice.Kind);
        Assert.Contains("changed nothing", _notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Two data folders cannot both register a plugin of one name.</strong> The second one
    /// is not connected, and says which folder holds the name.
    /// </summary>
    [Fact]
    public void A_plugin_that_belongs_to_another_data_folder_leaves_this_one_unconnected()
    {
        var settings = PluginSettings("true", @"C:\Elsewhere\plugin");
        Write(settings);
        var cli = new FakeClaudeCli();

        Assert.Equal(HookStartOutcome.OtherDataFolder, Start(cli));

        Assert.Empty(cli.Calls);
        Assert.Equal(settings, SettingsText());
        Assert.Contains(@"C:\Elsewhere\plugin", _notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Nothing out of Claude Code's settings reaches the log or the notice</strong>
    /// (T1.24). The file is the operator's, and one of their hooks may carry their prompt text.
    /// </summary>
    [Fact]
    public void Nothing_out_of_the_settings_file_reaches_the_log_or_the_notice()
    {
        foreach (var settings in new[] { Commented, OldHookSettings(), OldHookSettings(withPlugin: true), PluginSettings("false"), PluginSettings("true") })
        {
            Write(settings);
            Start(new FakeClaudeCli());

            Assert.DoesNotContain(Marker, _notice.Text ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.NotEmpty(_sink.Messages);
        Assert.Empty(_sink.Matching(Marker));
    }

    [Fact]
    public void It_needs_its_arguments()
    {
        var plugin = Plugin(new FakeClaudeCli());

        Assert.Throws<ArgumentNullException>(() => StartupHookInstall.Run(null!, true, SettingsLoadOutcome.Loaded, _logger, plugin, _notice));
        Assert.Throws<ArgumentNullException>(() => StartupHookInstall.Run(Check(), true, SettingsLoadOutcome.Loaded, null!, plugin, _notice));
        Assert.Throws<ArgumentNullException>(() => StartupHookInstall.Run(Check(), true, SettingsLoadOutcome.Loaded, _logger, null!, _notice));
        Assert.Throws<ArgumentNullException>(() => StartupHookInstall.Run(Check(), true, SettingsLoadOutcome.Loaded, _logger, plugin, null!));
    }

    // ---- The switches, and what survives a restart ----------------------------------------------

    /// <summary>
    /// <strong><c>--remove-hooks</c>, then a start, leaves the plugin removed.</strong>
    /// </summary>
    /// <remarks>
    /// The whole reason the flag is part of the feature rather than a nicety: an operator who
    /// removes the plugin and finds it back has been overruled by the application, and a supported
    /// switch has become a no-op with extra steps.
    /// </remarks>
    [Fact]
    public void A_removal_survives_the_next_start()
    {
        var cli = Recording();
        Start(cli);

        var told = new List<string>();
        var code = HookSwitches.Run(HookSwitches.Remove, Check(), _ => { }, Plugin(cli));
        StartupHookInstall.RecordSwitch(HookSwitches.Remove, code, Store(), _logger, told.Add);

        Assert.Equal(0, code);
        Assert.False(Store().Load().Settings.InstallHooksAtStart);

        // The consequence reaches the report the operator is reading, not only the log.
        var consequence = Assert.Single(told);
        Assert.Contains("no longer register", consequence, StringComparison.Ordinal);
        Assert.Contains(HookSwitches.Install, consequence, StringComparison.Ordinal);

        var asked = cli.Calls.Count;
        var next = Store().Load();

        var outcome = StartupHookInstall.Run(
            Check(), next.Settings.InstallHooksAtStart, next.Outcome, _logger, Plugin(cli), _notice);

        Assert.Equal(HookStartOutcome.Removed, outcome);
        Assert.Equal(asked, cli.Calls.Count);
        Assert.False(HookPlugin.IsEnabled(HookHandlers.Parse(SettingsText())));
    }

    /// <summary>
    /// <strong>A corrupt own settings file does not override a recorded removal (review of
    /// T1.32).</strong>
    /// </summary>
    /// <remarks>
    /// <c>SettingsStore.Load</c> hands back defaults for a file that would not read, the default
    /// says install, and the recorded <c>--remove-hooks</c> is in exactly the file that could not
    /// be read. The corruption is written over a settings file that genuinely recorded the opt-out
    /// first, because that is the sequence the rule exists for.
    /// </remarks>
    [Fact]
    public void A_corrupt_own_settings_file_does_not_override_a_recorded_removal()
    {
        var cli = Recording();
        Start(cli);
        StartupHookInstall.RecordSwitch(
            HookSwitches.Remove,
            HookSwitches.Run(HookSwitches.Remove, Check(), _ => { }, Plugin(cli)),
            Store(),
            _logger);

        File.WriteAllText(_paths.SettingsFile, """{ "installHooksAtStart": """);

        var loaded = Store().Load();

        Assert.Equal(SettingsLoadOutcome.Unreadable, loaded.Outcome);
        Assert.True(
            loaded.Settings.InstallHooksAtStart,
            "The default must say install, or this test is not exercising the override.");

        var asked = cli.Calls.Count;

        var outcome = StartupHookInstall.Run(
            Check(), loaded.Settings.InstallHooksAtStart, loaded.Outcome, _logger, Plugin(cli), _notice);

        Assert.Equal(HookStartOutcome.OptOutUnknown, outcome);
        Assert.Equal(asked, cli.Calls.Count);
        Assert.False(HookPlugin.IsEnabled(HookHandlers.Parse(SettingsText())));
    }

    /// <summary>
    /// <strong><c>--install-hooks</c> after that restores the plugin and the flag together.</strong>
    /// </summary>
    [Fact]
    public void An_install_switch_restores_the_plugin_and_the_flag()
    {
        var cli = Recording();
        Start(cli);
        StartupHookInstall.RecordSwitch(
            HookSwitches.Remove,
            HookSwitches.Run(HookSwitches.Remove, Check(), _ => { }, Plugin(cli)),
            Store(),
            _logger);

        var told = new List<string>();
        var code = HookSwitches.Run(HookSwitches.Install, Check(), _ => { }, Plugin(cli));
        var recorded = StartupHookInstall.RecordSwitch(HookSwitches.Install, code, Store(), _logger, told.Add);

        Assert.Equal(0, code);
        Assert.True(recorded);
        Assert.True(Store().Load().Settings.InstallHooksAtStart);
        Assert.True(HookPlugin.IsEnabled(HookHandlers.Parse(SettingsText())));
        Assert.Contains(told, line => line.Contains("register the plugin again", StringComparison.Ordinal));
    }

    /// <summary>
    /// <strong>A switch that failed decides nothing.</strong>
    /// </summary>
    [Fact]
    public void A_failed_switch_records_nothing()
    {
        Assert.False(StartupHookInstall.RecordSwitch(HookSwitches.Remove, 1, Store(), _logger));
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    /// <summary>
    /// <strong>An install switch on a machine with no settings file of the dashboard's writes
    /// none.</strong> The flag would be <c>true</c>, which is what an absent file already means.
    /// </summary>
    [Fact]
    public void An_install_switch_writes_no_settings_file_to_record_the_default()
    {
        Assert.False(StartupHookInstall.RecordSwitch(HookSwitches.Install, 0, Store(), _logger));
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    /// <summary>
    /// <strong>The dashboard's own settings are not written back from a partial parse.</strong>
    /// </summary>
    /// <remarks>
    /// <see cref="SettingsStore.Load"/> hands back defaults for a file that would not read — right
    /// for deciding what to run with, and destructive if saved, because the save would replace
    /// whatever the operator wrote with a fresh object.
    /// </remarks>
    [Fact]
    public void An_unreadable_dashboard_settings_file_is_left_exactly_as_it_is()
    {
        const string Theirs = "{ \"port\": ";
        File.WriteAllText(_paths.SettingsFile, Theirs);

        Assert.False(StartupHookInstall.RecordSwitch(HookSwitches.Remove, 0, Store(), _logger));
        Assert.Equal(Theirs, File.ReadAllText(_paths.SettingsFile));
        Assert.NotEmpty(_sink.Matching("could not record"));
    }

    /// <summary>Recording the flag keeps everything else in the file.</summary>
    [Fact]
    public void Recording_the_flag_keeps_the_rest_of_the_settings()
    {
        Store().Save(new DashboardSettings { Port = 51000 });

        StartupHookInstall.RecordSwitch(HookSwitches.Remove, 0, Store(), _logger);

        var settings = Store().Load().Settings;

        Assert.Equal(51000, settings.Port);
        Assert.False(settings.InstallHooksAtStart);
    }

    /// <summary>The flag survives a round trip through the real file.</summary>
    [Fact]
    public void The_flag_round_trips()
    {
        Store().Save(new DashboardSettings { InstallHooksAtStart = false });

        Assert.False(Store().Load().Settings.InstallHooksAtStart);
        Assert.Contains("installHooksAtStart", File.ReadAllText(_paths.SettingsFile), StringComparison.Ordinal);
    }

    /// <summary>An absent key is the default, which is on.</summary>
    /// <remarks>
    /// Every operator who has never opened the file is here, and they are the people issue #39 is
    /// about. A default of off would keep the defect for all of them.
    /// </remarks>
    [Fact]
    public void An_absent_key_registers_at_start()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "port": 51000 }""");

        Assert.True(Store().Load().Settings.InstallHooksAtStart);
    }
}
