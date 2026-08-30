using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The type the switches call, against a real settings file on disk (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The parts were tested and the composition was not — the same hole T1.21 found in the
/// old lifecycle.</strong> The merge, the writer and the script each have a class of their own.
/// This is the type that puts them together and decides which file to open, and neutering any one
/// of those decisions leaves every other test in the suite green.
/// </para>
/// <para>
/// <strong>Claude Code's settings file, never the dashboard's.</strong> There are two files called
/// <c>settings.json</c> in this system, and reaching for the wrong one throws nothing, fails no
/// test, and presents as the dashboard never receiving another hook. Both roots are separate
/// temporary folders here so that a confusion between them shows up as a failure rather than as a
/// coincidence.
/// </para>
/// </remarks>
public sealed class HookInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _claudeRoot;
    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;

    public HookInstallerTests()
    {
        _claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(_claudeRoot);

        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);
        _claude = new ClaudeCodePaths(_claudeRoot);
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

    private HookInstaller Installer() => new(_claude, _paths, new FakeClock(), Logger.None);

    private string SettingsText() =>
        File.Exists(_claude.UserSettingsFile) ? File.ReadAllText(_claude.UserSettingsFile) : string.Empty;

    private JsonObject Settings() => HookRegistration.Parse(SettingsText());

    private IEnumerable<JsonObject> Handlers() =>
        Settings()["hooks"] is JsonObject hooks
            ? hooks.SelectMany(pair => (pair.Value as JsonArray ?? []).OfType<JsonObject>())
                .SelectMany(group => (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
            : [];

    // ---- Installing ---------------------------------------------------------------------------------

    /// <summary>
    /// Installing writes the script and the handler, into the two files each belongs in.
    /// </summary>
    /// <remarks>
    /// <strong>The script first, and that order is load-bearing.</strong> A handler naming a file
    /// that is not there is worse than no handler at all: Claude Code would run <c>cmd</c> against
    /// a missing path on every event, and <c>cmd</c> says so on stderr — which is exactly the noise
    /// issue #29 exists to remove.
    /// </remarks>
    [Fact]
    public void Installing_writes_the_script_and_the_handler()
    {
        var result = Installer().Install();

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.True(File.Exists(_paths.HookScriptFile));
        Assert.Equal(HookScript.Text, File.ReadAllText(_paths.HookScriptFile));
        Assert.Equal(HookEventNames.Accepted.Count, HookRegistration.CountInstalled(Settings(), _paths.HookScriptFile));
    }

    /// <summary>
    /// The handler names the script in <em>this</em> data folder, not a compiled-in path.
    /// </summary>
    /// <remarks>
    /// <c>CLAUDE_DASHBOARD_HOME</c> moves the data folder, and a staging install and a development
    /// run both use it. A handler carrying a path from anywhere but
    /// <see cref="DashboardPaths.HookScriptFile"/> would point a second install at the first one's
    /// script — which would work, silently, until the first install was removed.
    /// </remarks>
    [Fact]
    public void The_handler_names_the_script_in_this_data_folder()
    {
        var installer = Installer();

        installer.Install();

        var args = (JsonArray)Handlers().First(handler => handler["args"] is JsonArray)["args"]!;

        Assert.Equal(_paths.HookScriptFile, args[^1]!.GetValue<string>());
        Assert.Equal(_paths.HookScriptFile, installer.ScriptPath);
        Assert.StartsWith(_paths.Root, installer.ScriptPath, StringComparison.Ordinal);
    }

    /// <summary>The interpreter is <c>cmd.exe</c>, absolute, under the real system directory.</summary>
    /// <remarks>
    /// Nothing expands <c>%SystemRoot%</c> in the exec form, so a relative or variable-bearing
    /// value would be handed to <c>CreateProcess</c> as written and would simply not be found.
    /// </remarks>
    [Fact]
    public void The_interpreter_is_cmd_by_absolute_path()
    {
        Assert.True(Path.IsPathFullyQualified(HookInstaller.Interpreter));
        Assert.Equal("cmd.exe", Path.GetFileName(HookInstaller.Interpreter));
        Assert.True(File.Exists(HookInstaller.Interpreter), $"{HookInstaller.Interpreter} is not there.");
        Assert.DoesNotContain('%', HookInstaller.Interpreter);
    }

    /// <summary>Installing twice produces one handler and reports the second as nothing to do.</summary>
    /// <remarks>
    /// The operator will run this switch more than once — after an upgrade, after editing their
    /// settings, whenever they are unsure. It has to be safe, and it has to say so rather than
    /// looking like a failure.
    /// </remarks>
    [Fact]
    public void Installing_twice_produces_one_handler()
    {
        Installer().Install();
        var second = Installer().Install();

        Assert.Equal(SettingsWriteOutcome.NothingToDo, second.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, Handlers().Count(handler => handler["args"] is JsonArray));
    }

    /// <summary>A backup is taken before an existing file is changed.</summary>
    /// <remarks>
    /// Every Claude Code session on the machine reads this file. The backup is a plain copy at a
    /// stated path, restorable by hand with the dashboard uninstalled — a restore that depends on
    /// the thing that broke is not a restore.
    /// </remarks>
    [Fact]
    public void Installing_over_an_existing_file_backs_it_up_first()
    {
        const string Theirs = """{ "model": "opus" }""";
        File.WriteAllText(_claude.UserSettingsFile, Theirs);

        var result = Installer().Install();

        Assert.NotNull(result.BackupPath);
        Assert.Equal(Theirs, File.ReadAllText(result.BackupPath!));
    }

    /// <summary>Installing creates the data folder if it is not there.</summary>
    /// <remarks>
    /// <c>--install-hooks</c> may be the first thing ever run on a machine — that is what it is for
    /// — so it cannot assume the folder a normal start would have made.
    /// </remarks>
    [Fact]
    public void Installing_creates_the_data_folder()
    {
        var fresh = new DashboardPaths(Path.Combine(_root, "never-started"));

        new HookInstaller(_claude, fresh, new FakeClock(), Logger.None).Install();

        Assert.True(File.Exists(fresh.HookScriptFile));
    }

    // ---- Removing -----------------------------------------------------------------------------------

    /// <summary>Removing takes the handler out and names what went.</summary>
    [Fact]
    public void Removing_takes_the_handler_out_and_names_it()
    {
        Installer().Install();

        var (result, removed) = Installer().Remove();

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.Equal(HookEventNames.Accepted.Count, removed.ScriptPaths.Count);
        Assert.All(removed.ScriptPaths, path => Assert.Equal(_paths.HookScriptFile, path));
        Assert.Equal(0, HookRegistration.CountInstalled(Settings(), _paths.HookScriptFile));
    }

    /// <summary>
    /// <strong>Removing also takes out the legacy HTTP handlers, which makes it the migration tool.</strong>
    /// </summary>
    /// <remarks>
    /// The operator runs <c>--remove-hooks</c> and then <c>--install-hooks</c> and is done. Leaving
    /// the old handlers would leave the error on every turn that the whole task is about — and
    /// nothing else in the application will ever remove them, because a build that removed an
    /// <c>http</c> handler on its own would be indistinguishable from the design being replaced.
    /// </remarks>
    [Fact]
    public void Removing_also_takes_out_the_handlers_of_the_old_design()
    {
        File.WriteAllText(_claude.UserSettingsFile, """
            {
              "allowedHttpHookUrls": ["http://127.0.0.1:52789/hook"],
              "hooks": {
                "Stop": [ { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook" } ] } ]
              }
            }
            """);

        var (result, removed) = Installer().Remove();

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.Equal(["http://127.0.0.1:52789/hook"], removed.Urls);
        Assert.Equal(["http://127.0.0.1:52789/hook"], removed.AllowListUrls);
        Assert.Null(Settings()["hooks"]);
        Assert.Null(Settings()[HookRegistration.UrlAllowListKey]);
    }

    /// <summary>The script itself is left on disk after a removal.</summary>
    /// <remarks>
    /// Taking the handler out is what stops Claude Code running it. Deleting the file as well would
    /// make the switch destructive for no gain, and would mean a later <c>--install-hooks</c> had
    /// to put it back — which it does anyway, at every start.
    /// </remarks>
    [Fact]
    public void Removing_leaves_the_script_on_disk()
    {
        Installer().Install();

        Installer().Remove();

        Assert.True(File.Exists(_paths.HookScriptFile));
    }

    /// <summary>
    /// <strong>Removing what was never there leaves the file byte for byte, comments included.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Found by a failing test rather than by reasoning, and it is a real defect.</strong>
    /// <c>SettingsFileWriter</c> decides whether anything changed by comparing the text it read
    /// with the text it renders, and rendering preserves neither comments nor the operator's
    /// formatting — <c>JsonNode</c> carries neither. So a merge that removed nothing still counted
    /// as a change on any hand-formatted file, and <c>--remove-hooks</c> against a settings file
    /// this dashboard had never touched would silently strip every comment in it.
    /// </para>
    /// <para>
    /// The fixture is deliberately commented and irregularly spaced. A test written against
    /// already-rendered JSON would pass against the defect, which is how it survived being written
    /// at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void Removing_what_was_never_there_leaves_the_file_exactly_as_it_was()
    {
        const string Theirs = """
            {
              // the model I use — and this comment is the assertion
              "model": "opus",
                  "cleanupPeriodDays": 30,
            }
            """;
        File.WriteAllText(_claude.UserSettingsFile, Theirs);

        var (result, removed) = Installer().Remove();

        Assert.Equal(SettingsWriteOutcome.NothingToDo, result.Outcome);
        Assert.Equal(0, removed.Total);
        Assert.Equal(Theirs, SettingsText());
    }

    /// <summary>An unreadable file is a failure, not "there was nothing of ours".</summary>
    /// <remarks>
    /// The two are opposite messages to the operator: "your hooks are already gone" against "your
    /// settings file is broken and nothing has been touched". The first would have them install
    /// hooks they still have.
    /// </remarks>
    [Fact]
    public void Removing_from_an_unparseable_file_is_a_failure()
    {
        File.WriteAllText(_claude.UserSettingsFile, "{ \"hooks\": ");

        var (result, removed) = Installer().Remove();

        Assert.Equal(SettingsWriteOutcome.Unreadable, result.Outcome);
        Assert.Equal(0, removed.Total);
        Assert.NotNull(result.Problem);
        Assert.Equal("{ \"hooks\": ", SettingsText());
    }

    /// <summary>What is reported removed is counted once, not once per write attempt.</summary>
    /// <remarks>
    /// <c>Modify</c> calls its merge once per attempt, always against freshly read content, and
    /// retries when another writer wins the race. A report that accumulated across attempts would
    /// tell the operator their file lost twice as many entries as it did — on exactly the occasions
    /// when they most need the count to be right.
    /// </remarks>
    [Fact]
    public void The_report_is_not_doubled_by_a_retry()
    {
        Installer().Install();

        var (_, removed) = Installer().Remove();

        Assert.Equal(HookEventNames.Accepted.Count, removed.ScriptPaths.Count);
    }

    // ---- The start check ----------------------------------------------------------------------------

    /// <summary>The check sees a complete install as complete.</summary>
    [Fact]
    public void The_check_sees_a_complete_install()
    {
        Installer().Install();

        var presence = Installer().Check();

        Assert.True(presence.Complete);
        Assert.Equal(HookEventNames.Accepted.Count, presence.Events);
        Assert.Empty(presence.Foreign);
        Assert.Null(presence.Problem);
    }

    /// <summary>
    /// <strong>The check reads the file and writes nothing at all.</strong>
    /// </summary>
    /// <remarks>
    /// The whole replacement for Impl §9.3's lifecycle. The running dashboard must not touch the
    /// operator's settings, so this asserts the file is unchanged byte for byte — including the
    /// comment, which a parse-and-render round trip would silently drop.
    /// </remarks>
    [Fact]
    public void The_check_does_not_touch_the_file()
    {
        const string Theirs = """
            {
              // mine
              "model": "opus",
            }
            """;
        File.WriteAllText(_claude.UserSettingsFile, Theirs);
        var before = File.GetLastWriteTimeUtc(_claude.UserSettingsFile);

        Installer().Check();

        Assert.Equal(Theirs, SettingsText());
        Assert.Equal(before, File.GetLastWriteTimeUtc(_claude.UserSettingsFile));
    }

    /// <summary>A missing settings file reads as "not installed", not as a fault.</summary>
    [Fact]
    public void The_check_reports_a_missing_file_as_not_installed()
    {
        var presence = Installer().Check();

        Assert.False(presence.Complete);
        Assert.Equal(0, presence.Events);
        Assert.Null(presence.Problem);
    }

    /// <summary>A settings file that will not parse is a problem, not a zero.</summary>
    /// <remarks>
    /// The two are different diagnoses with different fixes: "install the hook" against "your
    /// settings file is broken and nothing is reading it". Reporting the second as the first would
    /// send the operator to run a switch that would then refuse for the same reason.
    /// </remarks>
    [Fact]
    public void The_check_reports_an_unparseable_file_as_a_problem()
    {
        File.WriteAllText(_claude.UserSettingsFile, "{ \"model\": ");

        var presence = Installer().Check();

        Assert.False(presence.Complete);
        Assert.NotNull(presence.Problem);
    }

    /// <summary>A hook deleted from one event is seen as partial.</summary>
    [Fact]
    public void The_check_sees_a_hook_deleted_from_one_event()
    {
        Installer().Install();

        var settings = Settings();
        ((JsonObject)settings["hooks"]!).Remove(HookEventNames.Stop);
        File.WriteAllText(_claude.UserSettingsFile, HookRegistration.Render(settings));

        var presence = Installer().Check();

        Assert.False(presence.Complete);
        Assert.Equal(HookEventNames.Accepted.Count - 1, presence.Events);
    }

    /// <summary>
    /// <strong>A hook installed under another data folder is reported as foreign, with its path.</strong>
    /// </summary>
    /// <remarks>
    /// <c>CLAUDE_DASHBOARD_HOME</c> makes this a real configuration rather than a corruption: a
    /// staging install and a live install on one machine produce exactly this. A warning that named
    /// only the path this process expected would send the operator hunting for a missing entry that
    /// is sitting in the file under another name — so the check has to be able to hand the log both
    /// paths.
    /// </remarks>
    [Fact]
    public void The_check_names_a_hook_installed_under_another_data_folder()
    {
        var other = new DashboardPaths(Path.Combine(_root, "staging-data"));
        Directory.CreateDirectory(other.Root);

        new HookInstaller(_claude, other, new FakeClock(), Logger.None).Install();

        var presence = Installer().Check();

        Assert.False(presence.Complete);
        Assert.Equal(0, presence.Events);
        Assert.Equal([other.HookScriptFile], presence.Foreign);
    }

    /// <summary>A healthy install reports no foreign paths, which is what stops a false warning.</summary>
    [Fact]
    public void A_healthy_install_reports_nothing_foreign()
    {
        Installer().Install();

        Assert.Empty(Installer().Check().Foreign);
    }

    [Fact]
    public void It_needs_all_of_its_collaborators()
    {
        var clock = new FakeClock();

        Assert.Throws<ArgumentNullException>(() => new HookInstaller(null!, _paths, clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookInstaller(_claude, null!, clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookInstaller(_claude, _paths, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookInstaller(_claude, _paths, clock, null!));
    }
}
