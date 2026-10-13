using System.IO;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// No test can reach the real message box of a second start (T1.86, issue #150).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a scan.</strong> A real box waits for a click, so a test that reaches one holds the
/// whole run, and no assertion fails: the first build of T1.86 did exactly that. Program passed the
/// real box from inside the start, and <c>BadSettingsLeftAloneTests</c>, which drives the start as
/// a second instance, showed it. Nothing behavioural can prove a box was not shown; the code can be
/// held so that it cannot be.
/// </para>
/// <para>
/// <strong>The two halves.</strong> Only <c>Main</c> names the real box: it passes
/// <c>ShowMessageBox</c> to <c>Program.Run</c>, whose box argument is required. And a test calls
/// <c>Main</c> only with a hook switch, which returns before the gate and so before any box
/// (<c>MainSwitchTests</c> runs <c>Main</c> on purpose, for the Velopack call ahead of the switches).
/// Every other test drives the start through <c>Run</c> with a recorder.
/// </para>
/// <para>
/// Comments and strings are stripped first, so a sentence that names the call cannot satisfy or
/// break either count.
/// </para>
/// </remarks>
public sealed class MessageBoxGuardTests
{
    /// <summary>The real box is shown in one place, and only <c>Main</c> passes that place on.</summary>
    [Fact]
    public void Only_Main_supplies_the_real_message_box()
    {
        var appDirectory = RepoLayout.Project(RepoLayout.App).Directory!;

        var showers = SourceFiles(appDirectory)
            .Where(file => GuardScan.Occurrences(GuardScan.CodeOnly(File.ReadAllText(file.FullName)), "MessageBox.Show(") > 0)
            .Select(file => file.Name)
            .ToList();

        Assert.Equal(["Program.cs"], showers);

        var program = GuardScan.CodeOnly(File.ReadAllText(Path.Combine(appDirectory.FullName, "Program.cs")));

        Assert.Equal(1, GuardScan.Occurrences(program, "MessageBox.Show("));

        // The definition and one use, and the use is Main's call of Run.
        Assert.Equal(2, GuardScan.Occurrences(program, "ShowMessageBox"));
        Assert.Equal(1, GuardScan.Occurrences(program, "return Run(args, ShowMessageBox);"));

        var main = program.IndexOf("public static int Main(string[] args)", StringComparison.Ordinal);
        var run = program.IndexOf("internal static int Run(string[] args, Action<string, string> showBox)", StringComparison.Ordinal);
        var call = program.IndexOf("return Run(args, ShowMessageBox);", StringComparison.Ordinal);

        Assert.True(main >= 0 && run > main, "Program.cs no longer declares Main and then Run as this guard reads them.");
        Assert.True(call > main && call < run, "The real message box is passed from somewhere other than Main.");
    }

    /// <summary>A test calls <c>Main</c> only with a hook switch, which returns before any box.</summary>
    [Fact]
    public void A_test_calls_Main_only_with_a_hook_switch()
    {
        var testRoot = Path.Combine(RepoLayout.Root.FullName, "tests");

        var calls = 0;
        var literalCalls = 0;
        var switchCalls = 0;

        foreach (var file in SourceFiles(new DirectoryInfo(testRoot)))
        {
            var text = File.ReadAllText(file.FullName);
            var code = GuardScan.CodeOnly(text);

            // The scan keeps the quotes and blanks what is between them, so a call with one string
            // literal reads Program.Main([""]). The raw text then says which literal it is.
            calls += GuardScan.Occurrences(code, "Program.Main(");
            literalCalls += GuardScan.Occurrences(code, "Program.Main([\"\"])");
            switchCalls += GuardScan.Occurrences(text, "Program.Main([\"--install-hooks\"])");
            switchCalls += GuardScan.Occurrences(text, "Program.Main([\"--remove-hooks\"])");
        }

        Assert.True(switchCalls > 0, "No test runs Main with a hook switch: this guard reads nothing, so it is out of date.");
        Assert.True(
            calls == literalCalls && literalCalls == switchCalls,
            $"Program.Main is called {calls} time(s) in test code, {literalCalls} with one literal, {switchCalls} with a " +
            "literal hook switch. Each call must pass a literal hook switch, which returns before the gate. Drive the " +
            "start through Program.Run with a recorder for the message box: only Main passes the real one.");
    }

    private static IEnumerable<FileInfo> SourceFiles(DirectoryInfo root) =>
        root.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
