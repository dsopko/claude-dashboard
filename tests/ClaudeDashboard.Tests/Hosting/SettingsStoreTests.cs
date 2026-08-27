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
    /// <summary>
    /// <strong>A port that is not a port becomes unset, and never becomes the default.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserted the opposite until T1.21, and the old behaviour is now the worst outcome
    /// available: with a per-user port, coercing a typo to <see cref="DashboardSettings.DefaultPort"/>
    /// turns a mistake into a hard pin on the single port most likely to be contended — the
    /// operator pinned by accident to the one port the feature exists to stop everybody sharing.
    /// </para>
    /// <para>
    /// Falling through to the derivation is what a mistyped port should do, and the mistake is
    /// reported rather than swallowed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void An_out_of_range_port_becomes_unset_rather_than_the_default(int port)
    {
        WriteSettingsFile($"{{ \"port\": {port} }}");

        var result = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Null(result.Settings.Port);

        // A person tried to pin a port and did not, so it is said rather than left silent.
        Assert.NotNull(result.Problem);
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

        // Impl §3.1 as amended: the port is UNSET until an operator pins one. DefaultPort is the
        // base the derivation counts from, not a value anything defaults to — see DashboardSettings.
        Assert.Null(settings.Port);
        Assert.Equal(52789, DashboardSettings.DefaultPort);
    }

    [Fact]
    public void The_store_needs_paths_and_something_to_save()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsStore(null!));
        Assert.Throws<ArgumentNullException>(() => _store.Save(null!));
    }
    // ---- The port is a pin, or it is nothing (T1.21) -------------------------------------------

    /// <summary>A settings file with no port leaves the port unset, not defaulted.</summary>
    /// <remarks>
    /// The distinction the per-user port rests on. If an absent key produced
    /// <see cref="DashboardSettings.DefaultPort"/>, every operator who has never opened the file
    /// would look pinned to it, and honouring that would put every user back on one shared port.
    /// </remarks>
    [Fact]
    public void A_file_without_a_port_leaves_it_unset()
    {
        WriteSettingsFile("""{"logging":{"retainedFileCount":3}}""");

        var loaded = _store.Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, loaded.Outcome);
        Assert.Null(loaded.Settings.Port);
    }

    /// <summary>A pinned port survives the round trip as a pin.</summary>
    [Fact]
    public void A_pinned_port_round_trips()
    {
        _store.Save(new DashboardSettings { Port = 51000 });

        Assert.Equal(51000, _store.Load().Settings.Port);
    }

    /// <summary>
    /// <strong>A port that is not a port becomes unset, and never becomes the default.</strong>
    /// </summary>
    /// <remarks>
    /// The old behaviour coerced an out-of-range value to <see cref="DashboardSettings.DefaultPort"/>.
    /// Under a per-user port that is the worst available outcome: a typo would become a hard pin on
    /// the single port most likely to be contended — the operator would be pinned by accident to
    /// the one port the whole feature exists to stop everybody sharing. Falling through to the
    /// derivation is what a mistake should do.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void A_port_that_is_not_a_port_becomes_unset(string value)
    {
        WriteSettingsFile($$"""{"port":{{value}}}""");

        var loaded = _store.Load();

        Assert.Null(loaded.Settings.Port);
        Assert.NotEqual(DashboardSettings.DefaultPort, loaded.Settings.Port);
    }

    /// <summary>And a mistyped port says so, because it is a person who meant something.</summary>
    /// <remarks>
    /// An absent key and a mistyped one both end as <see langword="null"/>, so they are
    /// indistinguishable from the settings object. They are not the same event: one is normal and
    /// needs no line, the other is somebody who tried to pin a port and did not. The difference is
    /// recovered from the file rather than stored beside the value, so no second copy exists to
    /// disagree with the first.
    /// </remarks>
    [Fact]
    public void A_mistyped_port_is_reported_while_an_absent_one_is_not()
    {
        WriteSettingsFile("""{"port":0}""");
        var mistyped = _store.Load();

        Assert.NotNull(mistyped.Problem);
        Assert.Contains("port", mistyped.Problem!, StringComparison.OrdinalIgnoreCase);

        WriteSettingsFile("""{"logging":{"retainedFileCount":3}}""");

        Assert.Null(_store.Load().Problem);
    }
}

[Collection(ClaudeDashboard.Tests.Configuration.DataFolderEnvironment.Name)]
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
    /// <remarks>
    /// The override is cleared for the duration. Without that this asserts the default only on a
    /// machine where nobody has set <c>CLAUDE_DASHBOARD_HOME</c> — which was every machine until
    /// the variable shipped, and is not something a test should depend on. The class is in the
    /// serialized collection so clearing it cannot be seen by the tests that set it.
    /// </remarks>
    [Fact]
    public void The_default_root_is_local_appdata()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DashboardPaths.FolderName);

        var previous = Environment.GetEnvironmentVariable(DashboardPaths.HomeVariable);
        Environment.SetEnvironmentVariable(DashboardPaths.HomeVariable, null);

        try
        {
            Assert.Equal(expected, new DashboardPaths().Root);
            Assert.Equal(expected, DashboardPaths.DefaultRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DashboardPaths.HomeVariable, previous);
        }
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
