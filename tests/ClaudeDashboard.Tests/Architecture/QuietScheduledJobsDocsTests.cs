using System.IO;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// The quiet-tick documentation is acceptance, not a nicety (T1.44, issue #56).
/// </summary>
/// <remarks>
/// The dashboard cannot change anyone's cron prompt: the opt-in line has to be found where cron
/// authors look, and it has to be the line the code compares against. So the guide's example and
/// Appendix B's watchdog instruction are asserted to carry <see cref="QuietTicks.OptInLine"/> word for
/// word, and the line to carry <see cref="QuietTicks.Sentinel"/>. A reworded line in either place,
/// or a changed sentinel in the code, fails here rather than silently turning every tick into a beep.
/// </remarks>
public sealed class QuietScheduledJobsDocsTests
{
    /// <summary>
    /// The line tells the agent the sentinel bare, and tells it directly not to decorate it.
    /// </summary>
    /// <remarks>
    /// The agent reads only the cron prompt, never the guide. Measured on the archive, one-word
    /// replies end in punctuation; and a sentinel shown in backticks invites a reply in backticks.
    /// Both fail safe as one extra beep, but together they could mean the feature never works — so
    /// the line carries no backticks and says "no punctuation, quotes or formatting" (the T1.44
    /// review, ruled in). The matcher is unchanged: exact after trimming.
    /// </remarks>
    [Fact]
    public void The_opt_in_line_carries_the_bare_sentinel_and_forbids_decoration()
    {
        Assert.Contains($" {QuietTicks.Sentinel} ", QuietTicks.OptInLine, StringComparison.Ordinal);
        Assert.DoesNotContain("`", QuietTicks.OptInLine, StringComparison.Ordinal);
        Assert.Contains("no punctuation, quotes or formatting", QuietTicks.OptInLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_gives_the_line_and_a_complete_example_carrying_it()
    {
        var guide = Doc("docs/quiet-scheduled-jobs.md");

        // Once as the line to add, once inside the complete example.
        Assert.True(Occurrences(guide, QuietTicks.OptInLine) >= 2, "The guide should give the line and an example carrying it, word for word.");

        foreach (var topic in new[] { "opt-in", "delete the job and create it again", "harmless", "What still beeps", "How to check it is working" })
        {
            Assert.Contains(topic, guide, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Appendix_B_watchdog_instruction_carries_the_line_word_for_word()
    {
        var plan = Doc("docs/claude-dashboard-execution-plan.md");
        var watchdog = plan.Split('\n').Single(line => line.StartsWith("**Standing watchdog (never retired).**", StringComparison.Ordinal));

        Assert.Contains(QuietTicks.OptInLine, watchdog, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_has_the_section_and_links_the_guide()
    {
        var readme = Doc("README.md");

        Assert.Contains("## Quiet scheduled jobs", readme, StringComparison.Ordinal);
        Assert.Contains("(docs/quiet-scheduled-jobs.md)", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hook_reference_states_the_sentinel_convention()
    {
        var reference = Doc("docs/claude-code-hooks-reference.md");

        Assert.Contains($"`{QuietTicks.Sentinel}`", reference, StringComparison.Ordinal);
        Assert.Contains("(quiet-scheduled-jobs.md)", reference, StringComparison.Ordinal);
    }

    private static string Doc(string relative) =>
        File.ReadAllText(Path.Combine(RepoLayout.Root.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static int Occurrences(string text, string of)
    {
        var count = 0;

        for (var at = text.IndexOf(of, StringComparison.Ordinal); at >= 0; at = text.IndexOf(of, at + of.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
