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
        var program = GuardScan.CodeOnly(Program());

        var withdrawals =
            GuardScan.Occurrences(program, "announcement.Withdraw()")
            + GuardScan.Occurrences(program, "announcement?.Withdraw()");

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
    /// <c>StartupHookInstall</c> — the type that holds every rule about the connection, and the only
    /// one a test can call — rather than through a call spelled out in <c>Main</c>, which nothing
    /// can reach. And <c>Program.cs</c> names no install, no removal and nothing of the handler's
    /// shape: the plugin must outlive the process, so nothing here may take it out.
    /// </para>
    /// <para>
    /// <strong>What breaks without it.</strong> Move the decision inline and every rule it carries —
    /// register when missing, leave a turned-off plugin off, wait for an old hook to go, obey the
    /// opt-out, and show a notice whenever the dashboard is left unconnected — becomes unreachable
    /// from any test and fails silently in production.
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
        Assert.Contains("StartupHookInstall.RunAtStart(", GuardScan.CodeOnly(program), StringComparison.Ordinal);

        Assert.DoesNotContain(".Install()", program, StringComparison.Ordinal);
        Assert.DoesNotContain(".Remove()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("HookHandlers.", program, StringComparison.Ordinal);
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
        var code = GuardScan.CodeOnly(Program());

        const string Call = "StartupHookInstall.RunAtStart(";

        foreach (var line in code.Split('\n'))
        {
            var directive = line.TrimStart();

            if (directive.StartsWith("using", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("StartupHookInstall", directive, StringComparison.Ordinal);
            }
        }

        var occurrences = GuardScan.Occurrences(code, Call);

        Assert.True(
            occurrences == 1,
            $"Program.cs runs the start-time hook decision {occurrences} time(s) in code; it must be " +
            "exactly one, so that the call this guard reads is the call that runs.");

        var arguments = TopLevelArguments(
            code,
            code.IndexOf(Call, StringComparison.Ordinal) + Call.Length,
            out var closed);

        Assert.True(closed, "The StartupHookInstall.RunAtStart call is never closed, which cannot compile.");
        Assert.True(
            arguments.Count >= 2,
            $"The StartupHookInstall.RunAtStart call has {arguments.Count} argument(s); the start is " +
            "expected as the second.");

        // T1.56: the flag and the outcome travel inside the start, and RunAtStart reads them from its
        // first load (SettingsKeptAsideTests holds that by behaviour). What only this guard can hold
        // is that the start handed over is the one made from the first load: not a fresh read, and
        // not a start built from a literal.
        Assert.Equal("start", arguments[1]);
        Assert.Equal(1, GuardScan.Occurrences(code, "var start = "));
        Assert.Equal(1, GuardScan.Occurrences(code, ".PrepareForStart("));
        Assert.Equal("var start = settingsFile.PrepareForStart(loaded, DateTime.Now);", StatementAt(code, "var start = "));
        Assert.Equal("var loaded = settingsFile.Load();", StatementAt(code, "var loaded = "));
    }

    /// <summary>
    /// <strong>Both hook paths are handed the real parts: the check, the plugin installer and the
    /// notice.</strong>
    /// </summary>
    /// <remarks>
    /// The plugin is the only route since the operator's ruling of 2026-10-01, so each of these is
    /// a required argument and leaving one out no longer compiles. What a caller can still do is
    /// pass the wrong thing — a notice it made itself, which no window is bound to, would show
    /// nothing while every test stayed green. These two calls are the only ones in the product, and
    /// <c>Main</c> cannot be run past them by a test.
    /// </remarks>
    [Fact]
    public void Both_hook_paths_are_handed_the_plugin_route()
    {
        var code = GuardScan.CodeOnly(Program());

        const string Start = "StartupHookInstall.RunAtStart(";

        var arguments = TopLevelArguments(
            code,
            code.IndexOf(Start, StringComparison.Ordinal) + Start.Length,
            out var closed);

        Assert.True(closed, "The StartupHookInstall.RunAtStart call is never closed, which cannot compile.");
        Assert.True(
            arguments.Count == 5,
            $"The StartupHookInstall.RunAtStart call has {arguments.Count} argument(s); the plugin installer " +
            "is expected as the fourth and the notice as the fifth.");
        Assert.Equal("host.Services.GetRequiredService<HookCheck>()", arguments[0]);
        Assert.Equal("host.Services.GetRequiredService<PluginInstaller>()", arguments[3]);

        // The notice (the rulings of 2026-10-01): the host's own, which the tray and the window
        // are bound to. Without it a start that leaves the dashboard unconnected says so only in
        // the log, and the window shows a quiet day.
        Assert.Equal("host.Services.GetRequiredService<HookNotice>()", arguments[4]);

        const string Switch = "HookSwitches.Run(";

        Assert.Equal(1, GuardScan.Occurrences(code, Switch));

        var switchArguments = TopLevelArguments(
            code,
            code.IndexOf(Switch, StringComparison.Ordinal) + Switch.Length,
            out var switchClosed);

        Assert.True(switchClosed, "The HookSwitches.Run call is never closed, which cannot compile.");
        Assert.Equal(["requested", "check", "Report", "plugin"], switchArguments);
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
            GuardScan.CodeOnly(Program()),
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
        var program = GuardScan.CodeOnly(Program());

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
    /// <strong>Velopack's lifecycle handler is the first statement of <c>Main</c> — ahead of the
    /// hook switches, ahead of the gate, ahead of everything (PKG.1).</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// During install, update and uninstall, Velopack launches the exe with lifecycle arguments
    /// and expects <c>Run()</c> to handle them and exit the process. Anything ahead of it can
    /// shoot those invocations down: the switches would read a lifecycle launch as an ordinary
    /// start, and the single-instance gate would hand it over to the running instance — which
    /// would raise a window and leave the install half-done. Nothing behavioural can pin this:
    /// the failure needs a real installer mid-flight, and the suite has no such thing.
    /// </para>
    /// <para>
    /// <strong>First statement, asserted as "nothing but whitespace precedes it", not as an
    /// ordering between tokens.</strong> An ordering assertion would stay green while somebody
    /// slid a new "harmless" call above it — exactly the drift this guard exists to stop,
    /// because whatever runs first can run instead. The two index comparisons are kept as well,
    /// so a failure names the thing that got ahead when it is one of the two known killers.
    /// </para>
    /// <para>
    /// <strong>What this guard cannot see, named so its coverage is not overread (PKG.1 fix
    /// cycle 1).</strong> "First statement of <c>Main</c>" is not "first thing the process runs":
    /// a static field initialised with <c>SingleInstanceGate.Acquire(...)</c> takes the gate in
    /// the type initialiser, before <c>Main</c>'s first statement, and this guard and the
    /// behavioural tests all stay green — the reviewer measured it. An expression-bodied
    /// <c>Main</c> forwarding to a helper likewise moves the body out of the window this guard
    /// reads. Both are outside its stated model, the way the value override is outside the
    /// opt-out guard's; the model is honest drift within <c>Main</c>'s body as written.
    /// </para>
    /// </remarks>
    [Fact]
    public void Velopack_runs_before_everything_else_in_Main()
    {
        var code = GuardScan.CodeOnly(Program());

        // The builder chain grew in issue #36: the uninstall hook that removes the start-with-Windows
        // entry sits between Build() and Run(). The guard reads the chain's start, and asserts the
        // statement still ends in Run() and still carries the hook.
        const string Call = "VelopackApp.Build()";

        var occurrences = GuardScan.Occurrences(code, Call);

        Assert.True(
            occurrences > 0,
            "Program.cs no longer contains the text 'VelopackApp.Build()'. If the " +
            "builder chain grew — .OnFirstRun, .SetArgs — the call has moved, not gone: update " +
            "this guard's Call constant to the new text and keep the first-statement assertion.");

        Assert.True(
            occurrences == 1,
            $"Program.cs calls VelopackApp.Build().Run() {occurrences} times in code; it must be " +
            "exactly one, so that the call this guard reads is the call that runs.");

        var signature = code.IndexOf("public static int Main(string[] args)", StringComparison.Ordinal);

        Assert.True(signature >= 0, "Program.cs no longer declares the Main this guard reads.");

        var body = code.IndexOf('{', signature);
        var velopack = code.IndexOf(Call, StringComparison.Ordinal);
        var statement = code[velopack..(code.IndexOf(';', velopack) + 1)];

        Assert.EndsWith(".Run();", statement, StringComparison.Ordinal);
        Assert.Contains(".OnBeforeUninstallFastCallback(", statement, StringComparison.Ordinal);
        Assert.Contains("StartWithWindows.RemoveOnUninstall(", statement, StringComparison.Ordinal);

        Assert.True(
            velopack > body,
            "VelopackApp.Build().Run() sits outside Main's body, which is not where the lifecycle " +
            "arguments arrive.");

        Assert.True(
            string.IsNullOrWhiteSpace(code[(body + 1)..velopack]),
            "Something in Main runs before VelopackApp.Build().Run(). Whatever runs first can run " +
            "instead: a Velopack lifecycle launch expects Run() to handle it and exit, and any " +
            "earlier statement can shoot that invocation down.");

        var switches = code.IndexOf("HookSwitches.Requested(args)", StringComparison.Ordinal);
        var gate = code.IndexOf("SingleInstanceGate.Acquire", StringComparison.Ordinal);

        Assert.True(
            switches > velopack,
            "The hook switches are answered before Velopack's lifecycle handler, so an install-time " +
            "launch would be read as an ordinary start.");
        Assert.True(
            gate > velopack,
            "The single-instance gate is taken before Velopack's lifecycle handler, so an update " +
            "launched while the dashboard runs would be handed over and shot down.");
    }

    /// <summary>
    /// <strong>Every start makes Windows' <c>Run</c> value match <c>startWithWindows</c>, after the
    /// hook install (issue #36).</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this one call a fresh install never registers to start at sign-in, which is the
    /// whole of issue #36, and every test of <c>StartWithWindows</c> stays green: they drive the
    /// type, not the start. The review's plant Y3 removed it and the suite passed.
    /// </para>
    /// <para>
    /// The statement is asserted whole, after comments and string contents are stripped: the
    /// setting it passes is the point, and a call that passed <c>true</c> would undo the operator's
    /// checkbox at every start. It runs once, and after <c>StartupHookInstall.Run</c>, where the
    /// settings it reads are already loaded and a registry failure cannot hold up the hook repair.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_start_makes_Windows_startup_match_the_setting_after_the_hook_install()
    {
        var code = GuardScan.CodeOnly(Program());

        const string Expected = "host.Services.GetRequiredService<StartWithWindows>().Reconcile(settings.StartWithWindows);";

        Assert.Equal(1, GuardScan.Occurrences(code, ".Reconcile("));
        Assert.Equal(Expected, StatementAt(code, ".Reconcile("));

        var hooks = code.IndexOf("StartupHookInstall.RunAtStart(", StringComparison.Ordinal);
        var reconcile = code.IndexOf(Expected, StringComparison.Ordinal);

        Assert.True(hooks >= 0, "Program.cs no longer calls StartupHookInstall.RunAtStart.");
        Assert.True(
            reconcile > hooks,
            "Program.cs makes the Run value match the setting before the hook install. It belongs " +
            "after: a refusing registry must not hold up the hook repair.");
    }

    /// <summary>
    /// <strong>The tray's "Settings…" reaches the Settings window (issue #36).</strong>
    /// </summary>
    /// <remarks>
    /// The tray item only raises <c>SettingsRequested</c>; the one line in <c>Program</c> that
    /// subscribes to it is what opens the window. The review's plant Y4 left it unsubscribed and the
    /// suite passed, because every test of the tray and the window drives one side alone. The
    /// subscription must also come before <c>app.Run</c>, which does not return until the dashboard
    /// quits.
    /// </remarks>
    [Fact]
    public void The_tray_settings_item_opens_the_settings_window()
    {
        var code = GuardScan.CodeOnly(Program());

        const string Host = "var settingsWindows = host.Services.GetRequiredService<SettingsWindowHost>();";
        const string Subscription = "tray.ViewModel.SettingsRequested += (_, _) => settingsWindows.Show();";

        Assert.Equal(1, GuardScan.Occurrences(code, "SettingsRequested"));
        Assert.Equal(Subscription, StatementAt(code, "SettingsRequested"));

        Assert.Equal(1, GuardScan.Occurrences(code, "settingsWindows ="));
        Assert.Equal(Host, StatementAt(code, "settingsWindows ="));

        var subscription = code.IndexOf(Subscription, StringComparison.Ordinal);
        var run = code.IndexOf("app.Run(window)", StringComparison.Ordinal);

        Assert.True(run >= 0, "Program.cs no longer runs the application with the window.");
        Assert.True(
            subscription < run,
            "Program.cs subscribes to SettingsRequested after app.Run, which only returns at quit.");
    }

    /// <summary>
    /// The whole statement that contains <paramref name="token"/>: from the end of the statement or
    /// block before it to its own semicolon, with whitespace runs folded to one space.
    /// </summary>
    private static string StatementAt(string code, string token)
    {
        var at = code.IndexOf(token, StringComparison.Ordinal);

        Assert.True(at >= 0, $"Program.cs no longer contains '{token}'.");

        var start = code.LastIndexOfAny([';', '{', '}'], at) + 1;
        var end = code.IndexOf(';', at) + 1;

        return string.Join(' ', code[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
}
