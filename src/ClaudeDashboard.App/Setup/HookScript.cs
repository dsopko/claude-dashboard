using System.IO;
using System.Text;
using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// The hook forwarder Claude Code runs — its text, and getting that text onto disk (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The script is the feature.</strong> Everything else in T1.28 exists to put these
/// twenty-odd lines in the data folder and name them in the operator's settings. It replaces eight
/// HTTP handlers that only worked while the dashboard was listening, and its whole contribution is
/// that it does nothing, quietly, when the dashboard is not there.
/// </para>
/// <para>
/// <strong>A compiled-in constant compared with the file byte for byte, rather than a version
/// stamp.</strong> A stamp can be right while the body is wrong — a hand-edit, a half-written
/// file, a partial restore all leave the stamp intact. Comparing the content catches all three,
/// and it is the same reasoning that has <c>SettingsFileWriter</c> compare <c>before</c> with
/// <c>after</c> instead of trusting a flag.
/// </para>
/// <para>
/// <strong>Rewritten at every start, so a fix in the build reaches an existing install.</strong>
/// This is the point of comparing at all. A script written once at install and never revisited is
/// a script whose bugs can never be fixed on a machine that already has it, and the operator would
/// have no step to run because they would have no reason to think one was needed.
/// </para>
/// <para>
/// <strong>The cost, and it is accepted: a hand-edited script is reverted at the next start.</strong>
/// The header says so in the file itself. The alternative — leaving a modified script alone — is
/// exactly the "can never be fixed" failure above, arrived at by being polite.
/// </para>
/// </remarks>
public static class HookScript
{
    /// <summary>
    /// <c>post-status.cmd</c>, in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written here as a constant rather than as an embedded resource so that it can be compared
    /// with the file on disk directly, which is the mechanism this whole type rests on.
    /// </para>
    /// <para>
    /// <strong>Line endings are LF in this source file and CRLF on disk.</strong> The repository
    /// stores <c>.cs</c> files with LF, so this literal carries LF; <see cref="Text"/> converts.
    /// A <c>.cmd</c> with LF endings is not reliably parsed by <c>cmd</c>, and the way it fails is
    /// a label that is not found — which prints to stderr, under our own redirect, and is
    /// therefore silent.
    /// </para>
    /// </remarks>
    private const string Body = """"
        @echo off
        rem ===========================================================================
        rem  Claude Dashboard - hook forwarder (issue #29).
        rem
        rem  GENERATED FILE. Written by ClaudeDashboard.App at every start, from the
        rem  constant in Setup/HookScript.cs. AN EDIT HERE IS REVERTED AT THE NEXT
        rem  START - change HookScript.cs instead. That is deliberate: a script that
        rem  could not be replaced would be a script whose bugs could never be fixed
        rem  on a machine that already had it.
        rem
        rem  WHAT IT DOES. Claude Code runs this once per hook event and puts the
        rem  event's JSON on our stdin. If listening.txt is beside this file, a
        rem  dashboard is bound to the port it names, and the payload goes there. If
        rem  it is not, there is no dashboard and this exits having opened nothing.
        rem  That second case is the whole point: the hook stays installed while the
        rem  dashboard is closed, instead of printing an error on every turn.
        rem
        rem  IT PRINTS NOTHING, ON EVERY PATH, AND THAT IS A REQUIREMENT.
        rem  On UserPromptSubmit and SessionStart - two of the eight events we
        rem  register - Claude Code adds a hook's stdout to the model's context as if
        rem  the operator had typed it. One stray line therefore alters every prompt
        rem  in every session, and NOTHING IN THE TRANSCRIPT SHOWS IT. It is not a
        rem  crash and it cannot be seen from the session.
        rem
        rem  The redirect is on the "call" below and not on the individual lines. One
        rem  redirect covers every branch, including the branches that only run when
        rem  something has already gone wrong - which are exactly the branches a
        rem  per-line redirect gets wrong, because they are the ones nobody remembers.
        rem  A branch added later is covered without anybody having to be told.
        rem
        rem  IT ALWAYS EXITS 0. Exit 1 is reported to the operator as a hook error.
        rem  Exit 2 BLOCKS THE TURN, and the dashboard blocking a Claude turn breaks
        rem  the pure-observer rule outright.
        rem
        rem  THE "exit /b 0" AFTER THE CALL IS THE WHOLE OF THAT GUARANTEE. The
        rem  "exit /b" lines inside :post cannot reach the process: the call returns
        rem  and that line overrides whatever they set. Measured - changing one of
        rem  them to "exit /b 1" fails no test, because it cannot be observed;
        rem  changing the outer one fails twenty-three. So do not read the inner
        rem  zeros as the safety, and do not delete the outer line on the grounds
        rem  that every branch already exits 0.
        rem
        rem  TIMEOUTS: --connect-timeout 1 --max-time 2. Measured on this machine on
        rem  2026-08-30: a post to a free loopback port cost 1.09 s per invocation,
        rem  and 0.34 s with --connect-timeout 0.25 - so the time is the connect
        rem  TIMING OUT rather than being refused, which is not the normal loopback
        rem  behaviour and is probably a firewall dropping the SYN. On a machine that
        rem  refuses fast the cost is near zero. One second is still the right choice:
        rem  the cost falls only between a hard kill and the next start, the hook is
        rem  async so nothing waits for it, and a shorter timeout would risk dropping
        rem  a real event to buy nothing anybody can see. --max-time must exceed
        rem  --connect-timeout, or a slow connect leaves no budget to send the body.
        rem ===========================================================================

        call :post >nul 2>nul
        exit /b 0

        :post
        setlocal EnableExtensions EnableDelayedExpansion

        rem  No announcement means no dashboard. Nothing is opened and nothing is said.
        if not exist "%~dp0listening.txt" exit /b 0

        set "PORT="
        set /p PORT=<"%~dp0listening.txt"
        if not defined PORT exit /b 0

        rem  THE URL IS BUILT FROM AN INTEGER, NEVER FROM THE FILE'S TEXT. set /a reads
        rem  PORT by name and can execute nothing, and a value that does not survive
        rem  the round trip is not a port. Delayed expansion throughout, so a hostile
        rem  value is substituted after the line has been parsed and cannot become
        rem  syntax. Measured with a shell metacharacter payload in listening.txt:
        rem  exit 0, both streams empty, nothing launched.
        set /a "BOUND=PORT"
        if not "!BOUND!"=="!PORT!" exit /b 0
        if !BOUND! LSS 1 exit /b 0
        if !BOUND! GTR 65535 exit /b 0

        rem  curl.exe by absolute path and not by name: an unqualified curl.exe is
        rem  shadowed by anything earlier on PATH, and this one is handed the
        rem  operator's prompts. Two calls rather than one with an assembled argument
        rem  string - a header built up in a variable is re-parsed when it expands,
        rem  and the token would be the text doing the parsing. Keep the flags equal.
        if defined CLAUDE_DASHBOARD_TOKEN (
            "%SystemRoot%\System32\curl.exe" -s -o nul --connect-timeout 1 --max-time 2 -H "Content-Type: application/json" -H "X-Dashboard-Token: !CLAUDE_DASHBOARD_TOKEN!" --data-binary @- "http://127.0.0.1:!BOUND!/hook"
        ) else (
            "%SystemRoot%\System32\curl.exe" -s -o nul --connect-timeout 1 --max-time 2 -H "Content-Type: application/json" --data-binary @- "http://127.0.0.1:!BOUND!/hook"
        )

        exit /b 0
        """";

