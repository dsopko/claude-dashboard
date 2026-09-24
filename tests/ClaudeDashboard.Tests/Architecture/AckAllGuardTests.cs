using System.IO;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// One tripwire over <c>MainViewModel.cs</c>: the Ack-all flag is computed from the projection's
/// sessions, never from <c>Rows</c> (T1.34, issue #43).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A source-text guard, because no behavioural test can tell the two apart — the T1.34
/// review measured it.</strong> The product's own collapse rules mean an eligible session always
/// has a row: the Unread exemption and the never-summarised bands (Design Document §6 rule 3,
/// pinned by three <c>CollapseTests</c>) keep every <c>Acknowledgment.Applies</c> state out of
/// every footer, in both views. So a Rows-based flag ran the full suite green, the
/// collapsed-group test included, and the only thing standing between the flag and a later
/// Rows-based rewrite was a remark. A Rows-based flag would silently lean on those collapse
/// rules staying exactly as they are — the renderer is one rule change away — and the failure
/// would be the worst kind: the button unlit while a session waits behind whatever the new rule
/// hides.
/// </para>
/// <para>
/// Anchored to the assignment statement rather than a window, per the opt-out guard's ladder:
/// the statement must exist exactly once in code, read the sessions, and not read the view.
/// </para>
/// </remarks>
public sealed class AckAllGuardTests
{
    [Fact]
    public void The_flag_is_computed_from_the_sessions_and_not_the_view()
    {
        var code = GuardScan.CodeOnly(File.ReadAllText(Path.Combine(
            RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Ui", "MainViewModel.cs")));

        const string Assignment = "AnythingToAcknowledge =";

        Assert.True(
            GuardScan.Occurrences(code, Assignment) == 1,
            "MainViewModel.cs must assign AnythingToAcknowledge exactly once in code, so that the " +
            "statement this guard reads is the statement that runs.");

        var at = code.IndexOf(Assignment, StringComparison.Ordinal);
        var end = code.IndexOf(';', at);

        Assert.True(end > at, "The assignment is never terminated, which cannot compile.");

        var statement = code[at..end];

        Assert.Contains("sessions", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("Rows", statement, StringComparison.Ordinal);
    }
}
