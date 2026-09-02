using System.IO;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Tripwires over <c>Program.cs</c> that no behavioural test can carry (issue #29, issue #39).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Source text is a weak kind of test and is used here only where the thing being
/// protected is not reachable from one.</strong> <c>Main</c> builds a WPF application, takes the
/// single-instance gate and runs a dispatcher; nothing in the suite can call it. What it does with
/// the hook types is therefore invisible to every other test in this project — and each of the
/// claims below fails silently in production.
/// </para>
/// <para>
/// Each says what it pins and what breaks without it, so a reader who has to change one knows what
/// they are taking on.
/// </para>
/// </remarks>
public sealed class StartupHookGuardTests
{
    private static string Program() => File.ReadAllText(
        Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Program.cs"));

    /// <summary>
    /// <strong>THE ANNOUNCEMENT IS WITHDRAWN AT ALL FOUR EXITS, NOT THREE.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The process exception handlers, WPF's <c>SessionEnding</c>, the ordinary quit, and the
    /// <c>finally</c> — and the <c>finally</c> is the one the old lifecycle did not have. Both
    /// <c>catch</c> blocks in <c>Main</c> return without reaching the ordinary quit, so a throw
    /// after the socket was bound and before the window ran — a view model that would not build, a
    /// settings file that would not load — left the dashboard announced on an exit that was
    /// otherwise perfectly orderly.
    /// </para>
    /// <para>
    /// <strong>What breaks without it.</strong> <c>listening.txt</c> survives, and until the next
    /// start every hook event in every session posts the operator's prompt to whatever has taken
    /// that port. It is the residual issue #29 accepts for a hard kill, arriving on an exit that is
    /// not a hard kill.
    /// </para>
    /// <para>
    /// Counted rather than merely found, because the failure is one site being deleted while three
    /// remain — which no test would notice and no log line would record.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_announcement_is_withdrawn_at_every_exit()
    {
        var program = Program();

        var withdrawals =
            Occurrences(program, "announcement.Withdraw()")
            + Occurrences(program, "announcement?.Withdraw()");

        Assert.True(
            withdrawals == 4,
            $"Program.cs withdraws the ingress announcement {withdrawals} time(s); there are four exits " +
            "that must: the process exception handlers, SessionEnding, the ordinary quit, and the finally.");

        // The finally specifically, because it is the one that was missing and the only one whose
        // call is null-conditional — so a count alone could be satisfied without it.
        Assert.Contains("announcement?.Withdraw()", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong><c>Program.cs</c> reaches the hooks through the one decision, and spells no
    /// installing, removing or merging call of its own.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THIS TEST PREVIOUSLY ASSERTED THE OPPOSITE, AND THE CLAIM IT MADE WAS THE DEFECT
    /// (issue #39).</strong> It required <c>Program.cs</c> to contain
    /// <c>HookInstaller&gt;().Check()</c> and no <c>.Install()</c> anywhere, on the reasoning that
    /// "a start that wrote the operator's settings — even to repair a handler it found missing —
    /// would be the design being removed, reintroduced as a helpful gesture". That reasoning
    /// belonged to the HTTP lifecycle, which rewrote the file at <em>every</em> start and removed
    /// the handlers at quit. A command handler is not that: T1.28 left registration an install step
    /// with nothing running the install step, so a user who had never opened a terminal received no
    /// events for ever, and this guard is what would have failed anybody who fixed it.
    /// </para>
    /// <para>
    /// <strong>What is pinned now.</strong> The start path goes through
    /// <c>StartupHookInstall</c> — the type that holds every bound on the repair, and the only one a
    /// test can call — rather than through a call spelled out in <c>Main</c>, which nothing can
    /// reach. And <c>Program.cs</c> still names no removal and no direct merge: the handler must
    /// outlive the process, so nothing here may take one out, and nothing here may reach past the
    /// install path into the settings tree.
    /// </para>
    /// <para>
    /// <strong>What breaks without it.</strong> Move the decision inline and every rule it carries —
    /// install on absent, top up on partial, write nothing on complete, write nothing on a file that
    /// would not read, obey the opt-out — becomes unreachable from any test and fails silently in
    /// production. The worst of them is the one the operator sees: a complete handler rewritten
    /// anyway, stripping every comment in a hand-formatted settings file at every start.
    /// </para>
    /// <para>
    /// <strong>WHOLE FILE, WHICH IS WIDER THAN THE STARTUP PATH.</strong> <c>RunHookSwitch</c> lives
    /// in this file and reaches the installer through <c>HookSwitches</c>, so it spells no writing
    /// call either. Narrowing the <em>scan</em> to the startup method would be the wrong repair: a
    /// whole-file scan survives somebody moving code between methods, which is precisely how the
    /// breach would arrive.
    /// </para>
    /// </remarks>
    [Fact]
    public void Program_reaches_the_hooks_through_the_start_decision_and_spells_no_write_of_its_own()
    {
        var program = Program();

        Assert.Contains("StartupHookInstall.Run(", program, StringComparison.Ordinal);

        Assert.DoesNotContain(".Install()", program, StringComparison.Ordinal);
        Assert.DoesNotContain(".Remove()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("HookRegistration.", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The start reads the operator's opt-out rather than assuming it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StartupHookInstall.Run</c> takes the flag as an argument, so a caller that passed a
    /// literal <c>true</c> would compile, pass every behavioural test in the suite — they call the
    /// decision directly — and quietly reinstate the hooks of an operator who ran
    /// <c>--remove-hooks</c>. That is the worst outcome this change can produce and the argument is
    /// the only place it can be caught.
    /// </para>
    /// <para>
    /// <strong>What breaks without it.</strong> <c>--remove-hooks</c> becomes a no-op with extra
    /// steps: the handler goes, the next start puts it back, and the operator has been overruled by
    /// the application with a log line as the only evidence.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_start_passes_the_operators_opt_out_to_the_decision()
    {
        var program = Program();

        var at = program.IndexOf("StartupHookInstall.Run(", StringComparison.Ordinal);

        Assert.True(at >= 0, "Program.cs no longer runs the start-time hook decision at all.");

        Assert.Contains(
            "settings.InstallHooksAtStart",
            program[at..],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A switch's decision is recorded, or <c>--remove-hooks</c> does not survive a restart.</strong>
    /// </summary>
    /// <remarks>
    /// The flag is written by <c>RunHookSwitch</c>, which <c>Main</c> reaches and no test can. Drop
    /// the call and every test of <c>RecordSwitch</c> still passes, because they call it themselves;
    /// what is lost is the only thing that runs it in the product.
    /// </remarks>
    [Fact]
    public void A_switch_records_what_it_decided()
    {
        Assert.Contains(
            "StartupHookInstall.RecordSwitch(",
            Program(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The switches are answered before the single-instance gate is taken.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operator whose dashboard is running must still be able to repair their hooks. Behind the
    /// gate, <c>--install-hooks</c> would stand down and hand over to the running instance —
    /// which would raise its window and install nothing at all, so the switch would appear to work
    /// and would do nothing. That is the worst of the available failures, and the ordering is the
    /// only thing preventing it.
    /// </para>
    /// <para>
    /// Asserted by position rather than by presence: both lines survive a reordering, and the
    /// reordering is the defect.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_switches_are_answered_before_the_gate_is_taken()
    {
        var program = Program();

        var switches = program.IndexOf("HookSwitches.Requested(args)", StringComparison.Ordinal);
        var gate = program.IndexOf("SingleInstanceGate.Acquire", StringComparison.Ordinal);

        Assert.True(switches >= 0, "Program.cs no longer answers the hook switches at all.");
        Assert.True(gate >= 0, "Program.cs no longer takes the single-instance gate.");
        Assert.True(
            switches < gate,
            "Program.cs takes the single-instance gate before answering --install-hooks, so the switch " +
            "would stand down to a running dashboard and silently install nothing.");
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