    /// <summary>The script exactly as it belongs on disk, with CRLF line endings.</summary>
    public static string Text { get; } = Body.ReplaceLineEndings("\r\n");

    /// <summary>
    /// Puts <see cref="Text"/> at <see cref="DashboardPaths.HookScriptFile"/> unless it is already
    /// there, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Temp-then-rename, and here that is not tidiness.</strong> Claude Code executes this
    /// file, and <c>cmd</c> reads a batch file incrementally rather than in one gulp — so a
    /// truncate-and-write leaves a window in which the operator's next turn runs half a program.
    /// A torn <c>.cmd</c> is a torn <em>executable</em>.
    /// </para>
    /// <para>
    /// <strong>It will genuinely fail sometimes, which is why nothing here throws.</strong> While
    /// <c>cmd</c> is running the script it holds the file open, so the rename can lose to a sharing
    /// violation on a busy machine. The next start tries again, and in the meantime the script
    /// already on disk is the one that runs — which is the old version, not a broken one.
    /// </para>
    /// </remarks>
    /// <returns>Whether the file now holds <see cref="Text"/>.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool EnsureWritten(DashboardPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        if (Matches(paths))
        {
            return true;
        }

        var temporary = $"{paths.HookScriptFile}{ListeningFile.TemporarySuffix}{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(temporary, Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, paths.HookScriptFile, overwrite: true);

            logger.Information("Wrote the hook forwarder to {Script}.", paths.HookScriptFile);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(
                ex,
                "Could not write {Script}. The copy already there is the one Claude Code will run, and " +
                "this is retried at the next start.",
                paths.HookScriptFile);

            TryDelete(temporary);

            return false;
        }
    }

    /// <summary>Whether the file on disk already holds exactly <see cref="Text"/>.</summary>
    /// <remarks>
    /// Ordinal, over the whole text. The line endings are part of what is being asserted, so a
    /// comparison that normalised them would leave an LF copy in place for ever.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static bool Matches(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            return File.Exists(paths.HookScriptFile)
                && string.Equals(File.ReadAllText(paths.HookScriptFile), Text, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing sweeps this, and that is a considered omission rather than an oversight: it
            // sits in our own data folder under a name that says what it is, and a sweep would be
            // more code than the residue it collects.
        }
    }
}
