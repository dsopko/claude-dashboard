using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>What the start check found in Claude Code's settings.</summary>
/// <param name="Events">How many of the accepted events carry our handler.</param>
/// <param name="Expected">How many should.</param>
/// <param name="Foreign">Script paths that look like ours but name another data folder.</param>
/// <param name="Problem">Why the file could not be read, when it could not be.</param>
/// <param name="ClaudeCodeInstalled">
/// Whether Claude Code's configuration directory exists at all (T1.33, issue #42). The check is
/// the directory and only the directory — the app never goes looking for other software — and
/// its absence is the one reliable sign this machine has never had Claude Code, which is what
/// stops a start creating <c>~/.claude</c> on it. Defaults to <see langword="true"/> so that a
/// presence built by hand describes the ordinary machine unless it says otherwise.
/// </param>
public readonly record struct HookPresence(
    int Events,
    int Expected,
    IReadOnlyList<string> Foreign,
    string? Problem = null,
    bool ClaudeCodeInstalled = true)
{
    /// <summary>Whether every accepted event carries our handler.</summary>
    public bool Complete => Problem is null && Events == Expected;
}

/// <summary>
/// Writes the dashboard's hook handler into Claude Code's settings, takes it out, and reports
/// whether it is there (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This replaces <c>HookLifecycle</c>'s register-at-start and unregister-at-quit, and the
/// replacement is the point of the task.</strong> A hook that only existed while the dashboard was
/// listening had two holes the operator lived with daily: a Claude Code session already open kept
/// the old settings until it restarted, and a dashboard that was killed left the handlers behind
/// naming a dead port. Neither is closable from inside that design.
/// </para>
/// <para>
/// <strong>A start now calls <see cref="Install"/>, and until T1.32 nothing did (issue #39).</strong>
/// This paragraph said "nothing here runs by itself" and that the running dashboard called
/// <see cref="Check"/> and nothing else. It was true and it was the defect: registration had become
/// an install step with nothing left running the install step, so a user who had never opened a
/// terminal received no events for ever. <see cref="StartupHookInstall"/> is what decides — it
/// installs only what is missing, only when the file read cleanly, and only while the operator has
/// not opted out. <see cref="Remove"/> is still reached from its switch alone, and nothing is
/// written on the way out.
/// </para>
/// <para>
/// <strong>Why the installer survives at all.</strong> First-run setup is T10.2 and does not exist.
/// Without <c>--install-hooks</c> the merge would be code nobody could run and a new user would
/// have no way to get hooks at all — so the switch is the operator's tool today and T10.2's call
/// site tomorrow. T1.32 gave the merge a second caller in the meantime, which is what closed the
/// "no way to get hooks at all" half of that sentence.
/// </para>
/// </remarks>
public sealed class HookInstaller
{
    private readonly ClaudeCodePaths _claude;
    private readonly DashboardPaths _paths;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    /// <summary>Creates the installer over Claude Code's settings file.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public HookInstaller(ClaudeCodePaths claude, DashboardPaths paths, IClock clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _claude = claude;
        _paths = paths;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The interpreter the handler names — <c>cmd.exe</c>, by absolute path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absolute, because nothing expands a variable in the exec form</strong>, and
    /// <strong>resolved rather than written out</strong>, because a machine whose Windows
    /// directory is not <c>C:\Windows</c> is unusual and not impossible.
    /// </para>
    /// <para>
    /// A <c>.cmd</c> file is not an executable image, so <c>CreateProcess</c> cannot run one. The
    /// exec form spawns what it is given, which means the interpreter has to be named explicitly —
    /// that is the one thing <c>cmd.exe</c> is doing here, and it is not a shell in the sense
    /// §9.2 warns about: it is the program that runs <c>.cmd</c> files, chosen by us and identical
    /// on every machine.
    /// </para>
    /// </remarks>
    public static string Interpreter => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>The script the handler runs.</summary>
    public string ScriptPath => _paths.HookScriptFile;

