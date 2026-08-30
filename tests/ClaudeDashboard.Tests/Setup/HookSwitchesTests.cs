using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// <c>--install-hooks</c> and <c>--remove-hooks</c>: what they do and what they say (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What they say is asserted, not merely that they say something.</strong> Both removal
/// rules match a <em>shape</em> rather than a marker, so an entry the operator wrote themselves can
/// match. Printing exactly what left their file is the safeguard against that — which makes the
/// report a requirement with a test, not a courtesy.
/// </para>
/// <para>
/// The reporter is a parameter for exactly this reason: a switch that wrote its report to a console
/// that was not attached would pass a test that only checked the exit code, and would tell the
/// operator nothing at all.
/// </para>
/// </remarks>
public sealed class HookSwitchesTests : IDisposable
{
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

    private HookInstaller Installer() => new(_claude, _paths, new FakeClock(), Logger.None);

    private int Run(string requested)
    {
        _report.Clear();

        return HookSwitches.Run(requested, Installer(), _report.Add);
    }

    private string Report() => string.Join(Environment.NewLine, _report);

    // ---- Which switch was asked for -----------------------------------------------------------------

    /// <summary>The switches are recognised, and nothing else is.</summary>
    /// <remarks>
    /// A false positive here starts a hook installer instead of the dashboard, so the match is on
    /// the whole argument rather than a prefix — <c>--install-hooks-please</c> is not a request to
    /// install hooks.
    /// </remarks>
    [Theory]
    [InlineData(new[] { "--install-hooks" }, HookSwitches.Install)]
    [InlineData(new[] { "--INSTALL-HOOKS" }, HookSwitches.Install)]
    [InlineData(new[] { "--remove-hooks" }, HookSwitches.Remove)]
    [InlineData(new[] { "-q", "--remove-hooks" }, HookSwitches.Remove)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--install-hooks-please" }, null)]
    [InlineData(new[] { "install-hooks" }, null)]
    [InlineData(new[] { "/show" }, null)]
    public void The_switches_are_recognised_and_nothing_else_is(string[] args, string? expected) =>
        Assert.Equal(expected, HookSwitches.Requested(args));

    /// <summary>With both named, the first wins.</summary>
    /// <remarks>
    /// They are opposites, so there is nothing sensible to do with both and no reason to make it an
    /// error the operator has to read about. The first is what they meant.
    /// </remarks>
    [Fact]
    public void With_both_switches_the_first_one_wins() =>
        Assert.Equal(HookSwitches.Remove, HookSwitches.Requested(["--remove-hooks", "--install-hooks"]));

    // ---- Installing ---------------------------------------------------------------------------------

    /// <summary>Installing succeeds, and says what it wrote and where.</summary>
    /// <remarks>
    /// The three facts an operator needs to check the result by hand: which script, what runs it,
    /// and which events. Without the event list a partial install looks identical to a complete
    /// one from the console.
    /// </remarks>
    [Fact]
    public void Installing_reports_the_script_the_interpreter_and_the_events()
    {
        Assert.Equal(0, Run(HookSwitches.Install));

        Assert.Contains(_paths.HookScriptFile, Report(), StringComparison.Ordinal);
        Assert.Contains(HookInstaller.Interpreter, Report(), StringComparison.Ordinal);

        foreach (var accepted in HookEventNames.Accepted)
        {
            Assert.Contains(accepted, Report(), StringComparison.Ordinal);
        }
    }

    /// <summary>Installing twice succeeds and says so, rather than looking like a failure.</summary>
    /// <remarks>
    /// The operator will run this more than once — after an upgrade, after editing their settings,
    /// whenever they are unsure. "Already installed" and exit 0 is the answer; a bare silence would
    /// read as a switch that did not work.
    /// </remarks>
    [Fact]
    public void Installing_twice_succeeds_and_says_nothing_changed()
    {
        Run(HookSwitches.Install);

        Assert.Equal(0, Run(HookSwitches.Install));
        Assert.Contains("Already installed", Report(), StringComparison.Ordinal);
    }

    // ---- Removing -----------------------------------------------------------------------------------

    /// <summary>
    /// <strong>Removing names every entry it took out — the path, the URL and the allowlist entry.</strong>
    /// </summary>
    /// <remarks>
    /// This is the safeguard, and it is the whole of it. Both rules match a shape, so an operator's
    /// own handler at a loopback hook URL would be removed by this switch; the only thing standing
    /// between that and a silent loss is a line on their console naming it.
    /// </remarks>
    [Fact]
    public void Removing_names_every_entry_it_took_out()
    {
        File.WriteAllText(_claude.UserSettingsFile, """
            {
              "allowedHttpHookUrls": ["http://127.0.0.1:52789/hook", "http://127.0.0.1:61000/hook"],
              "hooks": {
                "Stop": [ { "hooks": [ { "type": "http", "url": "http://127.0.0.1:61000/hook" } ] } ]
              }
            }
            """);
        Run(HookSwitches.Install);

        Assert.Equal(0, Run(HookSwitches.Remove));

        var report = Report();

        Assert.Contains(_paths.HookScriptFile, report, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:61000/hook", report, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:52789/hook", report, StringComparison.Ordinal);
    }

    /// <summary>Removing says the script is still on disk, so its presence is not a surprise.</summary>
    /// <remarks>
    /// An operator who removed their hooks and then found a <c>.cmd</c> in their data folder would
    /// reasonably wonder whether the removal worked. One line closes that.
    /// </remarks>
    [Fact]
    public void Removing_says_the_script_is_left_behind()
    {
        Run(HookSwitches.Install);

        Run(HookSwitches.Remove);

        Assert.Contains("is left at", Report(), StringComparison.Ordinal);
        Assert.True(File.Exists(_paths.HookScriptFile));
    }

    /// <summary>Removing what was never there succeeds and says nothing changed.</summary>
    [Fact]
    public void Removing_what_was_never_there_succeeds_and_says_so()
    {
        Assert.Equal(0, Run(HookSwitches.Remove));
        Assert.Contains("Nothing of the dashboard's", Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A settings file that cannot be parsed is a non-zero exit and a report that says why.
    /// </summary>
    /// <remarks>
    /// <strong>The exit code is the half T10.2 will read.</strong> A first-run setup that could not
    /// tell success from failure would report a working install to a user who has none — and the
    /// symptom of that is a dashboard that never shows a session, which reads as the dashboard
    /// being broken rather than as setup having failed.
    /// </remarks>
    [Fact]
    public void A_settings_file_that_cannot_be_read_is_a_failure_with_a_reason()
    {
        File.WriteAllText(_claude.UserSettingsFile, "{ \"model\": ");

        Assert.Equal(1, Run(HookSwitches.Install));
        Assert.Contains("FAILED", Report(), StringComparison.Ordinal);
        Assert.Contains("unchanged", Report(), StringComparison.Ordinal);

        // And it is left exactly as it was, which is the point of failing rather than repairing.
        Assert.Equal("{ \"model\": ", File.ReadAllText(_claude.UserSettingsFile));
    }

    [Fact]
    public void It_needs_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Requested(null!));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(null!, Installer(), _report.Add));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(HookSwitches.Install, null!, _report.Add));
        Assert.Throws<ArgumentNullException>(() => HookSwitches.Run(HookSwitches.Install, Installer(), null!));
        Assert.Throws<ArgumentException>(() => HookSwitches.Run("--something-else", Installer(), _report.Add));
    }
}
