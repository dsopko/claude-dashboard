using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Adapters;

/// <summary>
/// The read of a session's names from its transcript (issue #161): the newest of each kind, from the end, only when
/// the file changed, only under the projects folder, and never a throw. Every transcript here is written in a temp
/// folder that stands for Claude Code's configuration folder; the real one is never read.
/// </summary>
public sealed class TranscriptNameReaderTests : IDisposable
{
    private static readonly SessionId Id = new("s-1");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLogSink _log = new();
    private readonly Logger _logger;
    private readonly TranscriptNameReader _reader;
    private readonly string _transcript;

    public TranscriptNameReaderTests()
    {
        var claude = new ClaudeCodePaths(Path.Combine(_root, "claude-config"));
        var project = Path.Combine(claude.ProjectsDirectory, "C--dev-PennCustQuote");
        Directory.CreateDirectory(project);

        _transcript = Path.Combine(project, "2fc94061.jsonl");
        _logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();
        _reader = new TranscriptNameReader(claude, _logger);
    }

    public void Dispose()
    {
        _logger.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }
    }

    /// <summary>
    /// The newest line of each kind, in the order the transcripts hold them: Claude Code goes on writing
    /// <c>aiTitle</c> after a rename, so the given name is often not the newest title line.
    /// </summary>
    [Fact]
    public void The_read_gives_the_newest_given_name_and_the_newest_generated_title()
    {
        Write(Generated("First generated"), User("go"), Given("Old name"), Given("Director"), Generated("Second generated"), User("more"));

        Assert.Equal(new TranscriptNames("Director", "Second generated"), _reader.Read(Id, _transcript));
    }

    /// <summary>
    /// An unchanged file is not opened again: the second read gives nothing, so the row keeps its name. The lock
    /// shows the file was not opened: an open would fail, and the log file would have its Debug line.
    /// </summary>
    [Fact]
    public void An_unchanged_file_is_not_opened_again()
    {
        Write(Generated("Generated words"));

        Assert.Equal("Generated words", _reader.Read(Id, _transcript).Generated);

        using (new FileStream(_transcript, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));
        }

        Assert.Empty(_log.Events);
    }

    /// <summary>
    /// <strong>Follow changes:</strong> after the first read, only the appended bytes are read, so a new generated
    /// title is found and an appended line with no title gives nothing.
    /// </summary>
    [Fact]
    public void Appended_lines_give_the_new_title()
    {
        Write(User("go"), Generated("Build network monitoring application"));
        Assert.Equal("Build network monitoring application", _reader.Read(Id, _transcript).Generated);

        Append(User("rename the project"), Generated("home-internet-monitor-ping"));
        Assert.Equal(new TranscriptNames(null, "home-internet-monitor-ping"), _reader.Read(Id, _transcript));

        Append(User("and again"));
        Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));
    }

    /// <summary>
    /// <strong>Read only a path under the projects folder.</strong> Each of these holds a title line, so a read
    /// would find one; none is opened, and the log file says so once for the session, at Warning.
    /// </summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("dot-dot")]
    [InlineData("relative")]
    [InlineData("not-jsonl")]
    public void A_path_outside_the_projects_folder_is_never_read(string kind)
    {
        var outside = Path.Combine(_root, "elsewhere", "secret.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, Generated("Read from outside") + "\n");

        var notJsonl = Path.ChangeExtension(_transcript, ".txt");
        File.WriteAllText(notJsonl, Generated("Read from a .txt") + "\n");

        var path = kind switch
        {
            "outside" => outside,
            "dot-dot" => Path.Combine(Path.GetDirectoryName(_transcript)!, "..", "..", "..", "elsewhere", "secret.jsonl"),
            "relative" => Path.Combine("elsewhere", "secret.jsonl"),
            _ => notJsonl,
        };

        Assert.Equal(TranscriptNames.None, _reader.Read(Id, path));
        Assert.Equal(TranscriptNames.None, _reader.Read(Id, path));

        var warning = Assert.Single(_log.Events, entry => entry.Level == LogEventLevel.Warning);
        Assert.Contains("is not a .jsonl file under Claude Code's projects folder, so its title is not read", warning.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>A missing file gives nothing and no throw; the file is read once it is there.</summary>
    [Fact]
    public void A_missing_file_gives_nothing_until_it_is_there()
    {
        Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));

        Write(Generated("Generated words"));

        Assert.Equal("Generated words", _reader.Read(Id, _transcript).Generated);
        Assert.Single(_log.Events, entry => entry.Level == LogEventLevel.Debug);
    }

    /// <summary>A file locked against readers gives nothing and no throw; it is read once the lock is gone.</summary>
    [Fact]
    public void A_locked_file_gives_nothing_until_it_is_released()
    {
        Write(Generated("Generated words"));

        using (new FileStream(_transcript, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));
        }

        Assert.Equal("Generated words", _reader.Read(Id, _transcript).Generated);
        Assert.Single(_log.Events, entry => entry.Level == LogEventLevel.Debug && entry.Exception is IOException);
    }

    /// <summary>
    /// The writer's handle stays usable: a file Claude Code holds open for writing is read, and it can still write
    /// and delete it afterwards.
    /// </summary>
    [Fact]
    public void A_file_held_open_by_its_writer_is_read_and_stays_writable()
    {
        using (var writer = new FileStream(_transcript, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
        {
            writer.Write(Encoding.UTF8.GetBytes(Generated("Generated words") + "\n"));
            writer.Flush();

            Assert.Equal("Generated words", _reader.Read(Id, _transcript).Generated);

            writer.Write(Encoding.UTF8.GetBytes(Generated("Later words") + "\n"));
            writer.Flush();

            Assert.Equal("Later words", _reader.Read(Id, _transcript).Generated);
        }

        File.Delete(_transcript);
        Assert.False(File.Exists(_transcript));
    }

    /// <summary>
    /// A half-written last line gives nothing and no throw; it is read whole with the next appended bytes.
    /// </summary>
    [Fact]
    public void A_partly_written_line_is_read_once_it_is_complete()
    {
        var line = Generated("Generated words");
        var cut = line.Length / 2;

        File.WriteAllText(_transcript, User("go") + "\n" + line[..cut]);
        Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));

        File.AppendAllText(_transcript, line[cut..] + "\n");
        Assert.Equal("Generated words", _reader.Read(Id, _transcript).Generated);
    }

    /// <summary>A malformed title line is passed over for the newest good one before it.</summary>
    [Fact]
    public void A_malformed_title_line_is_passed_over()
    {
        Write(Generated("Good words"), """{"type":"ai-title","aiTitle":"Broken""", """{"type":"ai-title","aiTitle":42,"sessionId":"s-1"}""");

        Assert.Equal("Good words", _reader.Read(Id, _transcript).Generated);
    }

    /// <summary>
    /// A marker that does not start a line is not a title line: inside a JSON string its quotes are escaped, and a
    /// nested object is another line's data.
    /// </summary>
    [Fact]
    public void A_marker_inside_a_line_is_not_a_title()
    {
        Write(
            """{"type":"user","message":{"type":"ai-title","aiTitle":"Nested"}}""",
            JsonSerializer.Serialize(new { type = "user", message = Generated("Quoted") }));

        Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));
    }

    /// <summary>
    /// <strong>Bounded:</strong> the first read takes at most the last megabyte. A title further back is not found,
    /// the row keeps its name, and the log file says so once for the session, at Debug.
    /// </summary>
    [Fact]
    public void The_first_read_scans_back_one_megabyte_and_no_further()
    {
        Write(Generated("Too far back"), Filler(TranscriptNameReader.BackScanLimit + 4096));

        Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));

        var note = Assert.Single(_log.Events, entry => entry.Level == LogEventLevel.Debug);
        Assert.Contains("No title line in the last 1 MB", note.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // The control: the same title just inside the limit is found.
        Write(Generated("Just inside"), Filler(TranscriptNameReader.BackScanLimit - 4096));
        Assert.Equal("Just inside", new TranscriptNameReader(new ClaudeCodePaths(Path.Combine(_root, "claude-config")), _logger).Read(Id, _transcript).Generated);
    }

    /// <summary>
    /// A long line between the title and the end (an image, a large tool result) is passed over in the raw bytes;
    /// it is never parsed.
    /// </summary>
    [Fact]
    public void A_long_line_after_the_title_is_passed_over()
    {
        Write(Given("Director"), User(new string('x', 900_000)));

        Assert.Equal("Director", _reader.Read(Id, _transcript).Given);
    }

    /// <summary>
    /// An append larger than the limit is read from its last megabyte, where the newest title is; the bytes before
    /// it are not read.
    /// </summary>
    [Fact]
    public void A_large_append_is_read_from_its_end()
    {
        Write(Generated("First"));
        _reader.Read(Id, _transcript);

        Append(Generated("Lost in the middle"), Filler(TranscriptNameReader.AppendLimit + 4096), Generated("Newest"));

        Assert.Equal(new TranscriptNames(null, "Newest"), _reader.Read(Id, _transcript));
    }

    /// <summary>A file that is now shorter than the last read was rewritten: it is scanned back again.</summary>
    [Fact]
    public void A_rewritten_shorter_file_is_scanned_again()
    {
        Write(User(new string('x', 10_000)), Generated("Before"));
        _reader.Read(Id, _transcript);

        Write(Generated("After"));

        Assert.Equal("After", _reader.Read(Id, _transcript).Generated);
    }

    /// <summary>
    /// <strong>Text is data:</strong> the title comes back as it was written, line breaks, control characters and
    /// a right-to-left override included. The row folds it (issue #22); the domain keeps it.
    /// </summary>
    [Fact]
    public void A_title_is_kept_verbatim()
    {
        var raw = "Fix\nthe\tbuild " + (char)0x202E + "txt.exe" + (char)0x0007;
        Write(Generated(raw));

        Assert.Equal(raw, _reader.Read(Id, _transcript).Generated);
    }

    /// <summary>Each session has its own place in its own file.</summary>
    [Fact]
    public void Each_session_is_read_on_its_own()
    {
        var other = Path.Combine(Path.GetDirectoryName(_transcript)!, "7a239067.jsonl");
        Write(Generated("First session"));
        File.WriteAllText(other, Generated("Second session") + "\n");

        Assert.Equal("First session", _reader.Read(Id, _transcript).Generated);
        Assert.Equal("Second session", _reader.Read(new SessionId("s-2"), other).Generated);
        Assert.Equal(TranscriptNames.None, _reader.Read(Id, _transcript));
    }

    private static string Generated(string title) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "ai-title", ["aiTitle"] = title, ["sessionId"] = "s-1" });

    private static string Given(string title) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "custom-title", ["customTitle"] = title, ["sessionId"] = "s-1" });

    private static string User(string text) =>
        JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });

    /// <summary>Lines of other kinds, about <paramref name="bytes"/> long in all.</summary>
    private static string Filler(int bytes)
    {
        var line = User(new string('f', 4000));
        var builder = new StringBuilder();

        while (builder.Length < bytes)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private void Write(params string[] lines) => File.WriteAllText(_transcript, string.Join("\n", lines) + "\n");

    private void Append(params string[] lines) => File.AppendAllText(_transcript, string.Join("\n", lines) + "\n");
}
