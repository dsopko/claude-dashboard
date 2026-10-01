using System.IO;

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
    /// <strong>Two files name Claude Code's settings file: the one that says where it is, and the
    /// one that reads it.</strong> A third is a new reader or a new writer, and either needs to be
    /// looked at by a person who knows the ruling.
    /// </summary>
    [Fact]
    public void Only_the_path_and_the_check_name_Claude_Codes_settings_file() =>
        Assert.Equal(["ClaudeCodePaths.cs", "HookCheck.cs"], FilesNaming("UserSettingsFile"));

    /// <summary>
    /// <strong>The file that reads it writes nothing at all.</strong> Not "does not write that
    /// file": it holds no writing call of any kind, so there is nothing to point at the wrong path.
    /// </summary>
    [Fact]
    public void The_check_reads_and_holds_no_call_that_writes()
    {
        var check = Assert.Single(ProductSources(), source => source.Name == "HookCheck.cs").Code;

        Assert.Equal(1, GuardScan.Occurrences(check, "File.ReadAllText(_claude.UserSettingsFile)"));

        foreach (var write in Writes)
        {
            Assert.DoesNotContain(write, check, StringComparison.Ordinal);
        }
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

        foreach (var write in Writes)
        {
            Assert.DoesNotContain(write, cli, StringComparison.Ordinal);
        }
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
