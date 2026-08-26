using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Reading, merging and writing Claude Code's settings without ever leaving it broken (Impl §9.3).
/// </summary>
/// <remarks>
/// <para>
/// Real files in a temporary directory. Every claim here is about what is on disk after something
/// went wrong, and a mocked filesystem would assert none of it.
/// </para>
/// <para>
/// <strong>"The file still parses" is not an assertion.</strong> An empty object parses. So every
/// survival check names content that could only have come from the writer it belongs to.
/// </para>
/// </remarks>
public sealed class SettingsFileWriterTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 8, 26, 3, 30, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public SettingsFileWriterTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "settings.json");
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

    [Fact]
    public void A_merge_is_written_to_disk()
    {
        File.WriteAllText(_path, """{ "model": "opus" }""");

        var result = new SettingsFileWriter(_path).Modify(s => s["added"] = "yes", At);

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);

        var written = HookRegistration.Parse(File.ReadAllText(_path));
        Assert.Equal("yes", written["added"]!.GetValue<string>());
        Assert.Equal("opus", written["model"]!.GetValue<string>());
    }

    /// <summary>A merge that changes nothing writes nothing at all.</summary>
    /// <remarks>
    /// Asserted on the file's last-write time, not on the outcome alone: the outcome is a claim
    /// about what the writer decided, and this is a claim about whether the operator's file was
    /// touched. Halving the writes to a file every session on the machine is reading is the point
    /// of the distinction.
    /// </remarks>
    [Fact]
    public void A_merge_that_changes_nothing_does_not_touch_the_file()
    {
        var original = HookRegistration.Render(HookRegistration.Parse("""{ "model": "opus" }"""));
        File.WriteAllText(_path, original);
        var stampBefore = File.GetLastWriteTimeUtc(_path);

        var result = new SettingsFileWriter(_path).Modify(_ => { }, At);

        Assert.Equal(SettingsWriteOutcome.NothingToDo, result.Outcome);
        Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void A_missing_file_is_created()
    {
        var result = new SettingsFileWriter(_path).Modify(s => s["model"] = "opus", At);

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.Equal("opus", HookRegistration.Parse(File.ReadAllText(_path))["model"]!.GetValue<string>());
    }

    /// <summary>
    /// A malformed file is left exactly as it is, and nothing is written.
    /// </summary>
    /// <remarks>
    /// The opposite of the dashboard's own <c>SettingsStore</c>, and deliberately. There, defaults
    /// on a parse failure keep the dashboard starting. Here, "could not parse, use defaults" would
    /// write back an object with every hook, permission and preference of the operator's gone —
    /// destroying both their settings and the evidence of what was wrong with them.
    /// </remarks>
    [Fact]
    public void A_malformed_file_is_left_untouched()
    {
        const string Broken = "{ \"model\": }";
        File.WriteAllText(_path, Broken);

        var result = new SettingsFileWriter(_path).Modify(s => s["added"] = "yes", At);

        Assert.Equal(SettingsWriteOutcome.Unreadable, result.Outcome);
        Assert.Equal(Broken, File.ReadAllText(_path));
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }

    // ---- The backup ------------------------------------------------------------------------------

    /// <summary>A backup exists before the file changes, and is a plain restorable copy.</summary>
    /// <remarks>
    /// Restorability is asserted by actually restoring it over a scratch target and comparing
    /// bytes — not by the file existing. A backup nobody has ever restored is a hope.
    /// </remarks>
    [Fact]
    public void A_backup_is_taken_before_the_first_write_and_restores_byte_for_byte()
    {
        const string Original = """{ "model": "opus", "hooks": { "Stop": [] } }""";
        File.WriteAllText(_path, Original);

        var result = new SettingsFileWriter(_path).Modify(s => s["added"] = "yes", At);

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        // The backup holds what was there before, and the live file does not.
        Assert.Equal(Original, File.ReadAllText(result.BackupPath!));
        Assert.NotEqual(Original, File.ReadAllText(_path));

        var restored = Path.Combine(_root, "restored.json");
        File.Copy(result.BackupPath!, restored);
        Assert.Equal(File.ReadAllBytes(result.BackupPath!), File.ReadAllBytes(restored));
        Assert.Equal(Original, File.ReadAllText(restored));
    }

    /// <summary>An existing backup is never overwritten.</summary>
    /// <remarks>
    /// The operator has four hand-made backups beside their settings file. Two runs inside the
    /// same second must not destroy one, and neither must anything else.
    /// </remarks>
    [Fact]
    public void A_second_backup_in_the_same_second_does_not_overwrite_the_first()
    {
        File.WriteAllText(_path, """{ "n": 1 }""");
        var writer = new SettingsFileWriter(_path);

        var first = writer.BackUp(At);
        File.WriteAllText(_path, """{ "n": 2 }""");
        var second = writer.BackUp(At);

        Assert.NotEqual(first, second);
        Assert.Equal("""{ "n": 1 }""", File.ReadAllText(first!));
        Assert.Equal("""{ "n": 2 }""", File.ReadAllText(second!));
    }

    [Fact]
    public void Backing_up_a_file_that_does_not_exist_produces_nothing() =>
        Assert.Null(new SettingsFileWriter(_path).BackUp(At));

    // ---- Losing the race -------------------------------------------------------------------------

    /// <summary>
    /// A writer that arrives inside the read-modify-write gap is merged onto, never over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A real second writer, inside the real gap.</strong> The merge callback runs between
    /// the read and the replace, so writing the file from inside it puts another writer exactly
    /// where a concurrent Claude Code would be — no fake, no injected seam.
    /// </para>
    /// <para>
    /// The intruder writes once and then stands aside, which is what makes the retry observable:
    /// the second attempt reads their content, merges onto it, and both changes survive. Asserting
    /// only that the file parses would be satisfied by our change silently deleting theirs.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_write_that_loses_the_race_merges_onto_the_winner_rather_than_over_it()
    {
        File.WriteAllText(_path, """{ "model": "opus" }""");

        var intrusions = 0;

        var result = new SettingsFileWriter(_path).Modify(
            settings =>
            {
                if (intrusions++ == 0)
                {
                    // Another process, between our read and our replace.
                    File.WriteAllText(_path, """{ "model": "opus", "theirs": "kept" }""");
                }

                settings["ours"] = "kept";
            },
            At);

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.Equal(2, result.Attempts);

        var final = HookRegistration.Parse(File.ReadAllText(_path));
        Assert.Equal("kept", final["theirs"]!.GetValue<string>());
        Assert.Equal("kept", final["ours"]!.GetValue<string>());
        Assert.Equal("opus", final["model"]!.GetValue<string>());
    }

    /// <summary>
    /// A writer that never stops wins, and we leave the file alone rather than forcing ours in.
    /// </summary>
    /// <remarks>
    /// The file must be the other writer's work, entire and valid. Abandoning a registration costs
    /// a dashboard that hears nothing and says so in its tray; forcing the write costs the operator
    /// whatever the other process was doing.
    /// </remarks>
    [Fact]
    public void A_write_that_keeps_losing_is_abandoned_and_leaves_the_file_valid()
    {
        File.WriteAllText(_path, """{ "model": "opus" }""");

        var round = 0;

        var result = new SettingsFileWriter(_path, attempts: 3).Modify(
            settings =>
            {
                File.WriteAllText(_path, $$"""{ "model": "opus", "theirs": {{++round}} }""");
                settings["ours"] = "lost";
            },
            At);

        Assert.Equal(SettingsWriteOutcome.Abandoned, result.Outcome);
        Assert.Equal(3, result.Attempts);

        var final = HookRegistration.Parse(File.ReadAllText(_path));
        Assert.Equal(3, final["theirs"]!.GetValue<int>());
        Assert.Null(final["ours"]);
    }

    /// <summary>An abandoned write leaves no temporary file behind.</summary>
    /// <remarks>
    /// Standing rule: test what a failed write leaves on disk, not merely that it reported failure.
    /// </remarks>
    [Fact]
    public void An_abandoned_write_leaves_no_temporary_file()
    {
        File.WriteAllText(_path, """{ "model": "opus" }""");

        new SettingsFileWriter(_path, attempts: 2).Modify(
            settings =>
            {
                File.WriteAllText(_path, """{ "model": "haiku" }""");
                settings["ours"] = "lost";
            },
            At);

        Assert.Empty(Directory.EnumerateFiles(_root, "settings.json.dashboard-tmp-*"));
    }

    // ---- What a crash between the write and the rename leaves -------------------------------------

    /// <summary>
    /// A crash after the temporary file is written and before it is moved leaves the original
    /// intact, and the leftover is swept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one moment that is not atomic. The temporary file is planted directly, which is exactly
    /// what such a crash leaves behind — the process is gone, so no code of ours could have tidied
    /// up on the way down.
    /// </para>
    /// <para>
    /// Both halves matter. The original being untouched is the safety property; the sweep is what
    /// stops an unexplained file accumulating beside the operator's settings, one per crash.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_crash_between_the_write_and_the_rename_leaves_the_original_and_is_swept()
    {
        const string Original = """{ "model": "opus" }""";
        File.WriteAllText(_path, Original);

        var orphan = _path + ".dashboard-tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(orphan, """{ "half": "written" }""");

        var writer = new SettingsFileWriter(_path);

        Assert.Equal(Original, File.ReadAllText(_path));

        var swept = writer.SweepAbandonedTemporaries();

        Assert.Equal(1, swept);
        Assert.False(File.Exists(orphan));
        Assert.Equal(Original, File.ReadAllText(_path));
    }

    /// <summary>The sweep takes only our temporaries, and nothing that looks a bit like one.</summary>
    [Fact]
    public void The_sweep_leaves_files_that_are_not_ours()
    {
        File.WriteAllText(_path, "{}");
        var theirs = Path.Combine(_root, "settings.json.bak-before-dashboard-hooks");
        File.WriteAllText(theirs, "{}");

        new SettingsFileWriter(_path).SweepAbandonedTemporaries();

        Assert.True(File.Exists(theirs), "A backup of the operator's is not a temporary of ours.");
    }

    // ---- The lock ---------------------------------------------------------------------------------

    /// <summary>The lock sits beside the settings file and is gone afterwards.</summary>
    /// <remarks>
    /// A lock file left behind would look like a crashed writer for ever. Opened
    /// <c>DeleteOnClose</c>, so the operating system removes it even if this process is killed.
    /// </remarks>
    [Fact]
    public void The_lock_file_does_not_outlive_the_write()
    {
        var writer = new SettingsFileWriter(_path);

        writer.Modify(s => s["model"] = "opus", At);

        Assert.False(File.Exists(writer.LockPath));
        Assert.Equal(_root, Path.GetDirectoryName(writer.LockPath));
    }

    /// <summary>A held lock does not stop the write — the fingerprint is what makes it safe.</summary>
    /// <remarks>
    /// The lock narrows the window between two dashboards; it is not what guarantees correctness,
    /// and treating it as a precondition would turn a contended moment into a failed registration.
    /// </remarks>
    [Fact]
    public void A_lock_held_by_somebody_else_does_not_prevent_the_write()
    {
        File.WriteAllText(_path, """{ "model": "opus" }""");
        var writer = new SettingsFileWriter(_path);

        using var held = new FileStream(writer.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var result = writer.Modify(s => s["added"] = "yes", At);

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.Equal("yes", HookRegistration.Parse(File.ReadAllText(_path))["added"]!.GetValue<string>());
    }

    [Fact]
    public void A_writer_needs_a_path_and_a_positive_attempt_count()
    {
        Assert.Throws<ArgumentException>(() => new SettingsFileWriter("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingsFileWriter(_path, attempts: 0));
    }
}
