using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// Reads, writes and deletes <c>listening.txt</c> — the port a dashboard is bound to
/// <strong>right now</strong> (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file's absence is a feature, and it is the whole feature.</strong>
/// <c>post-status.cmd</c> runs on every hook event in every session. Finding no file, it exits
/// without opening a socket, which is what lets the hook stay installed while the dashboard is
/// closed — and closing that error on every turn is the point of issue #29.
/// </para>
/// <para>
/// <strong>It is not <c>port.txt</c> and must never be merged with it.</strong> <c>port.txt</c>
/// answers "where was this user last?", and it answers for ever, which is what makes it the first
/// attempt of Impl §3.1 and the only thing that tells a second launch where the first one is.
/// This answers "is a dashboard listening, and where?", and it is deleted on the way out. Giving
/// one file both jobs breaks §3.1 and §5.3 in silence.
/// </para>
/// <para>
/// <strong>Written temp-then-rename, unlike <c>port.txt</c>.</strong> The difference is who reads
/// it: <c>port.txt</c> is read by our own start-up, which tolerates any answer it does not like by
/// falling through to the derivation. This is read by a batch file on somebody else's prompt, and
/// a torn read there is a URL built from half a number.
/// <see cref="File.Move(string, string, bool)"/> is atomic on one volume, so a reader sees the
/// whole old file or the whole new one.
/// </para>
/// <para>
/// <strong>Never fatal, in any direction.</strong> A dashboard that could not write this file
/// still runs and still shows the operator their sessions through the window; it simply receives
/// nothing until the next start fixes it. Refusing to start would trade the whole application for
/// one text file.
/// </para>
/// <para>
/// <strong>Residual, and it is issue #29's.</strong> A hard kill leaves the file behind naming the
/// last bound port. Until the next start the script posts to whatever holds that port, and hook
/// payloads carry the operator's prompts — the same exposure Impl §9.3 already records for a hard
/// kill. <see cref="Write"/> overwrites unconditionally on every start, which is what closes it.
/// </para>
/// </remarks>
public static class ListeningFile
{
    /// <summary>What a half-finished write is called, before the move that commits it.</summary>
    /// <remarks>
    /// Distinctive enough to recognise beside the real file, and in the same directory so the move
    /// stays on one volume. A crash between the write and the move leaves one of these and leaves
    /// the real file untouched, which is the trade being made.
    /// </remarks>
    internal const string TemporarySuffix = ".dashboard-tmp-";

    /// <summary>The port a dashboard is listening on, or null when none is.</summary>
    /// <remarks>
    /// <para>
    /// Null covers every way this can fail to answer — no file, an empty file, text that is not a
    /// number, a number outside the port range, a folder that cannot be read. They are one case to
    /// a caller: <em>nothing is listening</em>.
    /// </para>
    /// <para>
    /// <strong>Nothing in the application reads this in production.</strong> The reader is
    /// <c>post-status.cmd</c>, which is a batch file and cannot call this. It is here so a test can
    /// assert what was written without restating the parsing rules, and so the two readers can be
    /// compared against each other.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static int? Read(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            var text = File.ReadAllText(paths.ListeningFile).Trim();

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535
                    ? port
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Announces <paramref name="port"/>. Returns false if it could not be written.</summary>
    /// <remarks>
    /// <para>
    /// <strong>No trailing newline, and the script does not need one either.</strong> Measured:
    /// <c>set /p</c> strips a trailing LF or CRLF, so a hand-edited file with a line ending still
    /// works. It does not strip a leading space, and that case is rejected rather than repaired —
    /// this is a file we write, and being strict about the one number in it is what keeps a
    /// malformed value out of a URL.
    /// </para>
    /// <para>Overwrites unconditionally, which is what corrects a file a crash left behind.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static bool Write(DashboardPaths paths, int port)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var temporary = $"{paths.ListeningFile}{TemporarySuffix}{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(
                temporary,
                port.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.Move(temporary, paths.ListeningFile, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);

            return false;
        }
    }

    /// <summary>Withdraws the announcement. Returns false if the file is still there afterwards.</summary>
    /// <remarks>
    /// <para>
    /// A file that was never there is a success, not a failure: this runs on several exit paths and
    /// more than one of them can run for the same exit, so "it is gone" is the outcome that matters
    /// and "I am the one who removed it" is not.
    /// </para>
    /// <para>
    /// <strong>This touches <c>listening.txt</c> and nothing else.</strong> Deleting
    /// <c>port.txt</c> here would be invisible until the next start, and would cost the port
    /// continuity of Impl §3.1 and the <c>POST /show</c> hand-over of §5.3.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static bool Delete(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return TryDelete(paths.ListeningFile);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
