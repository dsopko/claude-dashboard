using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// The product reads Claude Code's settings file and never writes it (the operator's ruling of
/// 2026-10-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Source text, because the claim is about what the product does not do.</strong> A
/// behavioural test can show that one path left the file alone. It cannot show that no path
/// writes it, and the path that would is the one somebody adds later: a "small repair", a
/// fallback for the day the <c>claude</c> program is missing. That is how the file was written
/// until this ruling, and the reasons it must not be are not visible from inside such a change —
/// a second writer rewrites the whole file, loses a change Claude Code made in the same moment,
/// and reformats what it did not mean to touch.
/// </para>
/// <para>
/// <strong>What is pinned.</strong> Which source files can name the file at all, and that the one
/// which reads it holds no call that writes, moves, copies, creates or deletes anything. The
/// threat model is honest drift, not an adversary: a determined author could reach the file by a
/// path this does not see, and could equally delete this test.
/// </para>
/// </remarks>
public sealed class ClaudeSettingsReadOnlyGuardTests
{
    private static readonly string[] Writes =
    [
        "File.Write", "File.Append", "File.Move", "File.Replace", "File.Copy", "File.Create",
        "File.Delete", "File.Open", "FileStream", "StreamWriter", "Directory.CreateDirectory",
        "Directory.Delete",
    ];

    /// <summary>
    /// The writing calls in <paramref name="code"/>, each matched where a name starts: <c>File.Write</c>
    /// and <c>System.IO.File.Write</c> count, while <c>PortFile.Write</c> — a call to one of the seven
    /// writers, not a write of its own — does not.
    /// </summary>
    private static IReadOnlyList<string> WritesIn(string code) =>
        [.. Writes.Where(write => Regex.IsMatch(code, @"(?<!\w)" + Regex.Escape(write)))];

    /// <summary>Every product source file, with comments and string contents removed.</summary>
    private static IReadOnlyList<(string Name, string Code)> ProductSources() =>
    [
        .. Directory
            .EnumerateFiles(Path.Combine(RepoLayout.Root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(file => (Path.GetFileName(file), GuardScan.CodeOnly(File.ReadAllText(file))))
            .OrderBy(source => source.Item1, StringComparer.Ordinal),
    ];

    private static IReadOnlyList<string> FilesNaming(string identifier) =>
        [.. ProductSources().Where(source => source.Code.Contains(identifier, StringComparison.Ordinal)).Select(source => source.Name)];

    /// <summary>
    /// <strong>Three files name Claude Code's settings file: the one that says where it is, and the
    /// two that read it</strong> (the hook check, and since T1.68 the history's <c>cleanupPeriodDays</c>).
    /// A fourth is a new reader or a new writer, and either needs to be looked at by a person who knows
    /// the ruling.
    /// </summary>
    [Fact]
    public void Only_the_path_and_the_check_name_Claude_Codes_settings_file() =>
        Assert.Equal(["ClaudeCleanupPeriod.cs", "ClaudeCodePaths.cs", "HookCheck.cs"], FilesNaming("UserSettingsFile"));

    /// <summary>
    /// <strong>The file that reads it writes nothing at all.</strong> Not "does not write that
    /// file": it holds no writing call of any kind, so there is nothing to point at the wrong path.
    /// </summary>
    /// <summary>
    /// The history's reader of <c>cleanupPeriodDays</c> (T1.68, issue #102) reads the file once and
    /// holds no call that writes, as the check does.
    /// </summary>
    [Fact]
    public void The_cleanup_reader_reads_and_holds_no_call_that_writes()
    {
        var reader = Assert.Single(ProductSources(), source => source.Name == "ClaudeCleanupPeriod.cs").Code;

        Assert.Equal(1, GuardScan.Occurrences(reader, "File.ReadAllText(_claude.UserSettingsFile)"));

        Assert.Empty(WritesIn(reader));
    }

    [Fact]
    public void The_check_reads_and_holds_no_call_that_writes()
    {
        var check = Assert.Single(ProductSources(), source => source.Name == "HookCheck.cs").Code;

        Assert.Equal(1, GuardScan.Occurrences(check, "File.ReadAllText(_claude.UserSettingsFile)"));

        Assert.Empty(WritesIn(check));
    }

    /// <summary>
    /// <strong>Claude Code's directory is used by two files, and one of them only hands it to the
    /// <c>claude</c> program.</strong> A path built from the directory is the other way to reach
    /// the settings file, so the list is pinned too, and the file that runs <c>claude</c> holds no
    /// writing call either: whatever is written in that directory, Claude Code writes.
    /// </summary>
    [Fact]
    public void Claude_Codes_directory_is_used_only_where_it_is_read_or_handed_to_claude()
    {
        Assert.Equal(["ClaudeCli.cs", "HookCheck.cs"], FilesNaming(".ConfigDirectory"));

        var cli = Assert.Single(ProductSources(), source => source.Name == "ClaudeCli.cs").Code;

        Assert.Empty(WritesIn(cli));
    }

    /// <summary>
    /// <strong>The product files that write anything at all are exactly these seven</strong> (PR #66
    /// review, M2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two guards above follow the names the settings file is reached by, and names can be
    /// walked around: the review wrote the file from inside <c>ClaudeCodePaths.cs</c>, and from a
    /// new file that built the path from <c>ClaudeCodePaths.DefaultConfigDirectory</c> and a
    /// <c>"settings.json"</c> literal, and both passed. So this pins the writers instead, wherever
    /// their paths come from. Each of the seven writes only in the dashboard's own data folder or to
    /// the console.
    /// </para>
    /// <para>
    /// A seventh file that writes fails here until a person looks at what it writes and where, and
    /// adds it with a reason. That is the point: the next writer is the one nobody has checked
    /// against the ruling.
    /// </para>
    /// </remarks>
    [Fact]
    public void Exactly_the_known_files_hold_a_call_that_writes()
    {
        string[] writers =
        [
            .. ProductSources()
                .Where(source => WritesIn(source.Code).Count > 0)
                .Select(source => source.Name),
        ];

        Assert.Equal(
            [
                "ConsoleReport.cs",   // the switches' report, to the console it was started from
                "DashboardPaths.cs",  // creates the dashboard's own data and log folders
                "HookPlugin.cs",      // the plugin's files, in the dashboard's data folder
                "HookScript.cs",      // post-status.cmd, in the dashboard's data folder
                "ListeningFile.cs",   // listening.txt, in the dashboard's data folder
                "PortFile.cs",        // port.txt, in the dashboard's data folder
                "SettingsStore.cs",   // the dashboard's own settings.json
            ],
            writers);
    }

    /// <summary>
    /// <strong>The file that says where Claude Code's settings are writes nothing.</strong> It holds
    /// the path, so a write there needs no other name to reach the file.
    /// </summary>
    [Fact]
    public void The_paths_of_Claude_Code_hold_no_call_that_writes()
    {
        var paths = Assert.Single(ProductSources(), source => source.Name == "ClaudeCodePaths.cs").Code;

        Assert.Empty(WritesIn(paths));
    }

    /// <summary>The settings writer is gone, and nothing by its names has come back.</summary>
    [Fact]
    public void Nothing_of_the_settings_writer_remains()
    {
        foreach (var name in new[] { "SettingsFileWriter", "SettingsWriteResult", "HookRegistration", "HookInstaller" })
        {
            Assert.Empty(FilesNaming(name));
        }
    }
}
