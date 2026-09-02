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
        // Code only (fix cycle 2): this is a count, and a comment carrying the call's exact text
        // would let a deleted site go unmissed — three real withdrawals plus one remembered in
        // prose would still count four.
        var program = CodeOnly(Program());

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

        // The positive search runs against code only, so a commented-out call cannot satisfy it
        // (fix cycle 2). The negative ones deliberately stay on the raw text: a forbidden call
        // appearing even in a comment is worth a failure that gets read.
        Assert.Contains("StartupHookInstall.Run(", CodeOnly(program), StringComparison.Ordinal);

        Assert.DoesNotContain(".Install()", program, StringComparison.Ordinal);
        Assert.DoesNotContain(".Remove()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("HookRegistration.", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The one start-time decision call passes the operator's opt-out and its load
    /// outcome — asserted by argument equality, not by the presence of tokens.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StartupHookInstall.Run</c> takes the flag and the outcome as arguments, so a caller that
    /// passed a literal <c>true</c> or a literal <c>SettingsLoadOutcome.Loaded</c> would compile,
    /// pass every behavioural test in the suite — they call the decision directly and supply both
    /// themselves — and quietly reinstate the hooks of an operator who ran <c>--remove-hooks</c>.
    /// The argument list is the only place it can be caught.
    /// </para>
    /// <para>
    /// <strong>THIS GUARD WAS DISARMED FOUR TIMES BEFORE IT REACHED THIS SHAPE, AND THE LADDER IS
    /// THE LESSON (T1.32 fix cycles 1 and 2).</strong> Version one searched from the call to the
    /// end of the file for the token; a literal <c>true</c> with the token in a trailing
    /// <c>//</c> comment left the suite green. Version two bounded the window to the argument list
    /// and stripped <c>//</c> comments; the reviewer beat it twice more — the token in a
    /// <c>/* */</c> block inside one argument slot, and a commented-out correct call one line
    /// above the real one, which the raw <c>IndexOf</c> found first so the window never read the
    /// real call at all. <strong>A guard that looks for tokens in a window is a statement more
    /// confident than the thing beneath it.</strong> So this version asserts the arguments: both
    /// comment styles and every string literal's contents are removed from the whole file first,
    /// the call must occur exactly once in what remains — a second call, correct or not, fails the
    /// guard rather than being the one it happens not to read — and arguments two and three must
    /// <em>equal</em> the expressions, not contain them.
    /// </para>
    /// <para>
    /// <strong>The exercise of beating it again found one more family, closed here, and its
    /// boundary, recorded here.</strong> A decoy call spelled inside a string literal, with the
    /// real call swallowed by phantom <c>"/*"</c> and <c>"*/"</c> strings, beats comment-stripping
    /// alone; blanking string contents closes it. A <c>using</c> alias for the type (or
    /// <c>using static</c>) would let the real call avoid the searched name entirely while a dead
    /// copy in an <c>#if false</c> block satisfied the count; the assertion below that no
    /// <c>using</c> directive names <c>StartupHookInstall</c> closes the alias half. What remains
    /// open, deliberately: reflection, source generators, a second file — a determined adversary
    /// can always beat a text guard, and could equally delete this test. The threat model is
    /// honest drift and lazy shortcuts, not adversaries, and for that the argument equality is the
    /// load-bearing line.
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
        var code = CodeOnly(Program());

        const string Call = "StartupHookInstall.Run(";

        foreach (var line in code.Split('\n'))
        {
            var directive = line.TrimStart();

            if (directive.StartsWith("using", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("StartupHookInstall", directive, StringComparison.Ordinal);
            }
        }

        var occurrences = Occurrences(code, Call);

        Assert.True(
            occurrences == 1,
            $"Program.cs runs the start-time hook decision {occurrences} time(s) in code; it must be " +
            "exactly one, so that the call this guard reads is the call that runs.");

        var arguments = TopLevelArguments(
            code,
            code.IndexOf(Call, StringComparison.Ordinal) + Call.Length,
            out var closed);

        Assert.True(closed, "The StartupHookInstall.Run call is never closed, which cannot compile.");
        Assert.True(
            arguments.Count >= 3,
            $"The StartupHookInstall.Run call has {arguments.Count} argument(s); the flag and the " +
            "outcome are expected as the second and third.");

        Assert.Equal("settings.InstallHooksAtStart", arguments[1]);
        Assert.Equal("loaded.Outcome", arguments[2]);
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
        // Code only, or a commented-out call would satisfy this — the same hole fix cycle 2
        // closed in the opt-out guard, closed here before anyone proves it.
        Assert.Contains(
            "StartupHookInstall.RecordSwitch(",
            CodeOnly(Program()),
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
        // Code only (fix cycle 2): against raw text, a comment mentioning the switches above the
        // gate would satisfy the ordering while the real call had moved below it.
        var program = CodeOnly(Program());

        var switches = program.IndexOf("HookSwitches.Requested(args)", StringComparison.Ordinal);
        var gate = program.IndexOf("SingleInstanceGate.Acquire", StringComparison.Ordinal);

        Assert.True(switches >= 0, "Program.cs no longer answers the hook switches at all.");
        Assert.True(gate >= 0, "Program.cs no longer takes the single-instance gate.");
        Assert.True(
            switches < gate,
            "Program.cs takes the single-instance gate before answering --install-hooks, so the switch " +
            "would stand down to a running dashboard and silently install nothing.");
    }

    /// <summary>
    /// Drops both comment styles and the <em>contents</em> of string and char literals, so that
    /// only code that runs can satisfy a positive search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Each omission here was a proven disarm before it was closed (T1.32 fix cycle
    /// 2).</strong> Stripping only <c>//</c> lines let a <c>/* */</c> block inside one argument
    /// slot carry the token — the first version's remark claimed a block comment could not
    /// compile, which is true of one spanning the list and false of one inside a slot. And
    /// stripping comments alone still leaves string literals, which cut both ways: a decoy call
    /// spelled inside a string would satisfy a search, and a <c>"/*"</c> in one string with a
    /// <c>"*/"</c> in another would open a phantom block that swallows the real call. Blanking
    /// string contents while keeping the quotes closes both directions at once.
    /// </para>
    /// <para>
    /// <strong>A pragmatic scanner, not a C# lexer, and its failures are closed.</strong> It
    /// tracks line comments, block comments, ordinary string and char literals with backslash
    /// escapes — the shapes <c>Program.cs</c> contains. A verbatim or raw string introduced later
    /// would be mis-scanned, and what that produces is mangled text in which a positive search
    /// finds nothing — a guard that fails and gets read, not one that quietly passes.
    /// </para>
    /// </remarks>
    private static string CodeOnly(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                if (i < text.Length)
                {
                    kept.Append('\n');
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;

                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                kept.Append(' ');
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                kept.Append(quote);
                i++;

                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                kept.Append(quote);
                continue;
            }

            kept.Append(c);
        }

        return kept.ToString();
    }

    /// <summary>The call's arguments, split on the commas at its own depth and trimmed.</summary>
    /// <remarks>
    /// Depth is tracked on parentheses and brackets, which is enough for this call and fails
    /// closed for one it is not enough for: an argument the split cuts wrongly compares unequal
    /// and the guard is read, not passed.
    /// </remarks>
    private static List<string> TopLevelArguments(string code, int afterOpenParen, out bool closed)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 1;
        closed = false;

        for (var i = afterOpenParen; i < code.Length; i++)
        {
            var c = code[i];

            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;

                if (depth == 0)
                {
                    closed = true;
                    break;
                }
            }
            else if (c == ',' && depth == 1)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        arguments.Add(current.ToString().Trim());

        return arguments;
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
