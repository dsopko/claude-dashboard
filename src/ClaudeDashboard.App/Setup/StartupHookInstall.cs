using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// The start-time repair: put Claude Code's hook back when it is missing (issue #39).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because T1.28 made registration an install step and left nothing running
/// that step.</strong> <see cref="HookInstaller.Install"/> was reachable from
/// <c>--install-hooks</c> and from nowhere else, and nothing called that — so a user who had never
/// opened a terminal started the exe and received no events, for ever, with one warning line in a
/// log they will not open. Impl §10.2 already required a first run to call that path once. This is
/// the thing that calls it.
/// </para>
/// <para>
/// <strong>It is a separate type rather than five lines in <c>Program.Main</c>, because
/// <c>Main</c> cannot be called from a test.</strong> It builds a WPF application, takes the
/// single-instance gate and runs a dispatcher. Every rule below — install on absent, top up on
/// partial, write nothing on complete, write nothing on a file that would not read, and obey the
/// operator's opt-out — fails silently in production and would be covered by nothing.
/// <c>Program.cs</c> keeps one call, and a source-text tripwire keeps it there.
/// </para>
/// <para>
/// <strong><see cref="HookInstaller"/> gains no logic from this.</strong>
/// <see cref="HookInstaller.Install"/> already writes the script before it merges, and
/// <see cref="HookRegistration.Register"/> matches on the script path and removes before it adds,
/// so calling it twice produces one handler. What is new here is only the decision about
/// <em>whether</em> to call it.
/// </para>
/// <para>
/// <strong>Nothing is written on quit, and that is not an omission.</strong> The half of issue #29
/// that mattered is that the handler outlives the process: it names a script, the script reads
/// <c>listening.txt</c>, and a closed dashboard therefore costs nothing. A shutdown removal would
/// reinstate the design T1.28 removed.
/// </para>
/// </remarks>
public static class StartupHookInstall
{
    /// <summary>Whether a start should write Claude Code's settings, given what it found.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A file that could not be read is never written, and that outranks everything
    /// else.</strong> <see cref="HookPresence.Problem"/> covers both an I/O failure and text that
    /// will not parse — the duplicate key included, which T1.28 made raise at the parse rather than
    /// somewhere later. Writing a settings file back from a partial parse costs the operator every
    /// hook, permission and preference in it, which is a far worse failure than the one this repair
    /// exists to fix.
    /// </para>
    /// <para>
    /// <strong>Partial tops up rather than being left alone.</strong> Some of the accepted events
    /// present has three causes — an interrupted write, a hand edit, or a build that added an event
    /// to <see cref="HookEventNames.Accepted"/> — and installing the missing ones is right for all
    /// three. A rule that only installed at zero would never reach an install that already existed.
    /// Deliberate removal is not one of the three: that is what
    /// <see cref="DashboardSettings.InstallHooksAtStart"/> is for.
    /// </para>
    /// <para>
    /// <strong>Complete short-circuits before the merge, and "the merge would change nothing" is
    /// not good enough.</strong> <c>SettingsFileWriter</c> decides whether anything changed by
    /// comparing the text it read against the text it renders, and rendering from
    /// <c>JsonNode</c> preserves neither comments nor the operator's spacing. So a no-op merge on a
    /// hand-formatted file still counts as a change and still strips every comment in it. The same
    /// reason <see cref="HookInstaller.Remove"/> reads before it writes.
    /// </para>
    /// <para>
    /// <strong>An unreadable opt-out is unknown, not consent (review of T1.32).</strong> When the
    /// dashboard's own settings file cannot be read, <c>SettingsStore</c> hands back defaults, and
    /// the default says install. But a recorded <c>--remove-hooks</c> lives in exactly the file
    /// that could not be read — so installing on the default would override the operator's stated
    /// decision on the strength of a file failure, and would do it silently. Only
    /// <see cref="SettingsLoadOutcome.Unreadable"/> refuses: a missing file is a first run, where
    /// the default is not standing in for anything and must install.
    /// </para>
    /// </remarks>
    /// <param name="presence">What <see cref="HookInstaller.Check"/> found.</param>
    /// <param name="installAtStart">The operator's setting.</param>
    /// <param name="settingsOutcome">
    /// How <paramref name="installAtStart"/> was arrived at — read, defaulted from a missing file,
    /// or defaulted from one that would not read.
    /// </param>
    public static bool Wanted(HookPresence presence, bool installAtStart, SettingsLoadOutcome settingsOutcome) =>
        settingsOutcome != SettingsLoadOutcome.Unreadable
        && installAtStart
        && presence.Problem is null
        && !presence.Complete;

    /// <summary>
    /// Reads Claude Code's settings and installs the hook if it is missing and wanted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One read, whatever happens next. <see cref="HookInstaller.Check"/> says what is there and
    /// logs the finding at the severity it deserves; this says what was done about it, and the two
    /// lines together are the whole of what a start records about hooks.
    /// </para>
    /// <para>
    /// <strong>The line names the events and the script path and no part of the file</strong>
    /// (T1.24). The event names are our own constants and the script path is in our own data
    /// folder; the operator's settings never appear in a log line.
    /// </para>
    /// </remarks>
    /// <param name="installer">The installer to ask and, when wanted, to drive.</param>
    /// <param name="installAtStart"><see cref="DashboardSettings.InstallHooksAtStart"/>.</param>
    /// <param name="settingsOutcome">How the dashboard's own settings load went.</param>
    /// <param name="logger">Where the one line goes.</param>
    /// <returns>The write outcome, or <see langword="null"/> when nothing was attempted.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static SettingsWriteResult? Run(
        HookInstaller installer,
        bool installAtStart,
        SettingsLoadOutcome settingsOutcome,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(logger);

