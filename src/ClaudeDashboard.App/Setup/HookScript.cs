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
        call :post >nul 2>nul
        exit /b 0
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
        rem  dashboard is bound to the port on its first line and accepts the token on
        rem  its second, and the payload goes there with that token. If it is not,
        rem  there is no dashboard and this exits having opened nothing. That second
        rem  case is the whole point: the hook stays installed while the dashboard is
        rem  closed, instead of printing an error on every turn.
        rem
        rem  THE TOKEN COMES FROM THE FILE, NEVER FROM THE ENVIRONMENT (T1.48, issue
        rem  #57). A Claude Code session gets its environment once, at launch, so a
        rem  token held there cut off every session started before it was set or
        rem  changed. The dashboard makes a new token at every start and this reads it
        rem  fresh at every event, so a session keeps reporting through any number of
        rem  dashboard restarts. CLAUDE_DASHBOARD_TOKEN is retired and not read.
        rem
        rem  IT PRINTS NOTHING, ON EVERY PATH, AND THAT IS A REQUIREMENT.
        rem  On UserPromptSubmit and SessionStart - two of the eight events we
        rem  register - Claude Code adds a hook's stdout to the model's context as if
        rem  the operator had typed it. One stray line therefore alters every prompt
        rem  in every session, and NOTHING IN THE TRANSCRIPT SHOWS IT. It is not a
        rem  crash and it cannot be seen from the session.
        rem
        rem  The redirect is on the "call" above and not on the individual lines. One
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
        rem  THE FIRST THREE LINES NEVER CHANGE, AND THEY ARE FIRST ON PURPOSE (T1.48).
        rem  cmd reads a batch file from disk as it runs, by byte offset. When a new
        rem  build rewrites this file while a hook is inside :post, that hook returns
        rem  from the call and reads its next line from the NEW file at the OLD offset.
        rem  With these three lines first and identical in every build, that offset
        rem  lands on "exit /b 0" in both. RESIDUAL: the running hook may also read
        rem  a fragment of the new :post at an old offset before it returns. Its output
        rem  is still under the call's redirect, and its exit code is still the line
        rem  above, so the worst case is one lost event, once, at an upgrade. The
        rem  rewrite from a build older than T1.48 has no fixed prologue to land on and
        rem  carries the same residual once. Nothing heavier is built for it.
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

        :post
        setlocal EnableExtensions EnableDelayedExpansion

        rem  No announcement means no dashboard. Nothing is opened and nothing is said.
        if not exist "%~dp0listening.txt" exit /b 0

        rem  Line 1 is the port, line 2 the token: two set /p calls from ONE redirected
        rem  block read consecutive lines, and set /p splits at CRLF, which is what the
        rem  dashboard writes. set /p assigns the text as data - nothing in it is
        rem  parsed - and a variable read later with !...! is not expanded again.
        set "PORT="
        set "TOKEN="
        < "%~dp0listening.txt" (
            set /p "PORT="
            set /p "TOKEN="
        )
        if not defined PORT exit /b 0
        if not defined TOKEN exit /b 0

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

        rem  THE TOKEN IS SENT ONLY IF IT IS EXACTLY 43 CHARACTERS OF A-Z a-z 0-9 - _.
        rem  Anything running as the operator can edit listening.txt, and the token
        rem  goes into a request header. Length: character 43 must exist and 44 must
        rem  not. Characters: a for /f whose delimiters are exactly those 64 leaves
        rem  nothing to iterate over a valid token, so any other character runs the
        rem  body, and the body refuses. eol is "_", itself a delimiter, so no token
        rem  can start with it; the default eol ";" would skip a line whose first
        rem  bad character is ";" and let it through. NOT findstr: its [a-z] is not
        rem  an ASCII range, and it would start a process on every hook. Measured
        rem  with every printable ASCII character outside the set, a tab, and every
        rem  byte 0x80-0xFF, each at three positions: all refused, nothing printed.
        rem
        rem  ONE BYTE GETS PAST THAT CHECK: A BARE LF (T1.48 review). set /p ends a
        rem  line only at CRLF, so an LF inside line 2 stays in TOKEN and counts toward
        rem  the 43; for /f splits its string at the LF, both halves are pure
        rem  delimiters, and the body never runs. The line after it closes that: the
        rem  first line for /f sees of "-TOKEN" must be the whole of it. Measured on
        rem  every byte 0x01-0xFF at one position: LF is the only one for /f treats as
        rem  a line break - a lone CR is an ordinary non-delimiter and is refused
        rem  above, and a NUL cuts the value short and fails the length test.
        if "!TOKEN:~42,1!"=="" exit /b 0
        if not "!TOKEN:~43,1!"=="" exit /b 0
        for /f "eol=_ delims=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_" %%R in ("-!TOKEN!") do exit /b 0
        for /f "delims=" %%L in ("-!TOKEN!") do if not "%%L"=="-!TOKEN!" exit /b 0

        rem  curl.exe by absolute path and not by name: an unqualified curl.exe is
        rem  shadowed by anything earlier on PATH, and this one is handed the
        rem  operator's prompts.
        "%SystemRoot%\System32\curl.exe" -s -o nul --connect-timeout 1 --max-time 2 -H "Content-Type: application/json" -H "X-Dashboard-Token: !TOKEN!" --data-binary @- "http://127.0.0.1:!BOUND!/hook"

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
    /// <strong>It can fail, which is why nothing here throws.</strong> Not because of <c>cmd</c>:
    /// the T1.48 review measured <c>MoveFileEx(REPLACE_EXISTING)</c> over a <c>.cmd</c> that
    /// <c>cmd</c> was running, and it succeeded three times in three, because <c>cmd</c> does not
    /// hold the file open between lines. What can hold it is anything else with a handle that does
    /// not share delete — a scanner, an indexer, an editor — or a folder the process cannot write.
    /// The copy already on disk is then the one that runs, which is the old version, not a broken
    /// one. <see cref="EnsureWrittenAtStart"/> retries at start and says so if it cannot.
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

    /// <summary>How many times a start tries to put the script in place before it says so.</summary>
    public const int StartAttempts = 3;

    /// <summary>The pause between those attempts.</summary>
    public static readonly TimeSpan StartPause = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <see cref="EnsureWritten"/> as a start runs it: a few tries, then one Error line if the script
    /// on disk is still not this build's (the T1.48 review).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a start cares more than the installer does.</strong> Since T1.48 the dashboard
    /// refuses a hook without this run's token, and only this build's script sends one. A start
    /// whose rewrite failed would still announce the port, and every hook from the old script
    /// would be refused for the whole run — issue #57's symptom, silently, for one run. So a
    /// failure here is retried briefly, and if it persists it is an Error that names the effect.
    /// </para>
    /// <para>
    /// It never refuses to start. The window still shows what the Registry holds, and the next
    /// start tries again.
    /// </para>
    /// </remarks>
    /// <param name="paths">The data folder.</param>
    /// <param name="logger">Where each failed try (Warning) and the final failure (Error) go.</param>
    /// <param name="attempts">How many tries; <see cref="StartAttempts"/> in the product.</param>
    /// <param name="wait">How to pause between tries; a thread sleep of <see cref="StartPause"/> in the product.</param>
    /// <returns>Whether the file now holds <see cref="Text"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="logger"/> is null.</exception>
    public static bool EnsureWrittenAtStart(
        DashboardPaths paths,
        ILogger logger,
        int attempts = StartAttempts,
        Action? wait = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (EnsureWritten(paths, logger))
            {
                return true;
            }

            if (attempt < attempts)
            {
                (wait ?? (() => Thread.Sleep(StartPause)))();
            }
        }

        logger.Error(
            "Could not replace {Script} after {Attempts} attempts. Hooks from the old script are refused " +
            "until the next start: it sends no token, and this dashboard requires one.",
            paths.HookScriptFile,
            attempts);

        return false;
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
