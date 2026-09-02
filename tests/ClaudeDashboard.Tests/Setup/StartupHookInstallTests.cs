using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The start-time hook repair, against real settings files on disk (issue #39).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this covers is a decision, and every branch of it fails silently.</strong> The
/// dashboard receiving no events looks exactly like a quiet day; the dashboard rewriting a complete
/// handler looks like nothing at all until the operator opens their settings file and finds their
/// comments gone. Neither shows up as an exception, a red row, or a failing test anywhere else.
/// </para>
/// <para>
/// <strong>Two roots, as <c>HookInstallerTests</c> has.</strong> There are two files called
/// <c>settings.json</c> in this system — Claude Code's, which holds the hooks, and the dashboard's,
/// which holds the opt-out — and T1.32 is the first task that writes both. Reaching for the wrong
/// one throws nothing and fails nothing, so they live in separate temporary folders here and a
/// confusion between them presents as a failure.
/// </para>
/// </remarks>
public sealed class StartupHookInstallTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;
    private readonly string _claudeRoot;
    private readonly RecordingLogSink _sink = new();
    private readonly Serilog.Core.Logger _logger;

    public StartupHookInstallTests()
    {
        _claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(_claudeRoot);

        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);
        _claude = new ClaudeCodePaths(_claudeRoot);

        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
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

    // Logging into the recording sink, because two of the claims here are about what a start says:
    // that a file it could not read is warned about, and that no part of that file reaches the
    // line. An installer wired to Logger.None would satisfy both by writing nothing at all.
    private HookInstaller Installer() => new(_claude, _paths, new FakeClock(), _logger);

    private SettingsStore Store() => new(_paths);

    private string SettingsText() =>
        File.Exists(_claude.UserSettingsFile) ? File.ReadAllText(_claude.UserSettingsFile) : string.Empty;

    private int Installed() =>
        HookRegistration.CountInstalled(HookRegistration.Parse(SettingsText()), _paths.HookScriptFile);

    /// <summary>
    /// A complete install, then hand-formatted: a comment inside the object the merge cannot keep.
    /// </summary>
    /// <remarks>
    /// The comment is the assertion in every test that uses this. <c>JsonNode</c> carries no
    /// comment, so any file this dashboard renders loses it — which means a test written against
    /// already-rendered JSON would pass against a start that rewrote the file, and would prove
    /// nothing.
    /// </remarks>
    private string WriteCompleteAndCommented()
    {
        Installer().Install();

        var rendered = SettingsText();
        var commented = rendered.Insert(
            rendered.IndexOf('{') + 1,
            "\n  // the model I use — and this comment is the assertion\n  \"model\": \"opus\",");

        File.WriteAllText(_claude.UserSettingsFile, commented);

        return commented;
    }

    // ---- The decision -------------------------------------------------------------------------------

    /// <summary>
    /// <strong>The truth table, stated once, so no branch is reached only by accident.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row that would go unnoticed among the problems is the "a count means nothing" one: a
    /// file that would not parse is <em>also</em> incomplete, so a rule written as "install when
    /// incomplete" installs over a broken file and looks correct doing it.
    /// </para>
    /// <para>
    /// <strong>The outcome column was added by the review, and the row it protects is the
    /// Unreadable one.</strong> An unreadable dashboard settings file hands back defaults, and the
    /// default says install — but a recorded <c>--remove-hooks</c> lives in exactly the file that
    /// could not be read, so the default is standing in for an answer that may be the opposite.
    /// Unknown is not consent. <strong>Missing is not Unreadable</strong>: a first run has no file,
    /// the default stands in for nothing, and it must install — that distinction is the difference
    /// between fixing issue #39 and re-opening it for every new user.
    /// </para>
    /// </remarks>
    [Theory]

    // events, expected, problem, opted in, own-settings outcome, Claude Code installed → wanted
    [InlineData(0, 8, null, true, SettingsLoadOutcome.Loaded, true, true)]        // absent
    [InlineData(5, 8, null, true, SettingsLoadOutcome.Loaded, true, true)]        // partial
    [InlineData(8, 8, null, true, SettingsLoadOutcome.Loaded, true, false)]       // complete
    [InlineData(0, 8, null, false, SettingsLoadOutcome.Loaded, true, false)]      // absent, opted out
    [InlineData(5, 8, null, false, SettingsLoadOutcome.Loaded, true, false)]      // partial, opted out
    [InlineData(0, 8, "broken", true, SettingsLoadOutcome.Loaded, true, false)]   // unreadable or malformed
    [InlineData(8, 8, "broken", true, SettingsLoadOutcome.Loaded, true, false)]   // a count means nothing beside a problem
    [InlineData(0, 8, null, true, SettingsLoadOutcome.Missing, true, true)]       // first run: no file, the default is real
    [InlineData(0, 8, null, true, SettingsLoadOutcome.Unreadable, true, false)]   // the opt-out is unknown, not consented
    [InlineData(5, 8, null, true, SettingsLoadOutcome.Unreadable, true, false)]   // partial changes nothing about unknown
    [InlineData(8, 8, null, true, SettingsLoadOutcome.Unreadable, true, false)]   // refused twice over
    [InlineData(0, 8, null, true, SettingsLoadOutcome.Missing, false, false)]     // no Claude Code: the PKG.4 machine
    [InlineData(0, 8, null, true, SettingsLoadOutcome.Loaded, false, false)]      // no Claude Code outranks everything
    public void The_decision_installs_only_what_is_missing_from_a_file_that_read(
        int events,
        int expected,
        string? problem,
        bool installAtStart,
        SettingsLoadOutcome settingsOutcome,
        bool claudeCodeInstalled,
        bool wanted)
    {
        var presence = new HookPresence(events, expected, [], problem, claudeCodeInstalled);

        Assert.Equal(wanted, StartupHookInstall.Wanted(presence, installAtStart, settingsOutcome));
    }

    /// <summary>
    /// <strong>A foreign install changes nothing about the decision, and one test pins it.</strong>
    /// </summary>
    /// <remarks>
    /// <see cref="HookPresence.Foreign"/> names a handler installed under another data folder — a
    /// real configuration, not a corruption, and <c>Check</c> warns about it with both paths. The
    /// decision deliberately ignores it: our handler being missing is our handler being missing,
    /// whoever else is installed. Right, but previously pinned by nothing — the truth table cannot
    /// carry a list in an attribute, so the claim lives here.
    /// </remarks>
    [Fact]
    public void A_foreign_install_does_not_change_the_decision()
    {
        var foreign = new HookPresence(0, 8, [@"C:\elsewhere\data\post-status.cmd"]);
        var foreignAndComplete = new HookPresence(8, 8, [@"C:\elsewhere\data\post-status.cmd"]);

        Assert.True(StartupHookInstall.Wanted(foreign, installAtStart: true, SettingsLoadOutcome.Loaded));
        Assert.False(StartupHookInstall.Wanted(foreignAndComplete, installAtStart: true, SettingsLoadOutcome.Loaded));
    }

    // ---- Installing ---------------------------------------------------------------------------------

    /// <summary>An absent handler is installed at start, on every accepted event.</summary>
    /// <remarks>
    /// The whole of issue #39: a user who has never opened a terminal starts the exe and receives
    /// events. Before T1.32 this file did not exist and they received nothing, for ever. The load
    /// outcome is <see cref="SettingsLoadOutcome.Missing"/> on purpose — that user has no dashboard
    /// settings file either, so this is also the pin on "Missing is not Unreadable": treat the two
    /// alike and the first run this issue is about installs nothing.
    /// </remarks>
    [Fact]
    public void An_absent_handler_is_installed_at_start()
    {
        var result = StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Missing, _logger);

        Assert.Equal(SettingsWriteOutcome.Written, result?.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, Installed());
        Assert.True(File.Exists(_paths.HookScriptFile), "The script must be written before the handler names it.");
    }

    /// <summary>
    /// <strong>A handler on some of the events is topped up to all of them.</strong>
    /// </summary>
    /// <remarks>
    /// Three things produce this and installing the missing ones is right for all three: an
    /// interrupted write, a hand edit, and a build that added an event to
    /// <see cref="HookEventNames.Accepted"/>. A rule that only installed at zero would never reach
    /// an install that already existed, so the third would go unfixed for every existing user.
    /// </remarks>
    [Fact]
    public void A_partial_handler_is_topped_up()
    {
        Installer().Install();

        var settings = HookRegistration.Parse(SettingsText());
        ((JsonObject)settings["hooks"]!).Remove(HookEventNames.Stop);
        File.WriteAllText(_claude.UserSettingsFile, HookRegistration.Render(settings));

        var result = StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Loaded, _logger);

        Assert.Equal(SettingsWriteOutcome.Written, result?.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, Installed());
    }

    /// <summary>
    /// <strong>A complete handler leaves the operator's file byte for byte, and the bytes are the
    /// claim.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not the reported outcome.</strong> <c>SettingsFileWriter</c> would report
    /// <c>NothingToDo</c> for a merge that changed no <em>content</em> — and would still have
    /// rewritten this file, because it compares the text it read against the text it renders and
    /// rendering from <c>JsonNode</c> preserves neither the comment nor the spacing. So a test
    /// asserting the outcome passes against exactly the defect it is meant to catch.
    /// </para>
    /// <para>
    /// The short-circuit is <see cref="StartupHookInstall.Wanted"/> refusing before the merge, the
    /// way <c>HookInstaller.Remove</c>'s own guard does.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_complete_handler_leaves_the_file_byte_for_byte()
    {
        var theirs = WriteCompleteAndCommented();
        var before = File.GetLastWriteTimeUtc(_claude.UserSettingsFile);

        var result = StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Loaded, _logger);

        Assert.Null(result);
        Assert.Equal(theirs, SettingsText());
        Assert.Equal(before, File.GetLastWriteTimeUtc(_claude.UserSettingsFile));
        Assert.Contains("this comment is the assertion", SettingsText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A hand-formatted file keeps its comments across a start that the opt-out silences.</strong>
    /// </summary>
    /// <remarks>
    /// A different path to the same promise: here the merge is refused by
    /// <see cref="DashboardSettings.InstallHooksAtStart"/> rather than by the handler being complete,
    /// and the file is incomplete, so a rule that keyed only on completeness would write over it.
    /// </remarks>
    [Fact]
    public void An_opted_out_start_leaves_a_hand_formatted_file_alone()
    {
        const string Theirs = """
            {
              // the model I use — and this comment is the assertion
              "model": "opus",
                  "cleanupPeriodDays": 30,
            }
            """;
        File.WriteAllText(_claude.UserSettingsFile, Theirs);

        var result = StartupHookInstall.Run(Installer(), installAtStart: false, SettingsLoadOutcome.Loaded, _logger);

        Assert.Null(result);
        Assert.Equal(Theirs, SettingsText());
    }

    /// <summary>The opt-out installs nothing whatever the presence.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_opt_out_installs_nothing(bool somethingIsThere)
    {
        if (somethingIsThere)
        {
            Installer().Install();

            var settings = HookRegistration.Parse(SettingsText());
            ((JsonObject)settings["hooks"]!).Remove(HookEventNames.Stop);
            File.WriteAllText(_claude.UserSettingsFile, HookRegistration.Render(settings));
        }

        var before = SettingsText();

        Assert.Null(StartupHookInstall.Run(Installer(), installAtStart: false, SettingsLoadOutcome.Loaded, _logger));
        Assert.Equal(before, SettingsText());
        Assert.Equal(somethingIsThere, File.Exists(_claude.UserSettingsFile));
    }

    /// <summary>
    /// <strong>A machine without Claude Code gets nothing — no install and no directory
    /// (T1.33, issue #42).</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion is the DIRECTORY'S absence after, not merely that no file exists: the
    /// settings write creates the directory on its way to the file, so a start that installed
    /// would conjure <c>~/.claude</c> into being on a machine that has never had Claude Code —
    /// PKG.4's gate item 7, and the whole of the issue.
    /// </para>
    /// <para>
    /// The line is Information, asserted by level: a machine without Claude Code is an ordinary
    /// machine, not a broken one, and the log must not say otherwise.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_machine_without_Claude_Code_gets_nothing_created()
    {
        Directory.Delete(_claudeRoot);

        var result = StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Missing, _logger);

        Assert.Null(result);
        Assert.False(Directory.Exists(_claudeRoot), "The start must not create Claude Code's directory.");

        var line = Assert.Single(_sink.Matching("Claude Code is not installed"));

        Assert.Contains(_claudeRoot, line, StringComparison.Ordinal);
        Assert.All(
            _sink.Events.Where(entry =>
                RecordingLogSink.Render(entry).Contains("Claude Code is not installed", StringComparison.Ordinal)),
            entry => Assert.Equal(Serilog.Events.LogEventLevel.Information, entry.Level));
    }

    /// <summary>
    /// <strong><c>--install-hooks</c> is not gated: run by hand, it still creates the directory
    /// and installs (T1.33).</strong>
    /// </summary>
    /// <remarks>
    /// An operator typing the switch is asking, and the ask outranks the heuristic — the absent
    /// directory means "Claude Code is not installed" only until the person who knows better
    /// says otherwise. Driven through <see cref="HookSwitches.Run"/>, the path the real switch
    /// takes.
    /// </remarks>
    [Fact]
    public void An_install_switch_still_creates_the_directory()
    {
        Directory.Delete(_claudeRoot);

        var code = HookSwitches.Run(HookSwitches.Install, Installer(), _ => { });

        Assert.Equal(0, code);
        Assert.True(Directory.Exists(_claudeRoot));
        Assert.Equal(HookEventNames.Accepted.Count, Installed());
    }

    // ---- Never a file that would not read -----------------------------------------------------------

    /// <summary>
    /// <strong>A settings file that will not parse is warned about and not written.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The duplicate key is in the set because it is what merging two blocks by hand produces, and
    /// because T1.28 is what made it raise at the parse rather than out of an indexer later —
    /// before that it would have escaped every <c>catch (JsonException)</c> on this path.
    /// </para>
    /// <para>
    /// <strong>Why this outranks the repair.</strong> Writing back from a partial parse costs the
    /// operator every hook, permission and preference in the file. That is a far worse failure than
    /// receiving no events, which is the one being fixed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""{ "hooks": { "Stop": [], "Stop": [] } }""")]
    [InlineData("""{ "model": """)]
    [InlineData("""[ "not-an-object" ]""")]
    public void A_malformed_settings_file_is_left_exactly_as_it_is(string theirs)
    {
        File.WriteAllText(_claude.UserSettingsFile, theirs);

        var result = StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Loaded, _logger);

        Assert.Null(result);
        Assert.Equal(theirs, SettingsText());
        Assert.NotEmpty(_sink.Matching("Could not read Claude Code's settings"));
    }

    /// <summary>
    /// <strong>A settings file that cannot be opened at all is warned about and not written.</strong>
    /// </summary>
    /// <remarks>
    /// Held open with no sharing, which is what a real one looks like: an editor with a lock, a
    /// backup agent, a virus scanner mid-scan. Distinct from malformed because it reaches a
    /// different arm — an <c>IOException</c> out of the read rather than a <c>JsonException</c> out
    /// of the parse — and a guard that covered only the second would write over this one.
    /// </remarks>
    [Fact]
    public void An_unreadable_settings_file_is_left_exactly_as_it_is()
    {
        const string Theirs = """{ "model": "opus" }""";
        File.WriteAllText(_claude.UserSettingsFile, Theirs);

        using (new FileStream(_claude.UserSettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Loaded, _logger));
        }

        Assert.Equal(Theirs, SettingsText());
        Assert.NotEmpty(_sink.Matching("Could not read Claude Code's settings"));
    }

    /// <summary>
    /// <strong>A hand edit that parses is installed over, and the backup is the only place the old
    /// value survives — which is the residual §5k accepts.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Problem is null</c> is the gate, and these three shapes carry no problem: they are valid
    /// JSON that Claude Code's own schema has no room for. Two of them
    /// <see cref="HookRegistration.Register"/> replaces outright — an event whose value is not an
    /// array, and a <c>hooks</c> that is not an object — and it says so in its own remark. The
    /// third is a bad group <em>inside</em> a proper array, which is merged into rather than
    /// replaced.
    /// </para>
    /// <para>
    /// <strong>The residual is that these are now replaced at a start rather than at a switch.</strong>
    /// Before T1.32 it took the operator typing <c>--install-hooks</c>. The gate is deliberately not
    /// narrowed to close it, so the boundary is measured here instead of reasoned about: which
    /// shapes go, which stay, and that a backup exists carrying exactly what was there.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""{ "hooks": { "Stop": "not-an-array" } }""", "not-an-array", false)]
    [InlineData("""{ "hooks": [ "not-an-object" ] }""", "not-an-object", false)]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": "not-an-array" } ] } }""", "not-an-array", true)]
    public void A_parseable_hand_edit_is_installed_over_and_the_backup_carries_it(
        string theirs,
        string marker,
        bool survivesInPlace)
    {
        File.WriteAllText(_claude.UserSettingsFile, theirs);

        var result = StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Loaded, _logger);

        Assert.Equal(SettingsWriteOutcome.Written, result?.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, Installed());
        Assert.Equal(survivesInPlace, SettingsText().Contains(marker, StringComparison.Ordinal));

        Assert.NotNull(result?.BackupPath);
        Assert.Equal(theirs, File.ReadAllText(result!.Value.BackupPath!));
    }

    // ---- What it says -------------------------------------------------------------------------------

    /// <summary>
    /// <strong>The line names the events and the script path, and no part of the operator's file.</strong>
    /// </summary>
    /// <remarks>
    /// T1.24's rule. The settings file holds the operator's permissions and preferences and is not
    /// ours to copy into a log; the marker below is a string that exists nowhere but in that file,
    /// so any log line quoting the file quotes it.
    /// </remarks>
    [Fact]
    public void The_line_names_the_events_and_the_script_and_nothing_of_the_file()
    {
        const string Marker = "zqx-operator-secret";

        File.WriteAllText(_claude.UserSettingsFile, $$"""{ "model": "{{Marker}}" }""");

        StartupHookInstall.Run(Installer(), installAtStart: true, SettingsLoadOutcome.Loaded, _logger);

        var lines = _sink.Matching(_paths.HookScriptFile);

        Assert.NotEmpty(lines);
        Assert.All(
            HookEventNames.Accepted,
            name => Assert.Contains(lines, line => line.Contains(name, StringComparison.Ordinal)));

        Assert.Empty(_sink.Matching(Marker));
    }

    /// <summary>An opted-out start says why nothing happened, and how to undo it.</summary>
    /// <remarks>
    /// <c>HookInstaller.Check</c> warns that the handler is missing and deliberately no longer
    /// prescribes a remedy — it does not know whether this start is about to install. This is the
    /// branch where the remedy is the operator's, so this is where it is said.
    /// </remarks>
    [Fact]
    public void An_opted_out_start_says_why_nothing_was_installed()
    {
        StartupHookInstall.Run(Installer(), installAtStart: false, SettingsLoadOutcome.Loaded, _logger);

        Assert.NotEmpty(_sink.Matching("installHooksAtStart"));
        Assert.NotEmpty(_sink.Matching("--install-hooks"));
    }

    // ---- The switches, and what survives a restart --------------------------------------------------

    /// <summary>
    /// <strong><c>--remove-hooks</c>, then a start, leaves the handler removed.</strong>
    /// </summary>
    /// <remarks>
    /// The worst outcome T1.32 can produce, and the whole reason the flag is part of the feature
    /// rather than a nicety: an operator who removes their hooks and finds them back has been
    /// overruled by the application, and a supported switch has become a no-op with extra steps.
    /// </remarks>
    [Fact]
    public void A_removal_survives_the_next_start()
    {
        Installer().Install();

        var told = new List<string>();
        var code = HookSwitches.Run(HookSwitches.Remove, Installer(), _ => { });
        StartupHookInstall.RecordSwitch(HookSwitches.Remove, code, Store(), _logger, told.Add);

        Assert.Equal(0, code);
        Assert.False(Store().Load().Settings.InstallHooksAtStart);

        // The consequence reaches the report the operator is reading, not only the log: they are
        // told starts will no longer install, and which switch reverses it.
        var consequence = Assert.Single(told);
        Assert.Contains("no longer install", consequence, StringComparison.Ordinal);
        Assert.Contains(HookSwitches.Install, consequence, StringComparison.Ordinal);

        var next = Store().Load();
        var result = StartupHookInstall.Run(
            Installer(),
            next.Settings.InstallHooksAtStart,
            next.Outcome,
            _logger);

        Assert.Null(result);
        Assert.Equal(0, Installed());
    }

    /// <summary>
    /// <strong>A corrupt own settings file does not override a recorded removal (review of
    /// T1.32).</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap the review found: <c>SettingsStore.Load</c> hands back defaults for a file that
    /// would not read, the default says install, and the recorded <c>--remove-hooks</c> is in
    /// exactly the file that could not be read. Without the outcome reaching the decision, this
    /// test's end state is eight events reinstalled over the operator's stated removal — silently,
    /// at some start after the corruption, with a log line as the only evidence.
    /// </para>
    /// <para>
    /// The corruption is written over a settings file that genuinely recorded the opt-out first,
    /// because that is the sequence the rule exists for. A test that never recorded the removal
    /// would pass for the weaker reason that there was nothing to override.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_corrupt_own_settings_file_does_not_override_a_recorded_removal()
    {
        Installer().Install();
        StartupHookInstall.RecordSwitch(
            HookSwitches.Remove,
            HookSwitches.Run(HookSwitches.Remove, Installer(), _ => { }),
            Store(),
            _logger);

        File.WriteAllText(_paths.SettingsFile, """{ "installHooksAtStart": """);

        var loaded = Store().Load();

        Assert.Equal(SettingsLoadOutcome.Unreadable, loaded.Outcome);
        Assert.True(
            loaded.Settings.InstallHooksAtStart,
            "The default must say install, or this test is not exercising the override.");

        var result = StartupHookInstall.Run(
            Installer(),
            loaded.Settings.InstallHooksAtStart,
            loaded.Outcome,
            _logger);

        Assert.Null(result);
        Assert.Equal(0, Installed());
        Assert.NotEmpty(_sink.Matching("could not be read, so the \"installHooksAtStart\" opt-out is unknown"));
    }

    /// <summary>
    /// <strong><c>--install-hooks</c> after that restores the handler and the flag together.</strong>
    /// </summary>
    /// <remarks>
    /// Together, because either alone leaves the operator somewhere they did not ask to be: the
    /// handler without the flag is removed again by nothing but is unrepairable at the next start,
    /// and the flag without the handler is a start that installs — which is right, but only after a
    /// restart they were not told they needed.
    /// </remarks>
    [Fact]
    public void An_install_switch_restores_the_handler_and_the_flag()
    {
        Installer().Install();
        StartupHookInstall.RecordSwitch(
            HookSwitches.Remove,
            HookSwitches.Run(HookSwitches.Remove, Installer(), _ => { }),
            Store(),
            _logger);

        var told = new List<string>();
        var code = HookSwitches.Run(HookSwitches.Install, Installer(), _ => { });
        var recorded = StartupHookInstall.RecordSwitch(HookSwitches.Install, code, Store(), _logger, told.Add);

        Assert.Equal(0, code);
        Assert.True(recorded);
        Assert.True(Store().Load().Settings.InstallHooksAtStart);
        Assert.Equal(HookEventNames.Accepted.Count, Installed());
        Assert.Contains(told, line => line.Contains("install the hook again", StringComparison.Ordinal));
    }

    /// <summary>
    /// <strong>A switch that failed decides nothing.</strong>
    /// </summary>
    /// <remarks>
    /// Recording the flag on a failure would write down an intention Claude Code's settings do not
    /// reflect — the operator asked to remove hooks that are still installed, and the next start
    /// would then leave them installed and silent about it.
    /// </remarks>
    [Fact]
    public void A_failed_switch_records_nothing()
    {
        Assert.False(StartupHookInstall.RecordSwitch(HookSwitches.Remove, 1, Store(), _logger));
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    /// <summary>
    /// <strong>An install switch on a machine with no settings file writes none.</strong>
    /// </summary>
    /// <remarks>
    /// The flag would be <c>true</c>, which is what an absent file already means. Writing it anyway
    /// would create a settings file on every first install purely to record a default, and would
    /// make <c>--install-hooks</c> look like it had changed something it had not.
    /// </remarks>
    [Fact]
    public void An_install_switch_writes_no_settings_file_to_record_the_default()
    {
        Assert.False(StartupHookInstall.RecordSwitch(HookSwitches.Install, 0, Store(), _logger));
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    /// <summary>
    /// <strong>The dashboard's own settings are not written back from a partial parse either.</strong>
    /// </summary>
    /// <remarks>
    /// <see cref="SettingsStore.Load"/> hands back defaults for a file that would not read — right
    /// for deciding what to run with, and destructive if saved, because the save would replace
    /// whatever the operator wrote with a fresh object. The same rule the hook path follows, applied
    /// to our own file.
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
    /// <remarks>
    /// The record is loaded, one member is changed, and the whole is written back — so a member the
    /// load did not carry would be silently dropped from the operator's file by a switch that had
    /// nothing to do with it.
    /// </remarks>
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
    public void An_absent_key_installs_at_start()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "port": 51000 }""");

        Assert.True(Store().Load().Settings.InstallHooksAtStart);
    }
}