        var presence = installer.Check();

        if (!Wanted(presence, installAtStart, settingsOutcome))
        {
            // Both refusals say so only when they bit — when the handler is missing and this is
            // the reason nothing goes back. A complete handler needs neither line.
            if (presence.Problem is null && !presence.Complete)
            {
                if (settingsOutcome == SettingsLoadOutcome.Unreadable)
                {
                    logger.Warning(
                        "The dashboard's hook is on {Events} of {Expected} events, but the " +
                        "dashboard's own settings file could not be read, so the " +
                        "\"installHooksAtStart\" opt-out is unknown and nothing was installed. Fix " +
                        "or delete that settings file, or run --install-hooks.",
                        presence.Events,
                        presence.Expected);
                }
                else if (!installAtStart)
                {
                    logger.Information(
                        "The dashboard's hook is on {Events} of {Expected} events and " +
                        "\"installHooksAtStart\" is false, so nothing was installed. Run --install-hooks " +
                        "to put it back.",
                        presence.Events,
                        presence.Expected);
                }
            }

            return null;
        }

        var result = installer.Install();

        if (result.Outcome == SettingsWriteOutcome.Written)
        {
            logger.Information(
                "The dashboard's hook was on {Found} of {Expected} events, so this start installed it " +
                "for {Script} on {Events}. Set \"installHooksAtStart\": false in the dashboard's " +
                "settings to stop that.",
                presence.Events,
                presence.Expected,
                installer.ScriptPath,
                string.Join(", ", HookEventNames.Accepted.Order(StringComparer.Ordinal)));
        }

        // Every other outcome is already reported by Install itself, at the severity it earns. A
        // failure leaves the dashboard running and showing sessions: it receives no events, which
        // is what it was already doing when this start began.
        return result;
    }

    /// <summary>
    /// Records what a hook switch decided, so the next start honours it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this, <c>--remove-hooks</c> is a no-op with extra steps.</strong> The
    /// operator removes their hooks, restarts, and finds them back — the application overriding a
    /// decision they stated explicitly. That is the worst outcome this whole change can produce, so
    /// the flag is part of the feature rather than a nicety.
    /// </para>
    /// <para>
    /// <strong>Only on success, and only when the value would change.</strong> A switch that failed
    /// decided nothing, and writing the flag then would record an intention the operator's file does
    /// not reflect. Writing only a differing value keeps <c>--install-hooks</c> from creating a
    /// settings file on a machine that has none purely to record the default.
    /// </para>
    /// <para>
    /// <strong>A settings file that will not read is left alone.</strong> The store hands back
    /// defaults for an unreadable file, and saving those would overwrite whatever the operator had
    /// written there with a fresh object. That is the same rule the hook path follows, applied to
    /// our own file: never write one you could not read.
    /// </para>
    /// <para>
    /// <strong>It never throws.</strong> The dashboard's own settings failing to save must not turn
    /// a switch that did its work into a failure — the hooks are already installed or removed by the
    /// time this runs, and the report the operator reads is about them.
    /// </para>
    /// </remarks>
    /// <param name="requested">The canonical switch, from <see cref="HookSwitches.Requested"/>.</param>
    /// <param name="exitCode">What the switch returned; anything but zero records nothing.</param>
    /// <param name="store">The dashboard's own settings.</param>
    /// <param name="logger">Where a failure to save is reported.</param>
    /// <param name="report">
    /// Where the operator is told what the flag now means — the same console the switch reports to.
    /// A removal that only said so in the log would leave the operator to discover at some later
    /// start that starts no longer install; the consequence belongs in the report they are reading.
    /// </param>
    /// <returns>Whether the flag was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requested"/>, <paramref name="store"/> or <paramref name="logger"/> is null.</exception>
    public static bool RecordSwitch(
        string requested,
        int exitCode,
        SettingsStore store,
        ILogger logger,
        Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        if (exitCode != 0)
        {
            return false;
        }

        var wanted = string.Equals(requested, HookSwitches.Install, StringComparison.OrdinalIgnoreCase);

        if (!wanted && !string.Equals(requested, HookSwitches.Remove, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var loaded = store.Load();

        if (loaded.Outcome == SettingsLoadOutcome.Unreadable)
        {
            logger.Warning(
                "{Switch} could not record \"installHooksAtStart\": {Problem}. The dashboard's own " +
                "settings file was left exactly as it is, so the next start may install the hook again.",
                requested,
                loaded.Problem);

            return false;
        }

        if (loaded.Settings.InstallHooksAtStart == wanted)
        {
            return false;
        }

        try
        {
            store.Save(loaded.Settings with { InstallHooksAtStart = wanted });

            logger.Information(
                "{Switch}: \"installHooksAtStart\" is now {Value} in the dashboard's own settings.",
                requested,
                wanted);

            report?.Invoke(wanted
                ? "Starts will install the hook again if it goes missing (\"installHooksAtStart\" is back on)."
                : "Starts will no longer install the hook (\"installHooksAtStart\" is now false). --install-hooks turns it back on.");

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(
                ex,
                "{Switch} could not write the dashboard's own settings, so " +
                "\"installHooksAtStart\" is unchanged. Claude Code's settings were still updated.",
                requested);

            return false;
        }
    }
}
