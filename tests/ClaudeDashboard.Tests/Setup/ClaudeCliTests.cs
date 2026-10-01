using System.IO;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Finding the <c>claude</c> program (issue #30).
/// </summary>
/// <remarks>
/// Only the search is covered. Running the program changes Claude Code's configuration, so the
/// real run is exercised once, through <c>Main</c> against redirected roots, in
/// <c>MainSwitchTests</c>.
/// </remarks>
public sealed class ClaudeCliTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

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

    private string FolderWithClaude(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "claude.exe"), string.Empty);

        return folder;
    }

    private string EmptyFolder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        return folder;
    }

    [Fact]
    public void The_first_folder_on_the_path_that_has_it_wins()
    {
        var empty = EmptyFolder("empty");
        var first = FolderWithClaude("first");
        var second = FolderWithClaude("second");

        var found = ClaudeCli.Locate(string.Join(Path.PathSeparator, empty, first, second), userProfile: null);

        Assert.Equal(Path.Combine(first, "claude.exe"), found);
    }

    [Fact]
    public void The_native_install_folder_is_tried_after_the_path()
    {
        var profile = EmptyFolder("profile");
        var bin = Path.Combine(profile, ".local", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude.exe"), string.Empty);

        Assert.Equal(Path.Combine(bin, "claude.exe"), ClaudeCli.Locate(EmptyFolder("empty"), profile));
        Assert.Equal(Path.Combine(bin, "claude.exe"), ClaudeCli.Locate(null, profile));
    }

    [Fact]
    public void A_path_entry_in_quotes_or_one_that_is_not_a_path_is_survived()
    {
        var folder = FolderWithClaude("quoted");

        var path = string.Join(Path.PathSeparator, "C:\\bad\0entry", "  ", $"\"{folder}\"");

        Assert.Equal(Path.Combine(folder, "claude.exe"), ClaudeCli.Locate(path, userProfile: null));
    }

    /// <summary>
    /// <strong>A <c>claude.cmd</c> shim is not found, on purpose.</strong> The caller then falls
    /// back to the settings file, which works; running the shim would need quoting rules nothing
    /// here measured.
    /// </summary>
    [Fact]
    public void Nothing_is_found_where_there_is_no_claude_exe()
    {
        var shim = EmptyFolder("shim");
        File.WriteAllText(Path.Combine(shim, "claude.cmd"), string.Empty);

        Assert.Null(ClaudeCli.Locate(shim, EmptyFolder("profile")));
        Assert.Null(ClaudeCli.Locate(null, null));
        Assert.Null(ClaudeCli.Locate(string.Empty, "  "));
    }

    /// <summary>
    /// A relative <c>PATH</c> entry is skipped, even when a <c>claude.exe</c> is there (the issue #30
    /// review, N1). It would resolve against whatever the current directory happens to be.
    /// </summary>
    [Fact]
    public void A_relative_path_entry_is_never_searched()
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), FolderWithClaude("relative"));

        Assert.False(Path.IsPathFullyQualified(relative), $"the test needs a relative entry, and got {relative}");
        Assert.True(File.Exists(Path.Combine(relative, "claude.exe")), "the relative entry must really lead to a claude.exe");

        Assert.Null(ClaudeCli.Locate(relative, null));
        Assert.Null(ClaudeCli.Locate($".{Path.PathSeparator}{relative}", null));
    }

    // ---- Running it: the budget, the stop and the output (the issue #30 review, M1) ----------------

    /// <summary>
    /// What claude prints on both streams, and its exit code, come back as they are.
    /// </summary>
    [Fact]
    public async Task Output_and_exit_code_come_back_from_a_run_that_finishes()
    {
        var cli = StandIn("""
            @echo off
            echo out-line
            echo err-line 1>&2
            exit /b 3
            """, TimeSpan.FromSeconds(30));

        var result = await Bounded(cli);

        Assert.True(result.Found);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("out-line", result.Output, StringComparison.Ordinal);
        Assert.Contains("err-line", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A claude that never finishes is stopped at the budget, with everything it started, and the
    /// run reports a failure.
    /// </summary>
    [Fact]
    public async Task A_claude_that_hangs_is_stopped_at_the_budget_with_its_children()
    {
        var before = Pings();
        var cli = StandIn("""
            @echo off
            ping -n 600 127.0.0.1 >nul
            exit /b 0
            """, TimeSpan.FromSeconds(2));

        var took = System.Diagnostics.Stopwatch.StartNew();
        var result = await Bounded(cli);
        took.Stop();

        Assert.True(result.Found);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("did not finish within 2 seconds", result.Output, StringComparison.Ordinal);
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"the run took {took.Elapsed}, far past its 2 s budget");
        AssertNoNewPings(before);
    }

    /// <summary>
    /// The case that hung a start: claude prints, starts a child that inherits its output, and exits
    /// 0. The child keeps the pipe open. The run returns inside its budget, with the exit code and
    /// what was printed, and the child is stopped.
    /// </summary>
    [Fact]
    public async Task A_child_holding_the_output_open_cannot_hold_the_run_past_its_budget()
    {
        var before = Pings();
        var cli = StandIn("""
            @echo off
            echo printed-before-exit
            start "" /b ping -n 600 127.0.0.1
            exit /b 0
            """, TimeSpan.FromSeconds(2));

        var took = System.Diagnostics.Stopwatch.StartNew();
        var result = await Bounded(cli);
        took.Stop();

        Assert.True(result.Found);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("printed-before-exit", result.Output, StringComparison.Ordinal);
        Assert.Contains("kept its output open", result.Output, StringComparison.Ordinal);
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"the run took {took.Elapsed}, far past its 2 s budget");
        AssertNoNewPings(before);
    }

    /// <summary>
    /// A claude stand-in: <c>cmd.exe</c> running <paramref name="script"/>, a real process that can
    /// hang or start children, without touching this process's <c>PATH</c>.
    /// </summary>
    private (ClaudeCli Cli, string Script) StandIn(string script, TimeSpan timeout)
    {
        var file = Path.Combine(EmptyFolder($"standin-{Guid.NewGuid():N}"), "claude-standin.cmd");
        File.WriteAllText(file, script.ReplaceLineEndings("\r\n"));

        var cli = new ClaudeCli(new ClaudeDashboard.App.Configuration.ClaudeCodePaths(_root))
        {
            Timeout = timeout,
            ProgramPath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        };

        return (cli, file);
    }

    /// <summary>
    /// Runs the stand-in on a worker, and fails rather than hangs if the run does not return: a
    /// regression here would otherwise hang the whole suite.
    /// </summary>
    private static async Task<ClaudeCliResult> Bounded((ClaudeCli Cli, string Script) standIn) =>
        await Task.Run(() => standIn.Cli.Run(["/c", standIn.Script])).WaitAsync(TimeSpan.FromSeconds(30));

    private static HashSet<int> Pings() =>
        [.. System.Diagnostics.Process.GetProcessesByName("PING").Select(process => process.Id)];

    /// <summary>
    /// No ping.exe started by this test is still running. Pings from elsewhere on the machine were
    /// recorded before the run and are left out.
    /// </summary>
    private static void AssertNoNewPings(HashSet<int> before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        List<int> left;

        do
        {
            left = [.. Pings().Except(before)];

            if (left.Count == 0)
            {
                return;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"ping.exe still running after the run returned: {string.Join(", ", left)}");
    }
}
