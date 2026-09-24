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
/// <strong>And the first version of this guard was itself the token-in-window shape, and the
/// reviewer beat it the way that shape is always beaten.</strong> It asserted the statement
/// Contains "sessions" and lacks "Rows" — and a Rows-derived local declared on the line above,
/// <c>var sessionsInView = Rows...</c>, satisfied both: "sessionsInView" carries "sessions" as a
/// substring and the "Rows" lives outside the statement window. 1545 of 1545 green. The same
/// ladder the opt-out guard climbed in T1.32, re-climbed here because its author held a new
/// guard to a lower standard than the one whose remark he had just cited. So the statement is
/// asserted by <em>equality</em> after trimming: exactly once in code, exactly this text. A
/// rename or re-wrap fails it with a message saying to update the text; an indirection cannot
/// pass it at all.
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

        var statement = code[at..end].Trim();

        const string Expected =
            "AnythingToAcknowledge = sessions.Any(session => Acknowledgment.Applies(session.State))";

        Assert.True(
            statement == Expected,
            $"The flag's assignment reads \"{statement}\". If it still reads the projection's " +
            "sessions and only the TEXT changed — a rename, a lambda parameter, a re-wrap — " +
            "update this guard's Expected constant and keep the equality. If the SOURCE moved to " +
            "Rows, or to anything derived from Rows however it is named, that is the defect this " +
            "guard exists to stop: the view never hides an eligible session today, and a flag " +
            "read off the view would go quietly wrong the day a collapse rule changes.");
    }
}
