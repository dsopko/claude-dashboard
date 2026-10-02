using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using Serilog;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The log file follows <c>logging.minimumLevel</c> (T1.52, issue #68).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Through the real <see cref="AppHost.CreateLogger"/>, and read back from the file it
/// writes.</strong> The defect lived in the file sink's own floor, a fixed Information that sat
/// under the logger's floor. Every earlier test of a Debug line built its own logger with its own
/// sink, so none of them could see it: the setting said Debug, the logger passed the line, and the
/// file dropped it.
/// </para>
/// <para>
/// Each test disposes the logger before reading, because disposing is what flushes; the sink
/// otherwise flushes every two seconds. The static <see cref="Log.Logger"/> that
/// <c>CreateLogger</c> assigns is put back afterwards.
/// </para>
/// </remarks>
public sealed class LogFileLevelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public LogFileLevelTests()
    {
        _paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_paths.LogFolder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not a test failure.
        }
    }

    /// <summary>
    /// Writes one line at each of Debug, Information and Warning through the real logger set to
    /// <paramref name="minimumLevel"/>, and returns everything the log folder holds afterwards.
    /// </summary>
    private string Written(string? minimumLevel)
    {
        var logging = minimumLevel is null
            ? new LoggingSettings()
            : new LoggingSettings { MinimumLevel = minimumLevel };
        var previous = Log.Logger;

        try
        {
            using (var logger = AppHost.CreateLogger(_paths, logging, foldersReady: true))
            {
                logger.Debug("T152 debug line {Marker}", "d-1");
                logger.Information("T152 information line {Marker}", "i-1");
                logger.Warning("T152 warning line {Marker}", "w-1");
            }
        }
        finally
        {
            Log.Logger = previous;
        }

        var files = Directory.GetFiles(_paths.LogFolder);

        Assert.NotEmpty(files);

        return string.Join('\n', files.Select(File.ReadAllText));
    }

    /// <summary>
    /// <strong>Set to Debug, a Debug line is in the file.</strong> That is what the setting is for:
    /// the decisions record, line by line, while someone chases a sound.
    /// </summary>
    [Fact]
    public void A_debug_line_reaches_the_file_when_the_setting_is_Debug()
    {
        var text = Written("Debug");

        Assert.Contains("T152 debug line d-1", text, StringComparison.Ordinal);
        Assert.Contains("T152 information line i-1", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>With the default, a Debug line is not in the file</strong>, and an Information line
    /// is — so the file was written and the absence means something. Nothing changes for an
    /// operator who never set the key.
    /// </summary>
    [Fact]
    public void A_debug_line_stays_out_of_the_file_at_the_default_level()
    {
        Assert.Equal("Information", LoggingSettings.DefaultMinimumLevel);

        var text = Written(minimumLevel: null);

        Assert.DoesNotContain("T152 debug line", text, StringComparison.Ordinal);
        Assert.Contains("T152 information line i-1", text, StringComparison.Ordinal);
    }

    /// <summary>A level above Information still keeps Information out of the file.</summary>
    [Fact]
    public void A_level_above_Information_keeps_Information_out_of_the_file()
    {
        var text = Written("Warning");

        Assert.DoesNotContain("T152 debug line", text, StringComparison.Ordinal);
        Assert.DoesNotContain("T152 information line", text, StringComparison.Ordinal);
        Assert.Contains("T152 warning line w-1", text, StringComparison.Ordinal);
    }
}