    /// <summary>
    /// Where Claude Code's configuration lives — the directory whose absence means Claude Code is
    /// not installed (T1.33). A pass-through like <see cref="ScriptPath"/>, so the one line that
    /// reports the refusal can name the path it checked without a second path computation.
    /// </summary>
    public string ClaudeConfigDirectory => _claude.ConfigDirectory;

    /// <summary>
    /// Writes the script and merges the command handler into Claude Code's settings.
    /// </summary>
    /// <remarks>
    /// The script first. A handler naming a file that is not there is the one arrangement worse
    /// than no handler: Claude Code would run <c>cmd</c> against a missing path on every event, and
    /// <c>cmd</c> says so on stderr — which is exactly the noise issue #29 exists to remove.
    /// </remarks>
    /// <returns>The outcome of the settings write.</returns>
    public SettingsWriteResult Install()
    {
        _paths.TryEnsureCreated(out _);
        HookScript.EnsureWritten(_paths, _logger);

        // Claude Code's directory too (T1.33). An operator running --install-hooks on a machine
        // without Claude Code is asking, and the ask must not die on a missing parent folder —
        // measured before this line existed: the settings write failed and the switch exited 1,
        // so "the directory is created for them as today" had never been true. The START path
        // cannot reach here on such a machine — StartupHookInstall.Wanted refuses first — so
        // this creates ~/.claude only on an explicit ask. Idempotent when it already exists.
        Directory.CreateDirectory(_claude.ConfigDirectory);

        var writer = NewWriter();
        writer.SweepAbandonedTemporaries();

        var script = ScriptPath;
        var result = writer.Modify(
            settings => HookRegistration.Register(settings, Interpreter, script),
            _clock.Now);

        Report(result, "installed in");

        return result;
    }

    /// <summary>
    /// Takes out both shapes — the command handler, and the legacy HTTP handlers with their URL
    /// allowlist entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both shapes, so that this is a complete migration tool.</strong> An operator moving
    /// off a build that registered HTTP handlers runs this and then <c>--install-hooks</c>, and is
    /// finished. Leaving the old handlers would leave the error on every turn that the whole task
    /// is about.
    /// </para>
    /// <para>
    /// <strong>The script file is left on disk.</strong> Removing the handler is what stops Claude
    /// Code running it; deleting the file as well would mean a later <c>--install-hooks</c> had to
    /// put it back, and would make this switch destructive for no gain. It is written afresh at
    /// every start in any case.
    /// </para>
    /// </remarks>
    /// <returns>The write outcome, and what was taken out by name.</returns>
    public (SettingsWriteResult Result, HookRemoval Removed) Remove()
    {
        var script = ScriptPath;

        // NOTHING OF OURS MEANS NOTHING IS WRITTEN, AND THAT IS NOT AN OPTIMISATION.
        //
        // SettingsFileWriter decides "did anything change" by comparing the text it read with the
        // text it renders — and rendering does not preserve comments or the operator's formatting,
        // because JsonNode carries neither. So a merge that removed nothing would still count as a
        // change on any hand-formatted file, and running --remove-hooks against a settings file
        // this dashboard had never touched would silently strip every comment in it.
        //
        // Costs one extra read of a file this switch is about to read anyway.
        if (!AnythingOfOursIn(script, out var problem))
        {
            return problem is null
                ? (new SettingsWriteResult(SettingsWriteOutcome.NothingToDo), HookRemoval.None)
                : (new SettingsWriteResult(SettingsWriteOutcome.Unreadable, 1, problem), HookRemoval.None);
        }

        var removed = HookRemoval.None;

        var result = NewWriter().Modify(
            settings =>
            {
                var paths = HookRegistration.Unregister(settings, script);
                var legacy = HookRegistration.RemoveLegacyHttp(settings);

                // Assigned rather than accumulated: Modify calls this once per attempt, against
                // freshly read content, so an accumulating merge would double-count a retry.
                removed = legacy with { ScriptPaths = paths };
            },
            _clock.Now);

        Report(result, "removed from");

        return (result, removed);
    }

