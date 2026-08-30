using System.Globalization;
using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Configuration;

/// <summary>
/// <c>listening.txt</c> — the file whose <em>absence</em> is the feature (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Against the real filesystem, in a temporary folder.</strong> The point of this type is
/// what it puts on disk for a batch file to read, which a double cannot tell you.
/// </para>
/// <para>
/// The half of the contract that cannot be asserted here is the reader: <c>post-status.cmd</c> is
/// a batch file and does not call this. <c>HookScriptBehaviourTests</c> drives the real script
/// against files this class wrote, which is where the two readers are compared.
/// </para>
/// </remarks>
public sealed class ListeningFileTests : IDisposable
{
    private const int Port = 61345;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public ListeningFileTests()
    {
        _paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>It writes the port, and nothing else, into the data folder.</summary>
    /// <remarks>
    /// No trailing newline and no BOM. The reader is <c>set /p</c> in a batch file, and while that
    /// strips a line ending it does not strip a byte-order mark — a BOM would make the first
    /// character of the number unparseable and the hook would silently stop working.
    /// </remarks>
    [Fact]
    public void Writing_puts_the_bare_port_in_the_data_folder()
    {
        Assert.True(ListeningFile.Write(_paths, Port));

        Assert.Equal(_root, Path.GetDirectoryName(_paths.ListeningFile));
        Assert.Equal("listening.txt", Path.GetFileName(_paths.ListeningFile));
        Assert.Equal(
            Port.ToString(CultureInfo.InvariantCulture),
            File.ReadAllText(_paths.ListeningFile, System.Text.Encoding.UTF8));
        Assert.Equal(
            Port.ToString(CultureInfo.InvariantCulture).Length,
            new FileInfo(_paths.ListeningFile).Length);
    }

    /// <summary>Round trip, so the two ends of the format cannot drift apart.</summary>
    [Fact]
    public void What_is_written_is_what_is_read()
    {
        ListeningFile.Write(_paths, Port);

        Assert.Equal(Port, ListeningFile.Read(_paths));
    }

    /// <summary>
    /// <strong>It is overwritten on every start, which is what corrects a file a crash left.</strong>
    /// </summary>
    /// <remarks>
    /// The residual issue #29 states is that a hard kill leaves this file naming the last bound
    /// port, and until the next start the script posts to whatever holds it. The next start
    /// closing that is not a detail; it is the whole of why the exposure is bounded.
    /// </remarks>
    [Fact]
    public void A_stale_file_from_a_crash_is_corrected_rather_than_left()
    {
        File.WriteAllText(_paths.ListeningFile, "52789");

        ListeningFile.Write(_paths, Port);

        Assert.Equal(Port, ListeningFile.Read(_paths));
    }

    /// <summary>No temporary file survives a write.</summary>
    /// <remarks>
    /// The write is temp-then-rename because a torn read gives the script half a number. The
    /// residue of that is a file beside the operator's data that nobody can explain, so the move
    /// has to consume it rather than copy it.
    /// </remarks>
    [Fact]
    public void Writing_leaves_no_temporary_behind()
    {
        ListeningFile.Write(_paths, Port);
        ListeningFile.Write(_paths, Port + 1);

        Assert.Equal([_paths.ListeningFile], Directory.EnumerateFiles(_root));
    }

    /// <summary>Deleting takes the announcement away.</summary>
    [Fact]
    public void Deleting_removes_the_file()
    {
        ListeningFile.Write(_paths, Port);

        Assert.True(ListeningFile.Delete(_paths));
        Assert.False(File.Exists(_paths.ListeningFile));
        Assert.Null(ListeningFile.Read(_paths));
    }

    /// <summary>Deleting what is not there succeeds.</summary>
    /// <remarks>
    /// Four exit paths call the withdrawal and more than one runs for a single exit, so "it is
    /// gone" is the outcome and "I removed it" is not. A delete that reported failure the second
    /// time would make a correct shutdown log a warning.
    /// </remarks>
    [Fact]
    public void Deleting_what_was_never_there_succeeds()
    {
        Assert.True(ListeningFile.Delete(_paths));
        Assert.True(ListeningFile.Delete(_paths));
    }

    /// <summary>
    /// <strong>It is a different file from <c>port.txt</c>, and deleting it leaves that one alone.</strong>
    /// </summary>
    /// <remarks>
    /// The ruling this feature turns on. <c>port.txt</c> is an input — Impl §3.1's first attempt,
    /// and the only thing that tells a second launch where a running instance is — so deleting it
    /// on shutdown would break port continuity and <c>POST /show</c>, both without a sound. One
    /// file cannot carry both meanings, and this asserts the separation at the level of the two
    /// paths as well as the two behaviours.
    /// </remarks>
    [Fact]
    public void Deleting_the_announcement_does_not_touch_the_port_file()
    {
        PortFile.Write(_paths, Port);
        ListeningFile.Write(_paths, Port);

        ListeningFile.Delete(_paths);

        Assert.NotEqual(_paths.PortFile, _paths.ListeningFile);
        Assert.True(File.Exists(_paths.PortFile));
        Assert.Equal(Port, PortFile.Read(_paths));
    }

    /// <summary>Everything unreadable is one answer: nothing is listening.</summary>
    /// <remarks>
    /// Null rather than an exception or a sentinel, because a caller has nothing different to do
    /// about a missing file, an empty one, a hand-edited one and a port outside the range.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("52789 52790")]
    public void Nothing_usable_reads_as_nothing_listening(string content)
    {
        File.WriteAllText(_paths.ListeningFile, content);

        Assert.Null(ListeningFile.Read(_paths));
    }

    /// <summary>A missing file and a missing folder both read as nothing listening.</summary>
    [Fact]
    public void An_absent_file_reads_as_nothing_listening()
    {
        Assert.Null(ListeningFile.Read(_paths));
        Assert.Null(ListeningFile.Read(new DashboardPaths(Path.Combine(_root, "does-not-exist"))));
    }

    [Fact]
    public void It_needs_its_paths()
    {
        Assert.Throws<ArgumentNullException>(() => ListeningFile.Read(null!));
        Assert.Throws<ArgumentNullException>(() => ListeningFile.Write(null!, Port));
        Assert.Throws<ArgumentNullException>(() => ListeningFile.Delete(null!));
    }
}
