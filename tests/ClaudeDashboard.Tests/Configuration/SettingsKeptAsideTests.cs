using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Configuration;

/// <summary>
/// A settings file that cannot be read is kept aside, a fresh one is written, and the start says
/// so (T1.56, issue #73, the operator's ruling of 2026-10-03).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The bad file is the operator's, and its bytes are evidence.</strong> Every test that
/// keeps one aside checks the backup by hash against what was on disk, so a copy that re-encodes
/// the text (a lost byte-order mark, changed line ends) fails as surely as an overwrite.
/// </para>
/// <para>
/// <strong>Scratch folders only.</strong> Each test makes its own data folder and its own Claude
/// Code folder under the temp path; nothing here reads or writes the operator's.
/// </para>
/// </remarks>
public sealed class SettingsKeptAsideTests : IDisposable
{
    /// <summary>A file that does not parse, with a byte-order mark and CRLF line ends.</summary>
    private static readonly byte[] BadBytes =
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{\r\n  \"port\":\r\n}\r\n")];

    private static readonly DateTime When = new(2026, 10, 3, 14, 5, 9, DateTimeKind.Local);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;
    private readonly RecordingLogSink _log = new();
    private readonly Serilog.Core.Logger _logger;

    public SettingsKeptAsideTests()
    {
        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);

        _claude = new ClaudeCodePaths(Path.Combine(_root, "dot-claude"));
        Directory.CreateDirectory(_claude.ConfigDirectory);

        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();
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
        }
    }

    private SettingsStore Store() => new(_paths, _logger);

    private static string BackupName => $"settings.error-{When:yyyyMMdd-HHmmss}.json";

    private string[] Backups() => Directory.GetFiles(_paths.Root, "settings.error-*.json");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string HashOf(string path) => Hash(File.ReadAllBytes(path));

    private SettingsAtStart KeepAsideABadFile()
    {
        File.WriteAllBytes(_paths.SettingsFile, BadBytes);

        var store = Store();

        return store.PrepareForStart(store.Load(), When);
    }

    // ---- The file is moved, and a fresh one written ----------------------------------------------

    /// <summary>
    /// <strong>The backup holds the original bytes, and the new file the defaults.</strong>
    /// </summary>
    [Fact]
    public void A_file_that_does_not_parse_is_renamed_with_its_bytes_and_a_fresh_one_written()
    {
        var start = KeepAsideABadFile();

        Assert.True(start.KeptAside);
        Assert.False(start.SavesRefused);
        Assert.Equal(SettingsLoadOutcome.Unreadable, start.Original.Outcome);
        Assert.Equal(Path.Combine(_paths.Root, BackupName), start.BackupFile);

        Assert.Equal(Hash(BadBytes), HashOf(start.BackupFile!));

        var fresh = Store().Load();
        Assert.Equal(SettingsLoadOutcome.Loaded, fresh.Outcome);
        Assert.Equal(new DashboardSettings(), fresh.Settings);

        Assert.False(File.Exists(_paths.SettingsFile + ".new"), "The fresh file's temporary name was left behind.");
        Assert.Single(Backups());
    }

    /// <summary>The next start reads the fresh file: no second backup, and no notice.</summary>
    [Fact]
    public void The_next_start_finds_the_fresh_file_and_keeps_nothing_aside()
    {
        KeepAsideABadFile();

        var store = Store();
        var next = store.PrepareForStart(store.Load(), When.AddMinutes(1));

        Assert.Equal(SettingsLoadOutcome.Loaded, next.Original.Outcome);
        Assert.False(next.KeptAside);
        Assert.False(next.SavesRefused);
        Assert.Single(Backups());
        Assert.False(new SettingsNotice(next, _paths).IsShown);
    }

    /// <summary>
    /// A quit after the bad start saves the window's place into the fresh file; the backup is
    /// untouched.
    /// </summary>
    [Fact]
    public void A_later_save_goes_to_the_fresh_file_and_never_to_the_backup()
    {
        var start = KeepAsideABadFile();
        var store = Store();
        var place = new WindowSettings { Left = 120, Top = 80 };

        // As Program does at quit: re-read, override the window, save.
        Assert.True(store.Save(store.Load().Settings with { Window = place }));

        Assert.Equal(place, Store().Load().Settings.Window);
        Assert.Equal(Hash(BadBytes), HashOf(start.BackupFile!));
    }

    /// <summary>A name that is taken gets <c>-2</c>; the file that held it is not touched.</summary>
    [Fact]
    public void A_taken_backup_name_gets_a_number()
    {
        var taken = Path.Combine(_paths.Root, BackupName);
        File.WriteAllText(taken, "an earlier backup");

        var start = KeepAsideABadFile();

        Assert.Equal(Path.Combine(_paths.Root, $"settings.error-{When:yyyyMMdd-HHmmss}-2.json"), start.BackupFile);
        Assert.Equal("an earlier backup", File.ReadAllText(taken));
        Assert.Equal(Hash(BadBytes), HashOf(start.BackupFile!));
    }

    /// <summary>A bare <c>null</c> and a wrong type do not parse either, and are kept aside.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("""{ "port": "not a number", "sound": 7 }""")]
    public void Null_and_a_wrong_type_are_kept_aside_too(string text)
    {
        File.WriteAllText(_paths.SettingsFile, text);
        var store = Store();

        var start = store.PrepareForStart(store.Load(), When);

        Assert.True(start.KeptAside, $"Not kept aside: {start.Original.Problem}");
        Assert.Equal(text, File.ReadAllText(start.BackupFile!));
    }

    // ---- The file cannot be opened, or kept aside ------------------------------------------------

    /// <summary>
    /// <strong>A file held open by another program is left alone: no backup, no new file, and the
    /// defaults.</strong> Every save for the rest of the run is refused at the store.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_opened_is_left_alone_and_saves_are_refused()
    {
        var good = """{ "startWithWindows": false }""";
        File.WriteAllText(_paths.SettingsFile, good);

        SettingsAtStart start;

        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var store = Store();
            var loaded = store.Load();

            Assert.True(loaded.Blocked);
            start = store.PrepareForStart(loaded, When);
        }

        Assert.True(start.SavesRefused);
        Assert.False(start.KeptAside);
        Assert.Empty(Backups());
        Assert.Equal(good, File.ReadAllText(_paths.SettingsFile));
        Assert.Equal(new DashboardSettings(), start.Original.Settings);

        // The file is readable again now, so only the store's guard can stop a save.
        Assert.False(Store().Save(new DashboardSettings()));
        Assert.Equal(good, File.ReadAllText(_paths.SettingsFile));
        Assert.Contains(_log.Events, entry => entry.Level == LogEventLevel.Warning
            && entry.MessageTemplate.Text.StartsWith("Did not save", StringComparison.Ordinal));

        Assert.Equal(SettingsNotice.NotOpenedText(_paths.Root), new SettingsNotice(start, _paths).Text);
    }

    /// <summary>
    /// A keep-aside that fails half-way (the fresh file cannot be written) leaves the bad file where
    /// it was, writes nothing, and refuses saves, like a file that could not be opened.
    /// </summary>
    [Fact]
    public void A_keep_aside_that_fails_leaves_the_file_and_refuses_saves()
    {
        File.WriteAllBytes(_paths.SettingsFile, BadBytes);
        Directory.CreateDirectory(_paths.SettingsFile + ".new");

        var store = Store();
        var start = store.PrepareForStart(store.Load(), When);

        Assert.True(start.SavesRefused);
        Assert.False(start.KeptAside);
        Assert.NotNull(start.KeepAsideProblem);
        Assert.Empty(Backups());
        Assert.Equal(Hash(BadBytes), HashOf(_paths.SettingsFile));
        Assert.False(Store().Save(new DashboardSettings()));
    }

    /// <summary>
    /// <strong>Every save site goes through the guard.</strong> The rosters, the Settings window and
    /// the <c>installHooksAtStart</c> record each refuse once saves are refused; the window's place at
    /// quit is the store's own <see cref="SettingsStore.Save"/>, asserted above.
    /// </summary>
    [Fact]
    public void Every_save_site_refuses_once_saves_are_refused()
    {
        var good = """{ "startWithWindows": false, "installHooksAtStart": true }""";
        File.WriteAllText(_paths.SettingsFile, good);

        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var store = Store();
            store.PrepareForStart(store.Load(), When);
        }

        // The rosters.
        var book = RosterBook.From([("orchestration", ["s-1", "s-2"])]);
        new SettingsRosterPersistence(Store(), _logger).Remember(book);
        Assert.Equal(good, File.ReadAllText(_paths.SettingsFile));

        // The installHooksAtStart record of a switch.
        Assert.False(StartupHookInstall.RecordSwitch(HookSwitches.Remove, 0, Store(), _logger));
        Assert.Equal(good, File.ReadAllText(_paths.SettingsFile));

        // The Settings window.
        var settings = SettingsWindowFor(Store());
        settings.StartsWithWindows = !settings.StartsWithWindows;
        Assert.Equal(good, File.ReadAllText(_paths.SettingsFile));
    }

    /// <summary>The Settings window's view model over <paramref name="store"/>, with a fake registry.</summary>
    private SettingsViewModel SettingsWindowFor(SettingsStore store)
    {
        var startup = new StartWithWindows(new FakeStartupRegistry(), @"C:\Program Files\ClaudeDashboard\current\ClaudeDashboard.App.exe", _logger);
        var model = new SettingsViewModel(startup, store, _logger);
        model.Refresh();

        return model;
    }

    // ---- This start registers no plugin ----------------------------------------------------------

    /// <summary>
    /// <strong>This start registers no plugin</strong>, though the fresh file says
    /// <c>installHooksAtStart: true</c>: the start's first load is the authority (T1.32's guard).
    /// The "fix or delete the file" notice is not shown after a rename.
    /// </summary>
    [Fact]
    public void A_start_that_kept_its_file_aside_registers_no_plugin()
    {
        var start = KeepAsideABadFile();
        var cli = new FakeClaudeCli(_claude);
        var notice = new HookNotice();

        Assert.True(Store().Load().Settings.InstallHooksAtStart, "The fresh file should say true; that is the trap.");

        var outcome = StartupHookInstall.RunAtStart(
            new HookCheck(_claude, _paths, _logger), start, _logger, new PluginInstaller(cli, _paths, _logger), notice);

        Assert.Equal(HookStartOutcome.OptOutUnknown, outcome);
        Assert.Empty(cli.Calls);
        Assert.False(notice.IsShown, "The opt-out notice says to fix or delete the file, which is wrong after a rename.");
    }

    /// <summary>With the plugin already enabled, nothing is registered either, and nothing is changed.</summary>
    [Fact]
    public void A_start_that_kept_its_file_aside_leaves_an_enabled_plugin_alone()
    {
        var start = KeepAsideABadFile();
        var cli = new FakeClaudeCli(_claude);
        var enabled =
            "{\"extraKnownMarketplaces\":{\"claude-dashboard\":{\"source\":{\"source\":\"directory\",\"path\":"
            + System.Text.Json.JsonSerializer.Serialize(_paths.PluginFolder)
            + "}}},\"enabledPlugins\":{\"claude-dashboard@claude-dashboard\":true}}";
        File.WriteAllText(_claude.UserSettingsFile, enabled);

        var outcome = StartupHookInstall.RunAtStart(
            new HookCheck(_claude, _paths, _logger), start, _logger, new PluginInstaller(cli, _paths, _logger), new HookNotice());

        Assert.Equal(HookStartOutcome.Connected, outcome);
        Assert.Empty(cli.Calls);
        Assert.Equal(enabled, File.ReadAllText(_claude.UserSettingsFile));
    }

    /// <summary>The cannot-open case keeps the opt-out notice, as before T1.56.</summary>
    [Fact]
    public void A_start_that_could_not_open_its_file_still_shows_the_opt_out_notice()
    {
        File.WriteAllText(_paths.SettingsFile, "{}");
        SettingsAtStart start;

        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var store = Store();
            start = store.PrepareForStart(store.Load(), When);
        }

        var cli = new FakeClaudeCli(_claude);
        var notice = new HookNotice();

        StartupHookInstall.RunAtStart(
            new HookCheck(_claude, _paths, _logger), start, _logger, new PluginInstaller(cli, _paths, _logger), notice);

        Assert.Empty(cli.Calls);
        Assert.Equal(HookNoticeKind.OptOutUnknown, notice.Kind);
    }

    // ---- The notice ------------------------------------------------------------------------------

    /// <summary>The notice names the backup and the folder, and shows no setting and no parse text.</summary>
    [Fact]
    public void The_notice_names_the_backup_and_the_folder_and_nothing_from_the_file()
    {
        var start = KeepAsideABadFile();
        var notice = new SettingsNotice(start, _paths);

        Assert.True(notice.IsShown);
        Assert.Equal(SettingsNotice.KeptAsideText(BackupName, _paths.Root), notice.Text);
        Assert.Equal(SettingsNotice.TrayShort, notice.TrayText);
        Assert.Contains(BackupName, notice.Text, StringComparison.Ordinal);
        Assert.Contains(_paths.Root, notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(start.Original.Problem!, notice.Text, StringComparison.Ordinal);
    }
}
