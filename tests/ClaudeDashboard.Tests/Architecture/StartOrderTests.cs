using System.IO;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// A start writes the hook script before it announces the port (T1.48, issue #57).
/// </summary>
/// <remarks>
/// <para>
/// Since T1.48 the dashboard refuses a hook without this run's token, and only the new script sends
/// one. Until T1.48 <c>Program</c> announced first and rewrote the script second, so an upgrade would
/// have met the old script — which sends no token — with a dashboard that requires one, and every
/// hook in that window would have been refused.
/// </para>
/// <para>
/// <strong>A scan, because the start cannot be driven from a test.</strong> <c>Program.Main</c> takes
/// the single-instance gate, shows a window and runs a dispatcher. The order is two statements, and
/// this holds them in order in the code, with comments and strings stripped so that a sentence about
/// the order cannot satisfy it.
/// </para>
/// </remarks>
public sealed class StartOrderTests
{
    [Fact]
    public void The_script_is_written_before_the_port_is_announced()
    {
        var code = GuardScan.CodeOnly(File.ReadAllText(
            Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Program.cs")));

        Assert.Equal(1, GuardScan.Occurrences(code, "HookScript.EnsureWrittenAtStart("));
        Assert.Equal(1, GuardScan.Occurrences(code, "announcement.Announce()"));
        Assert.True(
            code.IndexOf("HookScript.EnsureWrittenAtStart(", StringComparison.Ordinal)
                < code.IndexOf("announcement.Announce()", StringComparison.Ordinal),
            "Program must write the hook script before it announces listening.txt: the dashboard refuses a hook " +
            "without this run's token, and only the new script sends one.");
    }
}