    /// <summary>
    /// Reads Claude Code's settings and says whether our handler is there. Writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this, a hook removed by anything at all is undetectable.</strong> The
    /// dashboard sits there receiving no events, and that looks exactly like a quiet day. It is the
    /// same class of failure as a port held by a stranger, which <c>IngressStatus</c> already
    /// speaks up about, and it deserves the same treatment.
    /// </para>
    /// <para>
    /// <strong>A warning names both paths — the one installed and the one this process expects.</strong>
    /// <c>CLAUDE_DASHBOARD_HOME</c> moves the data folder, so a hook installed under one root and a
    /// dashboard started under another is a real configuration rather than a corruption. A warning
    /// naming only one path cannot explain it, and would send the operator looking for a missing
    /// entry that is in fact right there under another name.
    /// </para>
    /// <para>
    /// <strong>It logs the handler's presence, never the file's contents.</strong> Claude Code's
    /// settings are the operator's, and T1.24's rule stands.
    /// </para>
    /// </remarks>
    public HookPresence Check()
    {
        var expected = HookEventNames.Accepted.Count;
        var script = ScriptPath;

        // The one existence check that says whether this machine has Claude Code at all (T1.33).
        // The same _claude every other read here goes through — a second path computation would
        // be a second answer to the same question. Measured once and threaded through every arm,
        // though only the absent-file arm can carry false: a file cannot be found, read, or fail
        // to parse inside a directory that is not there.
        var claudeCodeInstalled = Directory.Exists(_claude.ConfigDirectory);

        string text;

        try
        {
            if (!File.Exists(_claude.UserSettingsFile))
            {
                var absent = new HookPresence(0, expected, [], ClaudeCodeInstalled: claudeCodeInstalled);
                ReportPresence(absent);

                return absent;
            }

            text = File.ReadAllText(_claude.UserSettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var unreadable = new HookPresence(0, expected, [], ex.Message, claudeCodeInstalled);
            ReportPresence(unreadable);

            return unreadable;
        }

        HookPresence presence;

        try
        {
            var settings = HookRegistration.Parse(text);

            presence = new HookPresence(
                HookRegistration.CountInstalled(settings, script),
                expected,
                HookRegistration.ForeignScriptPaths(settings, script),
                ClaudeCodeInstalled: claudeCodeInstalled);
        }
        catch (System.Text.Json.JsonException ex)
        {
            presence = new HookPresence(0, expected, [], ex.Message, claudeCodeInstalled);
        }

        ReportPresence(presence);

        return presence;
    }

    /// <summary>Whether the file holds anything either removal rule would take out.</summary>
    /// <remarks>
    /// Asked of a throwaway parse, so the answer comes from the rules themselves rather than from a
    /// second description of them that could drift. An unreadable file is reported through
    /// <paramref name="problem"/> rather than as "nothing there": the two are different diagnoses,
    /// and treating a broken file as an empty one would tell the operator their hooks were already
    /// gone.
    /// </remarks>
    private bool AnythingOfOursIn(string script, out string? problem)
    {
        problem = null;

        string text;

        try
        {
            if (!File.Exists(_claude.UserSettingsFile))
            {
                return false;
            }

            text = File.ReadAllText(_claude.UserSettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;

            return false;
        }

        try
        {
            var probe = HookRegistration.Parse(text);

            return HookRegistration.Unregister(probe, script).Count > 0
                || HookRegistration.RemoveLegacyHttp(probe).Total > 0;
        }
        catch (System.Text.Json.JsonException ex)
        {
            problem = ex.Message;

            return false;
        }
    }

    private SettingsFileWriter NewWriter() => new(_claude.UserSettingsFile);

    /// <summary>Says what the check found, at the severity the finding deserves.</summary>
    /// <remarks>
    /// <para>
    /// Three cases and three sentences. "Cannot read the file" is a different diagnosis from "the
    /// handler is not there", and both are different from a handler installed under another data
    /// folder — which is a configuration the operator can see the whole of once both paths are in
    /// front of them.
    /// </para>
    /// <para>
    /// <strong>It states the finding and no longer prescribes the remedy (issue #39).</strong> Both
    /// incomplete cases ended "Run --install-hooks", which was right while nothing else could
    /// install. A start now may install by itself, so the same line would tell the operator to do
    /// what the next line says has already been done — and would still be wrong the other way round
    /// if they had opted out. The remedy belongs to <see cref="StartupHookInstall"/>, which is the
    /// only thing that knows which of the two happened.
    /// </para>
    /// <para>
    /// <strong>The absent-handler finding drops to Information on a machine with no Claude Code
    /// (T1.33 review, unpinning the brief's "today's severity" for exactly this case).</strong>
    /// There the missing hook is the expected state, and a Warning-filtered reader would see the
    /// alarm while its explanation — the refusal line — sat one level below the filter. The two
    /// lines now travel at one level, and the Warning is reserved for the machine where a missing
    /// handler is genuinely wrong.
    /// </para>
    /// </remarks>
    private void ReportPresence(HookPresence presence)
    {
        if (presence.Problem is { } problem)
        {
            _logger.Warning(
                "Could not read Claude Code's settings at {File} to check the dashboard's hook is " +
                "installed: {Problem}. Nothing was written. If the hook is missing, this dashboard " +
                "receives no events and looks exactly like a quiet day.",
                _claude.UserSettingsFile,
                problem);

            return;
        }

        if (presence.Complete)
        {
            _logger.Debug(
                "Claude Code's hook for {Script} is installed on all {Events} events.",
                ScriptPath,
                presence.Events);

            return;
        }

        if (presence.Foreign.Count > 0)
        {
            _logger.Warning(
                "Claude Code's settings carry the dashboard's hook on {Events} of {Expected} events for " +
                "{Script}, but they do run {Foreign}. That is a different data folder, so check " +
                "{HomeVariable}.",
                presence.Events,
                presence.Expected,
                ScriptPath,
                presence.Foreign,
                DashboardPaths.HomeVariable);

            return;
        }

        // Information, not Warning, when the machine has no Claude Code at all (T1.33 review).
        // The missing hook is the EXPECTED state there, and a reader filtering at Warning would
        // see this alarm with its explanation — StartupHookInstall's refusal line — one level
        // down where the filter hides it. On a machine that has Claude Code, a missing handler
        // is genuinely wrong and stays a Warning.
        _logger.Write(
            presence.ClaudeCodeInstalled
                ? Serilog.Events.LogEventLevel.Warning
                : Serilog.Events.LogEventLevel.Information,
            "Claude Code's settings carry the dashboard's hook on {Events} of {Expected} events for " +
            "{Script}. Until it is installed this dashboard receives nothing, which looks exactly like " +
            "a quiet day.",
            presence.Events,
            presence.Expected,
            ScriptPath);
    }

    private void Report(SettingsWriteResult result, string what)
    {
        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                _logger.Information(
                    "Hooks {What} Claude Code at {File} for {Script} (attempt {Attempts}). Backup: {Backup}.",
                    what,
                    _claude.UserSettingsFile,
                    ScriptPath,
                    result.Attempts,
                    result.BackupPath ?? "(none needed)");
                break;

            case SettingsWriteOutcome.NothingToDo:
                _logger.Information("Claude Code's settings already say what they should; nothing written.");
                break;

            case SettingsWriteOutcome.Abandoned:
                _logger.Warning(
                    "Gave up after {Attempts} attempts to update {File}: {Problem}. The file is unchanged.",
                    result.Attempts,
                    _claude.UserSettingsFile,
                    result.Problem);
                break;

            default:
                _logger.Error(
                    "Could not read {File}: {Problem}. It has been left exactly as it is.",
                    _claude.UserSettingsFile,
                    result.Problem);
                break;
        }
    }
}
