using System.Text;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The prompt on a row is cut between characters, never inside one (T1.81, issue #21).
/// </summary>
/// <remarks>
/// <para>
/// Until T1.81 the snippet was <c>Prompt[..SnippetLength]</c>: 140 UTF-16 code units. A cut there could fall inside
/// an emoji, a ZWJ family, a flag or a letter with its combining accent, and the row ended in half a character. The
/// snippet now uses the title's cut (<see cref="ClusterText"/>): 140 grapheme clusters, and a ceiling of 560
/// characters.
/// </para>
/// <para>
/// The characters are built from their code points, not typed as escapes, so the test file shows what each one is.
/// </para>
/// </remarks>
public sealed class PromptSnippetTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>
    /// A cluster that ends exactly at 140 clusters is shown whole, and the prompt is not cut: 139 ASCII characters
    /// then the cluster are 140 clusters.
    /// </summary>
    [Theory]
    [InlineData("thumbs up")]
    [InlineData("family")]
    [InlineData("flag")]
    [InlineData("accent")]
    public void A_cluster_that_fits_the_budget_is_shown_whole(string name)
    {
        var cluster = Cluster(name);
        var prompt = new string('a', SessionViewModel.SnippetLength - 1) + cluster;

        var snippet = Row(prompt).PromptSnippet;

        Assert.Equal(prompt, snippet);
        WellFormed(snippet);
    }

    /// <summary>A cluster that straddles the boundary, with more after it, is kept whole and the ellipsis follows.</summary>
    [Theory]
    [InlineData("thumbs up")]
    [InlineData("family")]
    [InlineData("flag")]
    [InlineData("accent")]
    public void A_cluster_at_the_boundary_is_kept_whole_before_the_ellipsis(string name)
    {
        var cluster = Cluster(name);
        var prompt = new string('a', SessionViewModel.SnippetLength - 1) + cluster + " and more";

        var snippet = Row(prompt).PromptSnippet;

        Assert.Equal(new string('a', SessionViewModel.SnippetLength - 1) + cluster + "…", snippet);
        WellFormed(snippet);
    }

    /// <summary>
    /// A cluster after the 140th is left out whole: 140 ASCII characters then the cluster end with the ellipsis and
    /// no part of the cluster.
    /// </summary>
    [Theory]
    [InlineData("thumbs up")]
    [InlineData("family")]
    [InlineData("flag")]
    [InlineData("accent")]
    public void A_cluster_after_the_budget_is_left_out_whole(string name)
    {
        var prompt = new string('a', SessionViewModel.SnippetLength) + Cluster(name);

        var snippet = Row(prompt).PromptSnippet;

        Assert.Equal(new string('a', SessionViewModel.SnippetLength) + "…", snippet);
        WellFormed(snippet);
    }

    /// <summary>
    /// <strong>140 clusters is not a bound on length, so the ceiling does the work.</strong> 140 clusters of a letter
    /// and 50 combining marks each are 7,140 characters; the ceiling of 560 cuts after 10 whole clusters (510
    /// characters), with the ellipsis.
    /// </summary>
    [Fact]
    public void A_prompt_of_enormous_clusters_is_cut_by_the_ceiling()
    {
        var enormous = "a" + new string((char)0x0301, 50);
        var prompt = string.Concat(Enumerable.Repeat(enormous, SessionViewModel.SnippetLength));

        var snippet = Row(prompt).PromptSnippet;

        Assert.Equal(7_140, prompt.Length);
        Assert.Equal(string.Concat(Enumerable.Repeat(enormous, 10)) + "…", snippet);
        Assert.True(snippet.Length <= SessionViewModel.SnippetCharacterCeiling + 1, $"The row was handed {snippet.Length} characters.");
        WellFormed(snippet);
    }

    /// <summary>A short prompt is shown as it is, and the open row's prompt is never cut.</summary>
    [Fact]
    public void A_short_prompt_is_shown_whole_and_the_full_prompt_is_unchanged()
    {
        var prompt = new string('a', SessionViewModel.SnippetLength + 10) + Cluster("family");
        var row = Row(prompt);

        Assert.Equal("short", Row("short").PromptSnippet);
        Assert.Equal(prompt, row.Prompt);
    }

    /// <summary>
    /// The snippet is a well-formed string: it round-trips through UTF-8 unchanged and holds no replacement character.
    /// A lone surrogate fails both, which is the objective form of "draws as a replacement glyph".
    /// </summary>
    private static void WellFormed(string snippet)
    {
        Assert.Equal(snippet, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(snippet)));
        Assert.DoesNotContain(snippet.EnumerateRunes(), rune => rune == Rune.ReplacementChar);
    }

    /// <summary>The four clusters of issue #21's table, each more than one UTF-16 code unit.</summary>
    internal static string Cluster(string name)
    {
        var joiner = ((char)0x200D).ToString();

        return name switch
        {
            // U+1F44D, a surrogate pair.
            "thumbs up" => char.ConvertFromUtf32(0x1F44D),

            // Man, woman, girl, held together by two zero-width joiners: 8 code units, one cluster.
            "family" => char.ConvertFromUtf32(0x1F468) + joiner + char.ConvertFromUtf32(0x1F469) + joiner + char.ConvertFromUtf32(0x1F467),

            // The flag of the United Kingdom: two regional indicators, G and B.
            "flag" => char.ConvertFromUtf32(0x1F1EC) + char.ConvertFromUtf32(0x1F1E7),

            // e and a combining acute accent, U+0301.
            "accent" => "e" + (char)0x0301,

            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    private static SessionViewModel Row(string prompt) =>
        new(
            new Session
            {
                Id = new SessionId("88a85f67-4c21-4f0e-9d3b-a1b2c3d4e5f6"),
                State = SessionState.Working,
                Latest = new Exchange { Prompt = prompt, StartedAt = At },
                Cwd = @"C:\w",
                WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\w"),
                EnteredAt = At,
                LastActivity = At,
                LastHeardAt = At,
            },
            new MotionPolicy(() => false, observeChanges: false));
}
