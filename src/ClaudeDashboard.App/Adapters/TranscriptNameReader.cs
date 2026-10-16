using System.Buffers;
using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// Reads a session's given name and generated title from the end of its transcript (issue #161).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it reads at all.</strong> The hook's <c>session_title</c> carries only a given name. The title
/// Claude Code generates for a session nobody named is written only to the transcript, as
/// <c>{"type":"ai-title","aiTitle":"…","sessionId":"…"}</c>; a given name is there too, as
/// <c>{"type":"custom-title","customTitle":"…","sessionId":"…"}</c>. Claude Code writes both again and again, so the
/// newest of each is near the end of the file.
/// </para>
/// <para>
/// <strong>Where it runs: on the event consumer's thread, before the Registry applies the event.</strong> The
/// hook's answer has already gone (ingress answers <c>200</c> before the consumer sees the event), so no Claude turn
/// waits for this read. On the consumer the result rides on the event: the Registry keeps its one writer, and the
/// archive's row for the same event already has the name the row shows. A reader on its own thread would need an
/// event of its own to reach the Registry, and the name would arrive one record late.
/// </para>
/// <para>
/// <strong>Bounded.</strong> A read happens only when the file's length or write time changed since the last read
/// for that session; an unchanged file costs one file-information call. The first read for a session scans back
/// from the end, at most <see cref="BackScanLimit"/> bytes (measured 2026-10-16 over 393 transcripts: the newest
/// title line of each kind was at most 58,922 bytes from the end). Each later read takes only the bytes appended
/// since, at most the last <see cref="AppendLimit"/> of them. Nothing reads a whole multi-megabyte file, and a
/// multi-megabyte line (an image, a large tool result) is never held whole: the read looks for the title markers
/// in the raw bytes and parses only a title line, at most <see cref="LongestTitleLine"/> bytes long.
/// </para>
/// <para>
/// <strong>Read only, and only Claude Code's transcripts.</strong> The file is opened for reading with
/// <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/>, so Claude Code can go on writing it and
/// remove it. A <c>transcript_path</c> that is not a <c>.jsonl</c> file under
/// <see cref="ClaudeCodePaths.ProjectsDirectory"/> is never opened, and the log file says so once for the session.
/// </para>
/// <para>
/// <strong>Degrade, never crash.</strong> A missing, locked or malformed file, or a title line not yet complete,
/// gives <see cref="TranscriptNames.None"/>, and the row keeps the name it has. One <c>Debug</c> line for each
/// session says the first such read; a half-written line is read again with the next appended bytes.
/// </para>
/// <para>
/// <strong>One thread.</strong> Only the consumer calls <see cref="Read"/>, so this type has no lock, as the
/// Registry has none.
/// </para>
/// </remarks>
public sealed class TranscriptNameReader : ITranscriptNames
{
    /// <summary>The most bytes the first read for a session takes from the end of its transcript.</summary>
    public const int BackScanLimit = 1024 * 1024;

    /// <summary>The most appended bytes a later read takes: the last ones, when more were appended.</summary>
    public const int AppendLimit = 1024 * 1024;

    /// <summary>A title line longer than this is not parsed; measured title lines are well under 1 KB.</summary>
    public const int LongestTitleLine = 64 * 1024;

    private static readonly byte[] GivenMarker = "{\"type\":\"custom-title\""u8.ToArray();
    private static readonly byte[] GeneratedMarker = "{\"type\":\"ai-title\""u8.ToArray();

    private readonly string? _projects;
    private readonly ILogger _logger;
    private readonly Dictionary<SessionId, Cursor> _cursors = [];

    /// <summary>Reads transcripts under <paramref name="claude"/>'s projects folder.</summary>
    /// <param name="claude">Where Claude Code keeps its transcripts.</param>
    /// <param name="logger">The log file.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public TranscriptNameReader(ClaudeCodePaths claude, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(logger);

