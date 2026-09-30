using System.Xml.Linq;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The logon task definition (Impl §10.1, §10.2).
/// </summary>
/// <remarks>
/// The definition is built and asserted here; registering one on the machine is a separate,
/// deliberate act and is not done by the test suite.
/// </remarks>
public sealed class LogonTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Definition() =>
        XDocument.Parse(LogonTask.BuildDefinition(@"C:\Apps\ClaudeDashboard.App.exe", @"MACHINE\dave"));

    [Fact]
    public void The_definition_starts_the_executable_it_was_given() =>
        Assert.Equal(
            @"C:\Apps\ClaudeDashboard.App.exe",
            Definition().Descendants(Ns + "Command").Single().Value);

    [Fact]
    public void The_definition_triggers_at_logon() =>
        Assert.Single(Definition().Descendants(Ns + "LogonTrigger"));

    /// <summary>
    /// It runs at normal integrity, and the negative half is the one that matters.
    /// </summary>
    /// <remarks>
    /// An elevated dashboard cannot inspect the non-elevated terminal windows it exists to watch
    /// (Impl §6.5), so <c>HighestAvailable</c> would break the product as well as the working
    /// agreement — and it is a single word to get wrong.
    /// </remarks>
    [Fact]
    public void The_definition_never_asks_for_elevation()
    {
        var xml = LogonTask.BuildDefinition(@"C:\Apps\ClaudeDashboard.App.exe", @"MACHINE\dave");

        Assert.Equal("LeastPrivilege", XDocument.Parse(xml).Descendants(Ns + "RunLevel").Single().Value);
        Assert.DoesNotContain("HighestAvailable", xml, StringComparison.Ordinal);
    }

    /// <summary>Impl §10.1's restart policy, which is why this is XML and not switches.</summary>
    /// <remarks>
    /// <c>schtasks</c>'s command-line form cannot express restart-on-failure at all. It is the only
    /// thing that shortens the window in which a crashed dashboard leaves its hooks registered with
    /// nothing listening, so losing it would quietly widen the residual T1.18 documents.
    /// </remarks>
    [Fact]
    public void The_definition_restarts_every_minute_up_to_three_times()
    {
        var restart = Definition().Descendants(Ns + "RestartOnFailure").Single();

        Assert.Equal("PT1M", restart.Element(Ns + "Interval")!.Value);
        Assert.Equal("3", restart.Element(Ns + "Count")!.Value);
    }

    /// <summary>A laptop unplugging must not stop the dashboard, or stop it starting.</summary>
    [Fact]
    public void The_definition_does_not_care_about_batteries()
    {
        var document = Definition();

        Assert.Equal("false", document.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", document.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
    }

    /// <summary>It runs from logon to logoff, with no time limit to cut it off mid-afternoon.</summary>
    [Fact]
    public void The_definition_has_no_execution_time_limit() =>
        Assert.Equal("PT0S", Definition().Descendants(Ns + "ExecutionTimeLimit").Single().Value);

    /// <summary>
    /// What <see cref="LogonTask.Describe"/> reads is what a registration actually contains.
    /// </summary>
    /// <remarks>
    /// The two halves are pinned against each other. A reader tested only on hand-written XML would
    /// keep passing after the writer changed, and a writer tested only on its own output would keep
    /// passing after the reader stopped looking — and the reader is what verifies the real
    /// registration, which is the only check that catches a task the operator will not miss until
    /// their next logon.
    /// </remarks>
    [Fact]
    public void The_facts_read_back_are_the_facts_written()
    {
        var facts = LogonTask.Describe(LogonTask.BuildDefinition(@"C:\Apps\ClaudeDashboard.App.exe", @"MACHINE\dave"));

        Assert.Equal(@"C:\Apps\ClaudeDashboard.App.exe", facts.Command);
        Assert.Equal("LeastPrivilege", facts.RunLevel);
        Assert.True(facts.HasLogonTrigger);
        Assert.Equal("PT1M", facts.RestartInterval);
        Assert.Equal(3, facts.RestartCount);
    }

    /// <summary>
    /// A registration Windows stored is read as not elevated even though it names no run level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture is what <c>schtasks /query /xml</c> actually returned for a task registered from
    /// <see cref="LogonTask.BuildDefinition"/> on 2026-08-26: <strong>no <c>RunLevel</c> element at
    /// all</strong>, and the principal's user rewritten to a SID. Windows omits the default rather
    /// than storing it.
    /// </para>
    /// <para>
    /// Without this, a verification that looked for <c>LeastPrivilege</c> in a read-back would go
    /// red on a correct task — and the obvious repair, deleting the check, would then pass a task
    /// someone had changed to elevate.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_registration_windows_stored_reads_as_not_elevated()
    {
        var facts = LogonTask.Describe("""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>MACHINE\dave</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author">
                <UserId>S-1-5-21-3953501118-1735086671-3633542688-1001</UserId>
                <LogonType>InteractiveToken</LogonType>
              </Principal></Principals>
              <Settings><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>
              <Actions Context="Author"><Exec><Command>C:\Apps\ClaudeDashboard.App.exe</Command></Exec></Actions>
            </Task>
            """);

        Assert.Null(facts.RunLevel);
        Assert.False(facts.IsElevated);
        Assert.True(facts.HasLogonTrigger);
        Assert.Equal("PT1M", facts.RestartInterval);
        Assert.Equal(3, facts.RestartCount);
    }

    /// <summary>…and one that does ask for elevation is caught. The control for the above.</summary>
    /// <remarks>
    /// Without it, <c>IsElevated</c> returning false unconditionally would satisfy every other
    /// assertion in this class.
    /// </remarks>
    [Fact]
    public void A_registration_that_asks_for_elevation_is_caught()
    {
        var facts = LogonTask.Describe("""
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal id="Author"><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Actions Context="Author"><Exec><Command>C:\Apps\x.exe</Command></Exec></Actions>
            </Task>
            """);

        Assert.True(facts.IsElevated);
    }

    [Fact]
    public void A_definition_needs_an_executable_and_a_user()
    {
        Assert.Throws<ArgumentException>(() => LogonTask.BuildDefinition("  ", @"MACHINE\dave"));
        Assert.Throws<ArgumentException>(() => LogonTask.BuildDefinition(@"C:\x.exe", " "));
    }

    /// <summary>The account is qualified, because a logon trigger belongs to somebody.</summary>
    [Fact]
    public void The_current_user_is_named_in_full() =>
        Assert.Contains(Environment.UserName, LogonTask.CurrentUserId, StringComparison.OrdinalIgnoreCase);
}
