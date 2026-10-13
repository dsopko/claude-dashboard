using System.IO;
using ClaudeDashboard.App;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.Tests.Configuration;
using ClaudeDashboard.Tests.Hosting;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// A start whose data folder lock another process holds exits through the real start, with the
/// other-session box, and asks nothing of the port (T1.86, issue #150).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why through the start.</strong> The lock, the decision and the box text each have tests
/// of their own, and the review showed that two plants in <c>Program</c> passed all of them: the
/// folder bit forced to "free", and the probe made before the decision. Only a test that runs the
/// start with the lock held elsewhere sees how <c>Program</c> joins the three.
/// </para>
/// <para>
/// <strong>Safe to run because both roots are redirected</strong>, as in
/// <c>BadSettingsLeftAloneTests</c>: <c>CLAUDE_DASHBOARD_HOME</c> points at a scratch folder, and
/// <c>CLAUDE_CONFIG_DIR</c> at a scratch path that does not exist, so a start that went too far
/// would find no Claude Code and run no <c>claude</c> command. The start runs through
/// <c>Program.Run</c> with a recorder for the box; only <c>Main</c> passes the real one.
/// </para>
/// </remarks>
[Collection(DataFolderEnvironment.Name)]
public sealed class OtherSessionStartTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly string _claudeRoot;

    public OtherSessionStartTests()
    {
        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        _claudeRoot = Path.Combine(_root, "no-claude");
        Directory.CreateDirectory(_paths.Root);
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
    /// The gate is free and another thread holds the data folder lock: a copy of us on this folder
    /// in another Windows session. The start exits with 1 and one other-session box, makes no
    /// request to the port in <c>port.txt</c> (no probe and no <c>/show</c>), and writes no
    /// <c>listening.txt</c>.
    /// </summary>
    /// <remarks>
    /// The port in <c>port.txt</c> is a listener that counts what connects to it and never answers.
    /// A probe would connect to it once, and a <c>/show</c> once more. All four claims are checked
    /// together, so a failure names every one that broke.
    /// </remarks>
    [Fact]
    public void A_start_whose_folder_another_session_holds_exits_with_the_box_and_asks_nothing_of_the_port()
    {
        // A start that went past the decision (a defect this test exists to catch) builds the host
        // and then the window. On an MTA thread the window cannot be built, so that start fails and
        // returns; on an STA thread it would show the window and wait for it to close.
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());

        using var listener = CannedServer.Silent();
        PortFile.Write(_paths, listener.Port);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holderHeld = false;

        // On another thread: a mutex is granted again to the thread that holds it.
        var holder = new Thread(() =>
        {
            using var folder = DataFolderLock.Acquire(_paths.Root);
            holderHeld = folder.IsHeld;
            held.Set();
            release.Wait();
        });

        holder.Start();
        held.Wait();

        var boxes = new List<(string Text, string Title)>();
        int code;

        try
        {
            Assert.True(holderHeld, "The test could not take the data folder lock, so it would not test another session.");

            using (MainSwitchTests.Set(DashboardPaths.HomeVariable, _paths.Root))
            using (MainSwitchTests.Set(ClaudeCodePaths.ConfigDirectoryVariable, _claudeRoot))
            {
                code = Program.Run([], (text, title) => boxes.Add((text, title)));
            }
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        Assert.Multiple(
            () => Assert.Equal(1, code),
            () =>
            {
                var box = Assert.Single(boxes);
                Assert.StartsWith(StandDownNotice.OtherSession, box.Text, StringComparison.Ordinal);
                Assert.EndsWith($"The details are in the newest log file in {_paths.LogFolder}", box.Text, StringComparison.Ordinal);
                Assert.Equal(StandDownNotice.Title, box.Title);
            },
            () => Assert.True(listener.Accepted == 0, $"The start connected to the port in port.txt: {listener.State}"),
            () => Assert.False(File.Exists(_paths.ListeningFile), "The start wrote listening.txt."));
    }
}
