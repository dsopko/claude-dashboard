using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// No source file holds UTF-8 that was decoded once too often (T1.52 review).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A conflict resolution in T1.52 rewrote <c>AppHost.cs</c> with
/// a Perl substitution whose replacement held a character above U+00FF. Perl then treated the
/// whole file as Latin-1 text and wrote it out as UTF-8 a second time: each em dash became six
/// bytes, and each section sign became a capital A with a circumflex before it. Only comments were
/// damaged, so every build and test passed.
/// </para>
/// <para>
/// <strong>Two shapes, and both are caught.</strong> UTF-8 read as Latin-1 gives a character from
/// U+00C2 to U+00F4 followed by one from U+0080 to U+00BF. UTF-8 read as Windows-1252 gives U+00E2
/// followed by the euro sign, U+20AC, at the start of every damaged dash and quote. Every character
/// below is built from its number, so this file holds none of the text it looks for.
/// </para>
/// </remarks>
public sealed class TextEncodingGuardTests
{
    private static readonly string[] Folders = ["src", "tests"];

    /// <summary>UTF-8 decoded as Latin-1: a lead byte's character, then a continuation byte's.</summary>
    private static readonly Regex Latin1Twice =
        new($"[{(char)0xC2}-{(char)0xF4}][{(char)0x80}-{(char)0xBF}]");

    /// <summary>UTF-8 decoded as Windows-1252: U+00E2 then U+20AC.</summary>
    private static readonly string Cp1252Twice = string.Concat((char)0xE2, (char)0x20AC);

    /// <summary>Every product and test source file: C# and XAML, outside build output.</summary>
    private static IEnumerable<string> SourceFiles() =>
        Folders
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(RepoLayout.Root.FullName, folder), "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static bool Damaged(string text) =>
        Latin1Twice.IsMatch(text) || text.Contains(Cp1252Twice, StringComparison.Ordinal);

    [Fact]
    public void No_source_file_holds_text_that_was_encoded_twice()
    {
        var damaged = new List<string>();

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (Damaged(lines[i]))
                {
                    damaged.Add($"{Path.GetRelativePath(RepoLayout.Root.FullName, file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            damaged.Count == 0,
            "Text that was encoded twice, so read as Latin-1 or Windows-1252 and written as UTF-8 again. " +
            "Restore the file from git and apply the change with a tool that reads UTF-8: " +
            string.Join(", ", damaged.Take(20)));
    }

    /// <summary>The guard sees both shapes, and leaves real text alone.</summary>
    [Fact]
    public void The_guard_knows_both_shapes_and_passes_real_text()
    {
        var section = (char)0xA7;
        var emDash = (char)0x2014;

        // A section sign and an em dash, each encoded twice through Latin-1.
        Assert.True(Damaged($"Impl {(char)0xC2}{section}3.1"));
        Assert.True(Damaged($"sound {(char)0xE2}{(char)0x80}{(char)0x94} that"));

        // An em dash encoded twice through Windows-1252.
        Assert.True(Damaged($"sound {(char)0xE2}{(char)0x20AC}{(char)0x201D} that"));

        // Real text: a section sign, an em dash, an accented letter, a multiplication sign.
        Assert.False(Damaged($"Impl {section}3.1, sound {emDash} that, caf{(char)0xE9}, {(char)0xD7} 2"));
    }
}
