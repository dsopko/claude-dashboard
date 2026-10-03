using System.IO;
using System.Text;
using ClaudeDashboard.App;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.Tests.Configuration;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// A second instance and the one-shot switches leave a settings file that does not parse byte for
/// byte, through the real <c>Program.Main</c> (T1.56, issue #73).
/// </summary>
/// <remarks>
/// <para>
/// Only a start that is the first instance and will show the window keeps a bad file aside. The
/// others never reach that point: the switches return before the single-instance gate, and a second
/// instance stands down right after it.
/// </para>
/// <para>
/// <strong>Safe to run because both roots are redirected</strong>, as in <c>MainSwitchTests</c>:
/// <c>CLAUDE_DASHBOARD_HOME</c> and <c>CLAUDE_CONFIG_DIR</c> point at scratch folders, and the class
/// joins the collection that serializes their use.
/// </para>
/// </remarks>
[Collection(DataFolderEnvironment.Name)]
public sealed class BadSettingsLeftAloneTests : IDisposable
{
    private static readonly byte[] BadBytes = Encoding.UTF8.GetBytes("{ \"port\": }");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly string _claudeRoot;

    public BadSettingsLeftAloneTests()
    {
        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        _claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(_paths.Root);
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

    [Theory]
    [InlineData("--install-hooks")]
    [InlineData("--remove-hooks")]
    [InlineData("--replay")]
    public void A_one_shot_switch_leaves_a_bad_file_byte_for_byte(string requested)
    {
        File.WriteAllBytes(_paths.SettingsFile, BadBytes);

        string[] args = requested == "--replay"
            ? [requested, Path.Combine(_root, "no-such.db")]
            : [requested];

        using (MainSwitchTests.Set(DashboardPaths.HomeVariable, _paths.Root))
        using (MainSwitchTests.Set(ClaudeCodePaths.ConfigDirectoryVariable, _claudeRoot))
        {
            Program.Main(args);
        }

        AssertLeftAlone();
    }

    /// <summary>
    /// A second instance stands down, and leaves the file as it is. The gate is held on another
    /// thread: a mutex is granted again to the thread that holds it.
    /// </summary>
    [Fact]
    public void A_second_instance_leaves_a_bad_file_byte_for_byte()
    {
        File.WriteAllBytes(_paths.SettingsFile, BadBytes);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holderFirst = false;

        var holder = new Thread(() =>
        {
            using var gate = SingleInstanceGate.Acquire(_paths.Root);
            holderFirst = gate.IsFirstInstance;
            held.Set();
            release.Wait();
        });

        holder.Start();
        held.Wait();

        try
        {
            Assert.True(holderFirst, "The test could not take the gate, so it would not test a second instance.");

            int code;

            using (MainSwitchTests.Set(DashboardPaths.HomeVariable, _paths.Root))
            using (MainSwitchTests.Set(ClaudeCodePaths.ConfigDirectoryVariable, _claudeRoot))
            {
                code = Program.Main([]);
            }

            // No port.txt, so nothing is probed: the gate alone says another copy runs.
            Assert.Equal(1, code);
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        AssertLeftAlone();
    }

    private void AssertLeftAlone()
    {
        Assert.Equal(BadBytes, File.ReadAllBytes(_paths.SettingsFile));
        Assert.Empty(Directory.GetFiles(_paths.Root, "settings.error-*.json"));
        Assert.False(File.Exists(_paths.SettingsFile + ".new"));
    }
}
