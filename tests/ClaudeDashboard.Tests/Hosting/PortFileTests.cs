using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// <c>port.txt</c> as an input, which it became at T1.21 (Impl §3.1, Part 8).
/// </summary>
/// <remarks>
/// It was write-only until the port stopped being fixed. Now it carries two things nothing else
/// can: the port this user should try first, and — for a second launch — where the running
/// instance actually is.
/// </remarks>
public sealed class PortFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public PortFileTests()
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
            // Disposable temp folder.
        }
    }

    [Fact]
    public void A_written_port_reads_back()
    {
        Assert.True(PortFile.Write(_paths, 54321));

        Assert.Equal(54321, PortFile.Read(_paths));
    }

    /// <summary>A fresh profile has no file, and that is a normal answer rather than a fault.</summary>
    [Fact]
    public void A_fresh_profile_reads_as_no_recorded_port() =>
        Assert.Null(PortFile.Read(_paths));

    /// <summary>
    /// Every unusable value is the same answer to the caller: fall through to the derivation.
    /// </summary>
    /// <remarks>
    /// A hand-edited file is the realistic source of most of these. None of them is worth failing
    /// a start over, and none of them should be tried as a port — <c>0</c> in particular, which
    /// binds to "any free port" and would look like it worked.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a port")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    [InlineData("52789 52790")]
    public void An_unusable_recorded_value_reads_as_nothing(string contents)
    {
        File.WriteAllText(_paths.PortFile, contents);

        Assert.Null(PortFile.Read(_paths));
    }

    /// <summary>Surrounding whitespace is tolerated, because an editor will add it.</summary>
    [Fact]
    public void Whitespace_around_the_number_is_tolerated()
    {
        File.WriteAllText(_paths.PortFile, "  54321\r\n");

        Assert.Equal(54321, PortFile.Read(_paths));
    }

    /// <summary>An unreadable location is "no recorded port", not a throw.</summary>
    /// <remarks>
    /// This runs before the logger exists, on the path that decides whether the process is the
    /// resident dashboard. A throw here would be a launch with no window and no diagnosis.
    /// </remarks>
    [Fact]
    public void An_unreadable_location_is_not_a_throw()
    {
        // A directory where the file should be: unreadable as a file on any machine.
        Directory.CreateDirectory(_paths.PortFile);

        Assert.Null(PortFile.Read(_paths));
        Assert.False(PortFile.Write(_paths, 54321));
    }

    [Fact]
    public void It_needs_paths()
    {
        Assert.Throws<ArgumentNullException>(() => PortFile.Read(null!));
        Assert.Throws<ArgumentNullException>(() => PortFile.Write(null!, 1));
    }
}
