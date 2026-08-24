using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// <c>settings.json</c> round-tripping (Impl Part 8).
/// </summary>
/// <remarks>
/// Against the real filesystem in a temporary folder and the real
/// <see cref="System.Text.Json"/> serializer — a mocked file system would prove the test
/// double works, which is not the thing in doubt. What is in doubt is whether these settings
/// survive a trip through JSON and back, and whether a file a human edited badly stops the
/// dashboard starting.
/// </remarks>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _paths = new DashboardPaths(_root);
        _store = new SettingsStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteSettingsFile(string contents)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsFile, contents);
    }

    // ---- Round trip -------------------------------------------------------------------------

    [Fact]
    public void Settings_survive_a_round_trip_through_the_real_file()
    {
        var written = new DashboardSettings
        {
            Port = 51000,
            Logging = new LoggingSettings { RetainedFileCount = 3, FileSizeLimitBytes = 1024 },
        };

        _store.Save(written);
        var read = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, read.Outcome);
        Assert.Equal(written, read.Settings);
        Assert.True(File.Exists(_paths.SettingsFile), "Save must produce the file it claims to.");
    }

    [Fact]
    public void Defaults_survive_a_round_trip()
    {
        _store.Save(new DashboardSettings());

        Assert.Equal(new DashboardSettings(), _store.Load().Settings);
    }

    [Fact]
    public void Save_creates_the_data_folder()
    {
        Assert.False(Directory.Exists(_root));

        _store.Save(new DashboardSettings());

        Assert.True(Directory.Exists(_root));
    }

    /// <summary>Impl Part 8 calls this file human-editable, so it has to be readable by one.</summary>
    [Fact]
    public void The_written_file_is_indented_json_a_person_can_edit()
    {
        _store.Save(new DashboardSettings { Port = 51000 });

        var text = File.ReadAllText(_paths.SettingsFile);

        Assert.Contains("\"port\": 51000", text, StringComparison.Ordinal);
        Assert.Contains('\n', text);
    }

    // ---- Missing ------------------------------------------------------------------------------

    [Fact]
    public void A_missing_file_yields_defaults_and_says_so()
    {
        var result = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Missing, result.Outcome);
        Assert.Equal(new DashboardSettings(), result.Settings);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void A_missing_file_is_not_created_by_reading_it()
    {
        _store.Load();

        Assert.False(File.Exists(_paths.SettingsFile));
    }

    // ---- Malformed ------------------------------------------------------------------------------

    /// <summary>
    /// The case that decides whether the dashboard can start at all. Impl §10.1 auto-starts it
    /// from a scheduled task that retries three times; refusing to start over a stray comma
    /// would present to the operator not as a bad setting but as the dashboard being gone.
    /// </summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{ \"port\": }")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    public void A_malformed_file_yields_defaults_rather_than_refusing_to_start(string contents)
    {
        WriteSettingsFile(contents);

        var result = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Unreadable, result.Outcome);
        Assert.Equal(new DashboardSettings(), result.Settings);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem), "The reason must reach the log.");
    }

    /// <summary>Valid JSON that parses to nothing is still unusable, and must not read as loaded.</summary>
    [Fact]
    public void A_file_containing_null_is_unreadable_not_loaded()
    {
        WriteSettingsFile("null");

        Assert.Equal(SettingsLoadOutcome.Unreadable, _store.Load().Outcome);
    }

    /// <summary>
    /// The operator's file is left exactly as it was. Overwriting it with defaults would destroy
    /// both the evidence of what was wrong and whatever they meant to keep.
    /// </summary>
    [Fact]
    public void A_malformed_file_is_left_untouched_on_disk()
    {
        const string Broken = "{ \"port\": 51000,, }";
        WriteSettingsFile(Broken);

        _store.Load();

        Assert.Equal(Broken, File.ReadAllText(_paths.SettingsFile));
    }

    // ---- Tolerance and validation -------------------------------------------------------------------

    /// <summary>A human editing JSON writes comments and trailing commas; neither should lose the file.</summary>
    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        WriteSettingsFile("{ /* the ingress port */ \"port\": 51000, }");

        var result = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(51000, result.Settings.Port);
    }

    [Fact]
    public void An_absent_section_falls_back_to_its_defaults()
    {
        WriteSettingsFile("{ \"port\": 51000 }");

        var result = _store.Load();

        Assert.Equal(51000, result.Settings.Port);
        Assert.Equal(LoggingSettings.DefaultRetainedFiles, result.Settings.Logging.RetainedFileCount);
    }

    /// <summary>A typo in one value must not cost the whole file.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void An_out_of_range_port_falls_back_to_the_default(int port)
    {
        WriteSettingsFile($"{{ \"port\": {port} }}");

        var result = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(DashboardSettings.DefaultPort, result.Settings.Port);
    }

    [Fact]
    public void Nonsensical_logging_limits_fall_back_to_their_defaults()
    {
        WriteSettingsFile("{ \"logging\": { \"retainedFileCount\": 0, \"fileSizeLimitBytes\": -5 } }");

        var logging = _store.Load().Settings.Logging;

        Assert.Equal(LoggingSettings.DefaultRetainedFiles, logging.RetainedFileCount);
        Assert.Equal(LoggingSettings.DefaultFileSizeLimitBytes, logging.FileSizeLimitBytes);
    }

    [Fact]
    public void Defaults_are_the_values_the_specs_name()
    {
        var settings = new DashboardSettings();

        // Impl §3.1: a fixed port in the private range, so the hook URL stays stable.
        Assert.Equal(52789, settings.Port);
        Assert.Equal(DashboardSettings.DefaultPort, settings.Port);
    }

    [Fact]
    public void The_store_needs_paths_and_something_to_save()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsStore(null!));
        Assert.Throws<ArgumentNullException>(() => _store.Save(null!));
    }
}

