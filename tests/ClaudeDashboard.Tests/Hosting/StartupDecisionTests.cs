using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// Which interlock decides, and when (Impl §5.3).
/// </summary>
/// <remarks>
/// The table is written out cell by cell rather than as a rule, because the defect this guards
/// against is a rule that looks right. "The port is in use, so I am the second instance" is such
/// a rule, and it silently stops the dashboard from ever starting once anything else takes the
/// ingress port.
/// </remarks>
public sealed class StartupDecisionTests
{
    /// <summary>The ordinary first start: nothing holds the gate and nothing holds the port.</summary>
    [Fact]
    public void With_the_gate_taken_and_the_port_free_it_starts_normally() =>
        Assert.Equal(
            StartupAction.StartNormally,
            StartupDecision.For(holdsGate: true, PortOccupant.Free));

    /// <summary>The ordinary duplicate launch.</summary>
    [Fact]
    public void With_the_gate_held_by_a_copy_of_us_that_is_serving_it_signals_and_exits() =>
        Assert.Equal(
            StartupAction.SignalAndExit,
            StartupDecision.For(holdsGate: false, PortOccupant.OurInstance));

    /// <summary>
    /// A copy of us is serving without the gate — a stale build, or a gate name that no longer
    /// matches. Two of us on one data folder is the thing to avoid, whichever holds the mutex.
    /// </summary>
    [Fact]
    public void With_the_gate_taken_but_a_copy_of_us_on_the_port_it_signals_and_exits() =>
        Assert.Equal(
            StartupAction.SignalAndExit,
            StartupDecision.For(holdsGate: true, PortOccupant.OurInstance));

    // ---- Case 4: the gate is free, so it has said nothing, and the port must not decide alone ----

    /// <summary>
    /// 4b. Another user's dashboard holds the port. Not our duplicate.
    /// </summary>
    /// <remarks>
    /// The case the identity on <c>/health</c> exists for. A loopback bind is machine-wide while
    /// the gate is per logon session and per data folder, so under fast user switching the
    /// healthy dashboard on our port belongs to somebody else. Signalling it would raise their
    /// window on their desktop and leave this user with no dashboard and no explanation.
    /// </remarks>
    [Fact]
    public void With_another_users_dashboard_on_the_port_it_starts_without_ingress() =>
        Assert.Equal(
            StartupAction.StartWithoutIngress,
            StartupDecision.For(holdsGate: true, PortOccupant.OtherInstance));

    /// <summary>4c. A stranger, or an old build that cannot say who it is.</summary>
    [Fact]
    public void With_an_unrecognised_answer_on_the_port_it_starts_without_ingress() =>
        Assert.Equal(
            StartupAction.StartWithoutIngress,
            StartupDecision.For(holdsGate: true, PortOccupant.Unrecognised));

    /// <summary>4d. Something that accepts the connection and never answers.</summary>
    [Fact]
    public void With_a_silent_socket_on_the_port_it_starts_without_ingress() =>
        Assert.Equal(
            StartupAction.StartWithoutIngress,
            StartupDecision.For(holdsGate: true, PortOccupant.Silent));

    /// <summary>
    /// An occupant this build does not recognise still starts. Degrade, never crash: a dashboard
    /// that runs half-deaf and says so can be diagnosed, and one that exits cannot.
    /// </summary>
    [Fact]
    public void With_an_occupant_this_build_does_not_know_it_still_starts() =>
        Assert.Equal(
            StartupAction.StartWithoutIngress,
            StartupDecision.For(holdsGate: true, (PortOccupant)999));

    // ---- The gate is held, so a copy of us is alive; the only question is reaching it ----------

    /// <summary>
    /// A live copy holds the gate but nothing is on the port: it is starting, stopping, or
    /// running without ingress.
    /// </summary>
    /// <remarks>
    /// Starting anyway would be two Registries on one data folder, which is exactly what the gate
    /// exists to prevent — and taking the gate over is not even possible, because a gate whose
    /// holder is dead is granted with <c>TookOverFromACrash</c> and never reaches this cell.
    /// </remarks>
    [Fact]
    public void With_the_gate_held_and_the_port_free_it_reports_and_exits() =>
        Assert.Equal(
            StartupAction.ReportAndExit,
            StartupDecision.For(holdsGate: false, PortOccupant.Free));

    /// <summary>
    /// A copy of us holds the gate, but the port belongs to somebody else — so <c>/show</c> would
    /// reach a stranger.
    /// </summary>
    [Theory]
    [InlineData(PortOccupant.OtherInstance)]
    [InlineData(PortOccupant.Unrecognised)]
    [InlineData(PortOccupant.Silent)]
    public void With_the_gate_held_and_a_stranger_on_the_port_it_reports_and_exits(PortOccupant occupant) =>
        Assert.Equal(
            StartupAction.ReportAndExit,
            StartupDecision.For(holdsGate: false, occupant));

    /// <summary>
    /// The two reasons for standing down are diagnosed differently, because they have different
    /// fixes and the log line is the only diagnosis a windowless process can give.
    /// </summary>
    [Fact]
    public void The_two_stand_down_reasons_read_differently()
    {
        var nothingListening = StartupDecision.ExplainReportAndExit(PortOccupant.Free, 52789);
        var strangerListening = StartupDecision.ExplainReportAndExit(PortOccupant.Unrecognised, 52789);

        Assert.NotEqual(nothingListening, strangerListening);
        Assert.Contains("52789", nothingListening, StringComparison.Ordinal);
        Assert.Contains("52789", strangerListening, StringComparison.Ordinal);
        Assert.Contains("nothing is listening", nothingListening, StringComparison.Ordinal);
        Assert.Contains("held by something else", strangerListening, StringComparison.Ordinal);
    }
}
