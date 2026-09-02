using System.IO;
using ClaudeDashboard.App;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Configuration;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The hook switches driven through the real <c>Program.Main</c> (PKG.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the one place the suite runs <c>Main</c>, and PKG.1 is why it exists.</strong>
/// Every other switch test calls <c>HookSwitches.Run</c> directly, which proves the switch and
/// says nothing about the path to it. PKG.1 put <c>VelopackApp.Build().Run()</c> ahead of the
/// switches, and a library that failed to load on .NET 10 would break every switch invocation
/// while every direct test stayed green.
/// </para>
/// <para>
/// <strong>What running <c>Run()</c> in-process does and does not prove (PKG.1 fix cycle 1).</strong>
/// It proves the Velopack assembly loads and <c>Run()</c> returns on this runtime, and that the
/// first-statement position executes on the switch path — a throw planted at that position fails
/// this test. It does <em>not</em> exercise Velopack's reading of <c>Main</c>'s arguments:
/// Velopack 1.2.0 reads <c>Environment.GetCommandLineArgs()</c> unless <c>SetArgs</c> is used,
/// so in this process <c>Run()</c> sees the test host's command line, not the array handed to
/// <c>Main</c> here. Argument reading against a real command line is checked by running the
/// built exe with each switch, which PKG.1's acceptance did as a measured step outside the
/// suite. The product is deliberately not changed to <c>SetArgs</c> for this test's benefit.
/// </para>
/// <para>
/// <strong>Safe to run because both roots are redirected, and serialized because the redirection
/// is process-wide.</strong> <c>CLAUDE_DASHBOARD_HOME</c> moves the dashboard's data folder and
/// <c>CLAUDE_CONFIG_DIR</c> moves Claude Code's — the same isolation T1.28's live verification
/// used — so nothing here touches the operator's real files. Environment variables are visible
/// to every test in the process, so this class joins the collection that serializes their use.
/// </para>
/// <para>
/// The switch path never takes the gate, never builds the host and never starts WPF: <c>Main</c>
/// answers it and returns. That is what makes calling it from a test possible at all.
/// </para>
/// </remarks>
[Collection(DataFolderEnvironment.Name)]
public sealed class MainSwitchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _dashboardRoot;
    private readonly string _claudeRoot;

    public MainSwitchTests()
    {
        _dashboardRoot = Path.Combine(_root, "data");
        _claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(_dashboardRoot);
        Directory.CreateDirectory(_claudeRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A log file straggler; the temp cleaner owns it now.
        }
    }

    /// <summary>
    /// <strong>Both switches work end to end through <c>Main</c>, Velopack handler included.</strong>
    /// </summary>
    /// <remarks>
    /// One test for the pair, in sequence, because the sequence is the operator's actual gesture
    /// and the second leg proves the first left the world in a state the second can act on. The
    /// exit codes are the machine-readable half of the switch contract; the files are the truth.
    /// </remarks>
    [Fact]
    public void The_switches_travel_the_whole_of_Main()
    {
        using (Set(DashboardPaths.HomeVariable, _dashboardRoot))
        using (Set(ClaudeCodePaths.ConfigDirectoryVariable, _claudeRoot))
        {
            var installed = Program.Main(["--install-hooks"]);

            Assert.Equal(0, installed);

            var scriptPath = new DashboardPaths(_dashboardRoot).HookScriptFile;
            var settingsFile = Path.Combine(_claudeRoot, "settings.json");

            Assert.True(File.Exists(scriptPath), "The install switch did not write the script.");
            Assert.Equal(
                HookEventNames.Accepted.Count,
                HookRegistration.CountInstalled(
                    HookRegistration.Parse(File.ReadAllText(settingsFile)),
                    scriptPath));

            var removed = Program.Main(["--remove-hooks"]);

            Assert.Equal(0, removed);
            Assert.Equal(
                0,
                HookRegistration.CountInstalled(
                    HookRegistration.Parse(File.ReadAllText(settingsFile)),
                    scriptPath));

            // The removal recorded the opt-out in the dashboard's own settings (T1.32), which is
            // the part of the switch contract that outlives the process.
            Assert.False(
                new SettingsStore(new DashboardPaths(_dashboardRoot)).Load().Settings.InstallHooksAtStart);
        }
    }

    private static Restore Set(string name, string? value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        return new Restore(() => Environment.SetEnvironmentVariable(name, previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