public sealed class DashboardPathsTests
{
    [Fact]
    public void Paths_sit_under_the_data_folder_Impl_part_8_names()
    {
        var paths = new DashboardPaths(@"C:\data\ClaudeDashboard");

        Assert.Equal(@"C:\data\ClaudeDashboard", paths.Root);
        Assert.Equal(@"C:\data\ClaudeDashboard\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\data\ClaudeDashboard\logs", paths.LogFolder);
        Assert.StartsWith(paths.LogFolder, paths.LogFile, StringComparison.Ordinal);
    }

    /// <summary>The default resolves under %LOCALAPPDATA%, which is what Impl Part 8 specifies.</summary>
    [Fact]
    public void The_default_root_is_local_appdata()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DashboardPaths.FolderName);

        Assert.Equal(expected, new DashboardPaths().Root);
    }

    [Fact]
    public void A_root_is_required()
    {
        Assert.Throws<ArgumentException>(() => new DashboardPaths(null!));
        Assert.Throws<ArgumentException>(() => new DashboardPaths("  "));
    }

    [Fact]
    public void Ensuring_the_folders_creates_them_on_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        var paths = new DashboardPaths(root);

        try
        {
            Assert.True(paths.TryEnsureCreated(out var failure));
            Assert.Null(failure);
            Assert.True(Directory.Exists(paths.Root));
            Assert.True(Directory.Exists(paths.LogFolder));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Losing diagnostics is a smaller failure than not starting, so an uncreatable folder is
    /// reported rather than thrown.
    /// </summary>
    [Fact]
    public void An_uncreatable_folder_is_reported_rather_than_thrown()
    {
        // A path under a file rather than a directory cannot be created.
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "not a directory");

        try
        {
            var paths = new DashboardPaths(Path.Combine(file, "ClaudeDashboard"));

            Assert.False(paths.TryEnsureCreated(out var failure));
            Assert.False(string.IsNullOrWhiteSpace(failure));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