        _projects = FolderOf(claude.ProjectsDirectory);
        _logger = logger;
    }

    /// <inheritdoc/>
    public TranscriptNames Read(SessionId session, string? transcriptPath)
    {
        if (session.IsEmpty || string.IsNullOrEmpty(transcriptPath))
        {
            return TranscriptNames.None;
        }

        if (!_cursors.TryGetValue(session, out var cursor) ||
            !string.Equals(cursor.Path, transcriptPath, StringComparison.Ordinal))
        {
            cursor = new Cursor(transcriptPath, Inside(transcriptPath));
            _cursors[session] = cursor;

            if (cursor.FullPath is null)
            {
                _logger.Warning(
                    "The transcript path of session {SessionId} is not a .jsonl file under Claude Code's projects folder, so its title is not read: {TranscriptPath}",
                    session.Value,
                    transcriptPath);
            }
        }

        if (cursor.FullPath is null)
        {
            return TranscriptNames.None;
        }

        try
        {
            return ReadChanged(session, cursor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or NotSupportedException or ArgumentException)
        {
            NoteOnce(cursor, ex, "The transcript of session {SessionId} could not be read; its row keeps its name.", session);
            return TranscriptNames.None;
        }
    }

    /// <summary>How many sessions this reader holds a place for. Diagnostic only.</summary>
    internal int Tracked => _cursors.Count;

    private TranscriptNames ReadChanged(SessionId session, Cursor cursor)
    {
        var info = new FileInfo(cursor.FullPath!);

        if (!info.Exists)
        {
            NoteOnce(cursor, null, "The transcript of session {SessionId} is not there; its row keeps its name.", session);
            return TranscriptNames.None;
        }

        var length = info.Length;
        var written = info.LastWriteTimeUtc;

        if (cursor.HasRead && length == cursor.Length && written == cursor.WrittenUtc)
        {
            return TranscriptNames.None;
        }

        using var handle = File.OpenHandle(
            cursor.FullPath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var end = RandomAccess.GetLength(handle);

        // The first read, or a file that is now shorter than where the last read stopped (rewritten): scan back.
        var backScan = !cursor.HasRead || end < cursor.Next;
        var from = backScan
            ? Math.Max(0, end - BackScanLimit)
            : Math.Max(cursor.Next, end - AppendLimit);
        var atLineStart = backScan ? from == 0 : from == cursor.Next && cursor.NextAtLineStart;

        var count = (int)(end - from);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));

        try
        {
            var got = 0;

            while (got < count)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(got, count - got), from + got);
                if (read == 0)
                {
                    break;
                }

                got += read;
            }

            var window = buffer.AsSpan(0, got);
            var names = Scan(window, atLineStart);

            // The next read starts after the last complete line, so a line still being written is read whole later.
            var lastNewline = window.LastIndexOf((byte)'\n');
            cursor.Next = lastNewline >= 0 ? from + lastNewline + 1 : from + got;
            cursor.NextAtLineStart = lastNewline >= 0 || (got == 0 && atLineStart);
            cursor.Length = length;
            cursor.WrittenUtc = written;
            cursor.HasRead = true;

            if (backScan && from > 0 && names == TranscriptNames.None)
            {
                NoteOnce(
                    cursor,
                    null,
                    "No title line in the last 1 MB of the transcript of session {SessionId}; its row keeps its name.",
                    session);
            }

            return names;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The newest complete title line of each kind in <paramref name="window"/> (issue #161).
    /// </summary>
    /// <param name="window">Bytes of a transcript.</param>
    /// <param name="atLineStart">Whether the window starts at the start of a line.</param>
    internal static TranscriptNames Scan(ReadOnlySpan<byte> window, bool atLineStart) =>
        new(
            Newest(window, GivenMarker, "custom-title", "customTitle", atLineStart),
            Newest(window, GeneratedMarker, "ai-title", "aiTitle", atLineStart));

    private static string? Newest(ReadOnlySpan<byte> window, byte[] marker, string type, string key, bool atLineStart)
    {
        var searchEnd = window.Length;

        while (searchEnd > 0)
        {
            var at = window[..searchEnd].LastIndexOf(marker);
            if (at < 0)
            {
                return null;
            }

            searchEnd = at;

            // A marker counts only at the start of a line. Inside a JSON string its quotes would be escaped, so it
            // cannot match there; this also refuses a window that starts in the middle of a line.
            if (at == 0 ? !atLineStart : window[at - 1] != (byte)'\n')
            {
                continue;
            }

            // A line without its end is still being written: not read now, read whole next time.
            var lineLength = window[at..].IndexOf((byte)'\n');
            if (lineLength < 0 || lineLength > LongestTitleLine)
            {
                continue;
            }

            if (TitleIn(window.Slice(at, lineLength), type, key) is { } title)
            {
                return title;
            }
        }

        return null;
    }

    private static string? TitleIn(ReadOnlySpan<byte> line, string type, string key)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String ||
                !kind.ValueEquals(type) ||
                !root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var title = value.GetString();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? Inside(string transcriptPath)
    {
        if (_projects is null)
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(transcriptPath))
            {
                return null;
            }

            var full = Path.GetFullPath(transcriptPath);

            return full.StartsWith(_projects, StringComparison.OrdinalIgnoreCase) &&
                   full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? FolderOf(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void NoteOnce(Cursor cursor, Exception? ex, string message, SessionId session)
    {
        if (cursor.Noted)
        {
            return;
        }

        cursor.Noted = true;
        _logger.Debug(ex, message, session.Value);
    }

    /// <summary>Where the reads of one session's transcript stand.</summary>
    private sealed class Cursor(string path, string? fullPath)
    {
        /// <summary>The <c>transcript_path</c> as it arrived.</summary>
        public string Path { get; } = path;

        /// <summary>The full path, or null when it is not under the projects folder.</summary>
        public string? FullPath { get; } = fullPath;

        public bool HasRead { get; set; }

        public long Length { get; set; }

        public DateTime WrittenUtc { get; set; }

        /// <summary>Where the next appended read starts: after the last complete line read.</summary>
        public long Next { get; set; }

        public bool NextAtLineStart { get; set; }

        /// <summary>Whether this session has had its one Debug line.</summary>
        public bool Noted { get; set; }
    }
}
