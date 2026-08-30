using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// Lets a windowed process write to the console it was launched from (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A <c>WinExe</c> has no standard output, and the switches have to report.</strong> The
/// project is <c>OutputType WinExe</c> so that a tray app does not flash a console window at
/// logon. The cost is that <c>--install-hooks</c> run from a terminal writes to nothing at all:
/// <c>Console.Out</c> is a sink that discards. That is unacceptable for a switch whose whole job is
/// to say what it changed in the operator's settings file.
/// </para>
/// <para>
/// <strong><c>AttachConsole(ATTACH_PARENT_PROCESS)</c>, never <c>AllocConsole</c>.</strong>
/// Attaching borrows the console the operator is already looking at. Allocating would open a new
/// window, which flashes and vanishes when the process exits — so the report would be unreadable
/// and the switch unscriptable, which defeats the purpose it is being kept for (T10.2's call
/// site).
/// </para>
/// <para>
/// <strong>Degrading is a real path, not a formality.</strong> Launched from Explorer, from a
/// scheduled task, or by a T10.2 that redirects its own streams, there is no parent console to
/// attach to. Then the log is the only report, which is the same channel every other startup
/// decision in this application uses, and the exit code still says whether it worked.
/// </para>
/// <para>
/// <strong>Attached before anything touches <see cref="Console"/>.</strong> .NET creates and caches
/// the standard streams on first use, so a single stray write before the attach would fix the
/// discarding sink in place for the life of the process. <see cref="Console.SetOut"/> afterwards
/// makes that ordering unnecessary rather than merely unlikely.
/// </para>
/// </remarks>
internal static class ConsoleReport
{
    /// <summary>The parent process, as <c>AttachConsole</c> spells it.</summary>
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>
    /// Attaches to the launching console and points <see cref="Console.Out"/> at it.
    /// </summary>
    /// <returns>Whether there was a console to attach to.</returns>
    public static bool TryAttach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return false;
            }

            // AutoFlush, because this process exits immediately afterwards and a buffered final
            // line would be the one the operator most needed to read.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}
