using System.IO;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Three tripwires over <c>Program.cs</c> that no behavioural test can carry (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Source text is a weak kind of test and is used here only where the thing being
/// protected is not reachable from one.</strong> <c>Main</c> builds a WPF application, takes the
/// single-instance gate and runs a dispatcher; nothing in the suite can call it. What it does with
/// the hook types is therefore invisible to every other test in this project — and each of the
/// three claims below fails silently in production.
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
    /// <strong>The running dashboard reads Claude Code's settings and never writes them.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what replaced Impl §9.3's lifecycle, and it is the whole of issue #29's second
    /// half: the hook is installed once, by an explicit switch, and left alone. A start that wrote
    /// the operator's settings — even to repair a handler it found missing — would be the design
    /// being removed, reintroduced as a helpful gesture.
    /// </para>
    /// <para>
    /// <strong>What breaks without it.</strong> Exactly what broke before: a Claude Code session
    /// already open keeps whatever the file said when it started, so a dashboard that rewrites the
    /// file at every launch leaves running sessions disagreeing with it — and a dashboard that
    /// removes handlers at quit takes them from sessions that will never see them come back.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_startup_path_checks_the_hooks_and_does_not_write_them()
    {
        var program = Program();

        Assert.Contains("HookInstaller>().Check()", program, StringComparison.Ordinal);

        Assert.DoesNotContain(".Install()", program, StringComparison.Ordinal);
        Assert.DoesNotContain(".Remove()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("HookRegistration.", program, StringComparison.Ordinal);
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
