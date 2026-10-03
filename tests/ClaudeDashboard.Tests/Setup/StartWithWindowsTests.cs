using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Start with Windows (issue #36): the reconcile table, Windows' own off switch, the checkbox, the
/// uninstall hook — all through the registry seam, never the operator's real value.
/// </summary>
public sealed class StartWithWindowsTests
{
    private const string Exe = @"C:\Users\someone\AppData\Local\dsopko.ClaudeDashboard\current\ClaudeDashboard.App.exe";
    private const string Data = "\"" + Exe + "\"";
    private const string Name = StartWithWindows.ValueName;

    /// <summary>Windows Settings' "off" mark, as measured: 01 00 00 00 then a FILETIME.</summary>
    private static readonly byte[] SettingsOff = [0x01, 0, 0, 0, 0x50, 0xBF, 0x70, 0xE2, 0x68, 0x51, 0xDD, 0x01];

    /// <summary>Windows Settings' "on" mark, as measured: twelve zero bytes.</summary>
    private static readonly byte[] SettingsOn = new byte[12];

    private readonly FakeStartupRegistry _registry = new();
    private readonly RecordingLogSink _sink = new();
    private readonly Serilog.Core.Logger _logger;

    public StartWithWindowsTests() =>
        _logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();

    private StartWithWindows Installed() => new(_registry, Exe, _logger);

    private StartWithWindows Portable() => new(_registry, null, _logger);

    // ---- A start whose settings were unreadable (T1.56's review, the director's ruling of 2026-10-03)

    private static SettingsAtStart Unreadable(string? backup = null, bool refused = false, string? problem = null) =>
        new(
            new SettingsLoadResult(new DashboardSettings(), SettingsLoadOutcome.Unreadable, "bad"),
            BackupFile: backup,
            SavesRefused: refused,
            KeepAsideProblem: problem);

    /// <summary>
    /// <strong>A start whose settings were unreadable leaves the <c>Run</c> value and Windows' mark as
    /// it found them</strong>: the file kept aside, the file that could not be opened, and the rename
    /// that failed. The defaults say "on"; the operator's choice was in the file.
    /// </summary>
    [Theory]
    [InlineData("kept aside")]
    [InlineData("cannot open")]
    [InlineData("rename failed")]
    public void An_unreadable_start_leaves_the_run_value_and_the_mark_as_found(string kind)
    {
        var start = kind switch
        {
            "kept aside" => Unreadable(backup: @"C:datasettings.error-20261003-140509.json"),
            "cannot open" => Unreadable(refused: true),
            _ => Unreadable(refused: true, problem: "The process cannot access the file."),
        };

        // Absent, and Windows has it off: what an operator who unticked it would leave.
        _registry.Approval[Name] = SettingsOff;

        Assert.Equal(StartupReconcileOutcome.SettingsUnreadable, Installed().ReconcileAtStart(start));
        Assert.False(_registry.Run.ContainsKey(Name));
        Assert.Equal(SettingsOff, _registry.Approval[Name]);
        Assert.Empty(_registry.Writes);
    }

    /// <summary>The control: a normal start still reconciles from its settings.</summary>
    [Fact]
    public void A_normal_start_still_reconciles()
    {
        var start = new SettingsAtStart(new SettingsLoadResult(new DashboardSettings { StartWithWindows = true }, SettingsLoadOutcome.Loaded));

        Assert.Equal(StartupReconcileOutcome.Written, Installed().ReconcileAtStart(start));
        Assert.Equal(Data, _registry.Run[Name]);

        var missing = new SettingsAtStart(new SettingsLoadResult(new DashboardSettings { StartWithWindows = false }, SettingsLoadOutcome.Missing));

        Assert.Equal(StartupReconcileOutcome.Removed, Installed().ReconcileAtStart(missing));
        Assert.False(_registry.Run.ContainsKey(Name));
    }

    // ---- The table (issue #36, design point 2) ------------------------------------------------------

    [Fact]
    public void On_and_absent_writes_it()
    {
        Assert.Equal(StartupReconcileOutcome.Written, Installed().Reconcile(wanted: true));
        Assert.Equal(Data, _registry.Run[Name]);
    }

    [Fact]
    public void On_and_naming_another_program_rewrites_it()
    {
        _registry.Run[Name] = "\"C:\\Elsewhere\\Claude Dashboard.exe\"";

        Assert.Equal(StartupReconcileOutcome.Written, Installed().Reconcile(wanted: true));
        Assert.Equal(Data, _registry.Run[Name]);
    }

    [Fact]
    public void On_and_correct_does_nothing()
    {
        _registry.Run[Name] = Data;

        Assert.Equal(StartupReconcileOutcome.NothingToDo, Installed().Reconcile(wanted: true));
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void Off_and_present_removes_it()
    {
        _registry.Run[Name] = Data;

        Assert.Equal(StartupReconcileOutcome.Removed, Installed().Reconcile(wanted: false));
        Assert.False(_registry.Run.ContainsKey(Name));
    }

    [Fact]
    public void Off_and_absent_does_nothing()
    {
        Assert.Equal(StartupReconcileOutcome.NothingToDo, Installed().Reconcile(wanted: false));
        Assert.Empty(_registry.Writes);
    }

    // ---- Windows' own off switch (the operator's ruling 5) -------------------------------------------

    /// <summary>A start leaves Windows' mark alone, even with the setting on.</summary>
    [Fact]
    public void A_start_leaves_Windows_off_mark_alone()
    {
        _registry.Run[Name] = Data;
        _registry.Approval[Name] = SettingsOff;

        Assert.Equal(StartupReconcileOutcome.NothingToDo, Installed().Reconcile(wanted: true));
        Assert.Equal(SettingsOff, _registry.Approval[Name]);
        Assert.Empty(_registry.Writes);
    }

    /// <summary>
    /// A start that writes the value back still leaves Windows' off switch as it was. Windows keeps
    /// the mark after the value is gone: switched off in Task Manager, then unticked, then
    /// <c>startWithWindows</c> back on. The start must not quietly undo the operator's choice.
    /// </summary>
    [Fact]
    public void A_start_that_writes_the_value_back_leaves_Windows_off_mark_alone()
    {
        byte[] off = [.. SettingsOff];
        _registry.Approval[Name] = off;

        Assert.Equal(StartupReconcileOutcome.Written, Installed().Reconcile(wanted: true));

        Assert.Equal(Data, _registry.Run[Name]);
        Assert.Same(off, _registry.Approval[Name]);
        Assert.Equal(SettingsOff, _registry.Approval[Name]);
        Assert.DoesNotContain("DeleteApproval " + Name, _registry.Calls);
    }

    /// <summary>The checkbox shows the entry off when Windows has it off.</summary>
    [Fact]
    public void Windows_off_mark_reads_as_not_starting()
    {
        _registry.Run[Name] = Data;
        _registry.Approval[Name] = SettingsOff;

        var state = Installed().Read();

        Assert.True(state.Registered);
        Assert.True(state.WindowsDisabled);
        Assert.False(state.StartsAtSignIn);
    }

    /// <summary>Ticking the checkbox clears Windows' mark and writes the value.</summary>
    [Fact]
    public void Ticking_clears_Windows_off_mark()
    {
        _registry.Run[Name] = Data;
        _registry.Approval[Name] = SettingsOff;

        var state = Installed().Apply(on: true);

        Assert.False(_registry.Approval.ContainsKey(Name));
        Assert.Equal(Data, _registry.Run[Name]);
        Assert.True(state.StartsAtSignIn);
    }

    /// <summary>Unticking removes the value.</summary>
    [Fact]
    public void Unticking_removes_the_value()
    {
        _registry.Run[Name] = Data;

        var state = Installed().Apply(on: false);

        Assert.False(_registry.Run.ContainsKey(Name));
        Assert.False(state.StartsAtSignIn);
    }

    /// <summary>
    /// The mark's format: twelve bytes, "off" when the first byte's low bit is set. Windows Settings'
    /// bytes as measured on 25H2, and Task Manager's documented 02 and 03.
    /// </summary>
    [Fact]
    public void The_mark_is_read_as_Windows_writes_it()
    {
        Assert.True(StartWithWindows.IsSwitchedOff(SettingsOff));
        Assert.False(StartWithWindows.IsSwitchedOff(SettingsOn));
        Assert.True(StartWithWindows.IsSwitchedOff([0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.False(StartWithWindows.IsSwitchedOff([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartWithWindows.IsSwitchedOff(null));
        Assert.False(StartWithWindows.IsSwitchedOff([0x03]));
    }

    // ---- Only an installed copy registers -------------------------------------------------------------

    /// <summary>A copy that is not installed never reads or writes the registry, whatever it is asked.</summary>
    [Fact]
    public void A_copy_that_is_not_installed_never_touches_the_registry()
    {
        var copy = Portable();

        Assert.Equal(StartupReconcileOutcome.NotInstalled, copy.Reconcile(wanted: true));
        Assert.False(copy.Apply(on: true).Installed);
        Assert.False(copy.Read().Installed);
        Assert.Empty(_registry.Calls);
    }

    /// <summary>
    /// The test host is a build, not an install, so Velopack says so and there is no exe to register.
    /// This is the path every build run from the repository takes.
    /// </summary>
    [Fact]
    public void A_build_run_from_the_repository_is_not_an_installed_copy() =>
        Assert.Null(InstalledCopy.CurrentExe(_logger));

    /// <summary>Velopack's answer decides: only an install Setup made, with its exe in place, registers.</summary>
    [Fact]
    public void Only_an_install_that_Setup_made_has_an_exe_to_register()
    {
        const string Current = @"C:\Users\someone\AppData\Local\dsopko.ClaudeDashboard\current";
        static bool Present(string _) => true;

        Assert.Equal(Exe, InstalledCopy.ExeFor(false, "1.2.0", Current, Present, _logger));

        Assert.Null(InstalledCopy.ExeFor(true, "1.2.0", Current, Present, _logger));
        Assert.Null(InstalledCopy.ExeFor(false, null, Current, Present, _logger));
        Assert.Null(InstalledCopy.ExeFor(false, "1.2.0", "", Present, _logger));
        Assert.Null(InstalledCopy.ExeFor(false, "1.2.0", Current, _ => false, _logger));
    }

    // ---- a failure never blocks a start ---------------------------------------------------------------

    /// <summary>A registry that refuses costs one Warning, and nothing throws.</summary>
    [Fact]
    public void A_refusing_registry_is_one_warning_and_the_true_state()
    {
        _registry.Refuses = true;
        var copy = Installed();

        Assert.Equal(StartupReconcileOutcome.Failed, copy.Reconcile(wanted: true));
        Assert.Single(_sink.Events, entry => entry.Level == LogEventLevel.Warning);

        var state = copy.Read();

        Assert.False(state.StartsAtSignIn);
        Assert.NotNull(state.Problem);
    }

    // ---- The uninstall hook ---------------------------------------------------------------------------

    [Fact]
    public void Uninstall_removes_this_copys_value_and_its_mark()
    {
        _registry.Run[Name] = Data;
        _registry.Approval[Name] = SettingsOff;

        StartWithWindows.RemoveOnUninstall(_registry, Exe);

        Assert.False(_registry.Run.ContainsKey(Name));
        Assert.False(_registry.Approval.ContainsKey(Name));
    }

    /// <summary>Uninstalling one copy does not stop another copy from starting.</summary>
    [Fact]
    public void Uninstall_leaves_a_value_that_names_another_copy()
    {
        const string Other = "\"C:\\Other\\current\\ClaudeDashboard.App.exe\"";
        _registry.Run[Name] = Other;

        StartWithWindows.RemoveOnUninstall(_registry, Exe);

        Assert.Equal(Other, _registry.Run[Name]);
    }

    [Fact]
    public void Uninstall_removes_the_value_when_the_copy_is_unknown()
    {
        _registry.Run[Name] = Data;

        StartWithWindows.RemoveOnUninstall(_registry, exePath: null);

        Assert.False(_registry.Run.ContainsKey(Name));
    }

    [Fact]
    public void Uninstall_never_throws()
    {
        _registry.Refuses = true;

        StartWithWindows.RemoveOnUninstall(_registry, Exe);
    }
}
