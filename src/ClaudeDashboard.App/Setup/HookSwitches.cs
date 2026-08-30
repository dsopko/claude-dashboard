using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// <c>--install-hooks</c> and <c>--remove-hooks</c>: the one-shot switches that write Claude
/// Code's settings (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>They exist because first-run setup does not.</strong> T10.2 is unbuilt, and without a
/// call site <see cref="HookInstaller"/> would be code nobody could run and no new user could get
/// hooks at all. These are the operator's tool today and T10.2's call site tomorrow.
/// </para>
/// <para>
/// <strong>They are the only thing that writes the operator's hooks, and they never run by
/// themselves.</strong> That is the whole replacement for Impl §9.3's lifecycle: the running
/// dashboard reads Claude Code's settings to check its handler is there and writes nothing. A
/// build that removed an <c>http</c> handler on its own would be indistinguishable from the design
/// this task is removing.
/// </para>
/// <para>
/// <strong>They exit without starting the UI, and before the single-instance gate.</strong> Before
/// the gate on purpose: an operator whose dashboard is running must still be able to repair their
/// hooks, and a switch that refused because the application was open would be useless exactly when
/// it was needed.
/// </para>
/// <para>
/// <strong>Every removal is printed by name.</strong> Both removal rules match on a shape rather
/// than on a marker, so an entry the operator wrote themselves can match. Printing what left their
/// file is the safeguard, and it is a requirement rather than a courtesy.
/// </para>
/// </remarks>
public static class HookSwitches
{
    /// <summary>Writes the script and merges the handler.</summary>
    public const string Install = "--install-hooks";

    /// <summary>Takes out both the command handler and the legacy HTTP ones.</summary>
    public const string Remove = "--remove-hooks";

    /// <summary>The switch named on the command line, or null when none was.</summary>
    /// <remarks>
    /// <para>
    /// Ordinal-ignore-case, and the first one wins. Both at once is not an error worth its own
    /// path: they are opposites, so the operator meant the first.
    /// </para>
    /// <para>
    /// <strong>The canonical spelling comes back, not the one that was typed.</strong> Every caller
    /// compares the answer against <see cref="Install"/> or <see cref="Remove"/>, and returning
    /// <c>--INSTALL-HOOKS</c> would make a case-insensitive match here into a case-sensitive
    /// failure two calls later.
    /// </para>
    /// <para>
    /// Whole arguments only. A prefix match would make <c>--install-hooks-please</c> start an
    /// installer instead of the dashboard.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static string? Requested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (var argument in args)
        {
            if (string.Equals(argument, Install, StringComparison.OrdinalIgnoreCase))
            {
                return Install;
            }

            if (string.Equals(argument, Remove, StringComparison.OrdinalIgnoreCase))
            {
                return Remove;
            }
        }

        return null;
    }

    /// <summary>Runs <paramref name="requested"/> and reports what it did.</summary>
    /// <param name="requested">One of <see cref="Install"/> or <see cref="Remove"/>.</param>
    /// <param name="installer">The installer to drive.</param>
    /// <param name="report">Where the lines go — the console, or a test's list.</param>
    /// <remarks>
    /// <para>
    /// <strong>The reporter is a parameter</strong> so that what is said can be asserted without a
    /// console, and so that a test cannot pass by writing nowhere. It is the only reason this is
    /// not simply a method on <see cref="HookInstaller"/>.
    /// </para>
    /// <para>
    /// <strong>The exit code is the machine-readable half.</strong> Zero means it did what was
    /// asked; anything else means it could not, and T10.2 will read that rather than the text.
    /// </para>
    /// </remarks>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requested"/> is not one of the two switches.</exception>
    public static int Run(string requested, HookInstaller installer, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(report);

        if (string.Equals(requested, Install, StringComparison.OrdinalIgnoreCase))
        {
            return RunInstall(installer, report);
        }

        if (string.Equals(requested, Remove, StringComparison.OrdinalIgnoreCase))
        {
            return RunRemove(installer, report);
        }

        throw new ArgumentException($"Not a hook switch: {requested}", nameof(requested));
    }

    private static int RunInstall(HookInstaller installer, Action<string> report)
    {
        var result = installer.Install();

        report($"Script:   {installer.ScriptPath}");
        report($"Runs as:  {HookInstaller.Interpreter} /c <script>");
        report($"Events:   {string.Join(", ", HookEventNames.Accepted.Order(StringComparer.Ordinal))}");

        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                report($"Installed. Backup: {result.BackupPath ?? "(none needed — there was no file)"}");
                return 0;

            case SettingsWriteOutcome.NothingToDo:
                // Not a failure and not a no-op the operator should worry about: installing twice
                // is meant to be safe, and this is what "safe" looks like from the outside.
                report("Already installed. Nothing changed.");
                return 0;

            default:
                report($"FAILED: {result.Problem}. Claude Code's settings are unchanged.");
                return 1;
        }
    }

    private static int RunRemove(HookInstaller installer, Action<string> report)
    {
        var (result, removed) = installer.Remove();

        foreach (var path in removed.ScriptPaths)
        {
            report($"Removed hook:      {path}");
        }

        foreach (var url in removed.Urls)
        {
            report($"Removed old hook:  {url}");
        }

        foreach (var url in removed.AllowListUrls)
        {
            report($"Removed allowlist: {url}");
        }

        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                report($"Removed {removed.Total} entr{(removed.Total == 1 ? "y" : "ies")}. Backup: {result.BackupPath ?? "(none needed)"}");
                report($"The script itself is left at {installer.ScriptPath}; nothing runs it now.");
                return 0;

            case SettingsWriteOutcome.NothingToDo:
                report("Nothing of the dashboard's was in Claude Code's settings. Nothing changed.");
                return 0;

            default:
                report($"FAILED: {result.Problem}. Claude Code's settings are unchanged.");
                return 1;
        }
    }
}
