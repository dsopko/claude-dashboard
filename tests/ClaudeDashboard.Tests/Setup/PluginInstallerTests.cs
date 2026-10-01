using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// What the plugin installer asks the <c>claude</c> program, in what order, and what it makes of
/// each answer (issue #30).
/// </summary>
/// <remarks>
/// The real program is not run here. The commands themselves were measured by hand on 2026-09-30;
/// these tests hold the dashboard's side of that conversation — the files exist before anything
/// is asked, a refusal stops the sequence, and a missing program is a different outcome from a
/// refusal, because the caller falls back for one and reports the other.
/// </remarks>
public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly FakeClaudeCli _cli = new();

    public PluginInstallerTests()
    {
        _paths = new DashboardPaths(Path.Combine(_root, "data"));
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

    private PluginInstaller Installer() => new(_cli, _paths, Logger.None);

    private static ClaudeCliResult Refusal(string text) => new(true, 1, text);

    [Fact]
    public void Installing_adds_the_marketplace_from_the_data_folder_then_installs_the_plugin()
    {
        var result = Installer().Install();

        Assert.Equal(PluginOutcome.Registered, result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(
            [
                $"plugin marketplace add {_paths.PluginFolder}",
                "plugin install claude-dashboard@claude-dashboard",
            ],
            _cli.Commands);
    }

    /// <summary>
    /// <strong>The script and the plugin files are on disk before Claude Code is asked.</strong>
    /// A plugin registered first would, for a moment, name a script that is not there.
    /// </summary>
    [Fact]
    public void The_files_are_written_before_anything_is_asked()
    {
        var seen = new List<bool>();

        _cli.Before = _ => seen.Add(HookScript.Matches(_paths) && HookPlugin.Matches(_paths));

        Installer().Install();

        Assert.Equal([true, true], seen);
    }

    [Fact]
    public void Without_the_claude_program_it_asks_once_and_says_so()
    {
        _cli.Found = false;

        var result = Installer().Install();

        Assert.Equal(PluginOutcome.CliNotFound, result.Outcome);
        Assert.Single(_cli.Calls);
    }

    [Fact]
    public void A_refused_marketplace_stops_before_the_install()
    {
        _cli.Answer = arguments => arguments[1] == "marketplace" ? Refusal("no such folder") : null;

        var result = Installer().Install();

        Assert.Equal(PluginOutcome.Failed, result.Outcome);
        Assert.Contains("no such folder", result.Problem, StringComparison.Ordinal);
        Assert.Single(_cli.Calls);
    }

    [Fact]
    public void A_refused_install_is_a_failure_with_the_reason()
    {
        _cli.Answer = arguments => arguments[1] == "install" ? Refusal("not in marketplace") : null;

        var result = Installer().Install();

        Assert.Equal(PluginOutcome.Failed, result.Outcome);
        Assert.Contains("not in marketplace", result.Problem, StringComparison.Ordinal);
        Assert.Equal(2, _cli.Calls.Count);
    }

    [Fact]
    public void Removing_uninstalls_the_plugin_then_forgets_the_marketplace()
    {
        var result = Installer().Remove();

        Assert.Equal(PluginOutcome.Removed, result.Outcome);
        Assert.Equal(
            [
                "plugin uninstall claude-dashboard@claude-dashboard",
                "plugin marketplace remove claude-dashboard",
            ],
            _cli.Commands);
    }

    /// <summary>
    /// <strong>One refusal does not leave the other half behind.</strong> A marketplace left
    /// declared after a failed uninstall would be found by the next start as a plugin folder of
    /// ours with nothing enabled in it.
    /// </summary>
    [Fact]
    public void A_refused_uninstall_still_asks_for_the_marketplace_to_go()
    {
        _cli.Answer = arguments => arguments[1] == "uninstall" ? Refusal("not installed") : null;

        var result = Installer().Remove();

        Assert.Equal(PluginOutcome.Failed, result.Outcome);
        Assert.Contains("not installed", result.Problem, StringComparison.Ordinal);
        Assert.Equal(2, _cli.Calls.Count);
    }

    [Fact]
    public void Removing_without_the_claude_program_says_so()
    {
        _cli.Found = false;

        Assert.Equal(PluginOutcome.CliNotFound, Installer().Remove().Outcome);
        Assert.Single(_cli.Calls);
    }

    [Fact]
    public void Removing_leaves_the_plugin_files_on_disk()
    {
        Installer().Install();
        Installer().Remove();

        Assert.True(HookPlugin.Matches(_paths));
    }

    [Fact]
    public void It_needs_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginInstaller(null!, _paths, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new PluginInstaller(_cli, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new PluginInstaller(_cli, _paths, null!));
    }
}
