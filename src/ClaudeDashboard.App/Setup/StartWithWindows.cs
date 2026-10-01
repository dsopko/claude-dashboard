using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// The two registry places Windows reads for a program that starts at sign-in: the <c>Run</c> value,
/// and the mark Windows' own off switch writes beside it (issue #36).
/// </summary>
/// <remarks>
/// An interface because the real value is named <c>Claude Dashboard</c> and belongs to the
/// operator's install. Tests never touch it; they drive a fake through this seam.
/// </remarks>
public interface IStartupRegistry
{
    /// <summary>The <c>Run</c> value's data, or null when there is none.</summary>
    string? ReadRun(string name);

    /// <summary>Writes the <c>Run</c> value.</summary>
    void WriteRun(string name, string data);

    /// <summary>Deletes the <c>Run</c> value. Absent is not an error.</summary>
    void DeleteRun(string name);

    /// <summary>The <c>StartupApproved\Run</c> mark's bytes, or null when there is none.</summary>
    byte[]? ReadApproval(string name);

    /// <summary>Deletes the <c>StartupApproved\Run</c> mark. Absent is not an error.</summary>
    void DeleteApproval(string name);
}

/// <summary>The current user's hive: <c>HKCU</c> only, so no administrator rights, ever.</summary>
public sealed class WindowsStartupRegistry : IStartupRegistry
{
    /// <summary>Where Windows looks for programs to start at sign-in.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Windows Settings and Task Manager record that an entry is switched off.</summary>
    public const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <inheritdoc/>
    public string? ReadRun(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);

        return key?.GetValue(name) as string;
    }

    /// <inheritdoc/>
    public void WriteRun(string name, string data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

        key.SetValue(name, data, RegistryValueKind.String);
    }

    /// <inheritdoc/>
    public void DeleteRun(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <inheritdoc/>
    public byte[]? ReadApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey);

        return key?.GetValue(name) as byte[];
    }

    /// <inheritdoc/>
    public void DeleteApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey, writable: true);

        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>What a start did to the <c>Run</c> value (issue #36's table).</summary>
public enum StartupReconcileOutcome
{
    /// <summary>Not an installed copy: nothing is ever written.</summary>
    NotInstalled = 1,

    /// <summary>Already as the setting wants it.</summary>
    NothingToDo = 2,

    /// <summary>Written, because it was absent or named another program.</summary>
    Written = 3,

    /// <summary>Removed, because the setting is off.</summary>
    Removed = 4,

    /// <summary>The registry refused; one Warning was logged and the start went on.</summary>
    Failed = 5,
}

/// <summary>What Windows has for this dashboard, as the Settings window shows it.</summary>
/// <param name="Installed">Whether this copy can register at all.</param>
/// <param name="Registered">Whether the <c>Run</c> value is there and names this copy's exe.</param>
/// <param name="WindowsDisabled">Whether Windows' own switch has the entry off.</param>
/// <param name="Problem">Why the registry could not be read, or null.</param>
public readonly record struct StartupState(bool Installed, bool Registered, bool WindowsDisabled, string? Problem = null)
{
    /// <summary>Whether the dashboard will start at the next sign-in: what the checkbox shows.</summary>
    public bool StartsAtSignIn => Registered && !WindowsDisabled;
}

/// <summary>
/// Starts the dashboard when the operator signs in to Windows, through the <c>Run</c> key (issue
/// #36, the operator's rulings of 2026-10-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The setting is the truth, and each start makes Windows match it.</strong>
/// <c>startWithWindows</c> in the dashboard's settings, default on. <see cref="Reconcile"/> writes
/// the value when it is absent or names another program, removes it when the setting is off, and
/// otherwise does nothing. Only the checkbox changes the setting, so closing the dashboard or ending
/// it in Task Manager changes nothing.
/// </para>
/// <para>
/// <strong>Windows' own off switch is respected.</strong> Windows Settings › Apps › Startup and Task
/// Manager do not delete the value; they write a mark under <c>StartupApproved\Run</c>. Measured on
/// Windows 11 25H2 (build 26200), Windows Settings writes twelve bytes: <c>01 00 00 00</c> and an
/// eight-byte FILETIME when switched off, twelve zero bytes when switched back on. Task Manager is
/// documented to write <c>03</c> and <c>02</c> in the first byte. So "off" here is a twelve-byte mark
/// whose first byte has its low bit set. A start never touches the mark; the checkbox shows the entry
/// unticked with a line saying Windows has it off; and ticking the checkbox deletes the mark, which is
/// the state of an entry nobody ever switched off.
/// </para>
/// <para>
/// <strong>Only an installed copy registers</strong>, and its exe is the one under <c>current\</c>,
/// never the root stub, which Velopack renames after an update. A portable copy or a build run from
/// the repository has no path that will still be there tomorrow.
/// </para>
/// <para>
/// <strong>Nothing here may stop a start.</strong> A registry that refuses costs one Warning, and the
/// Settings window shows the true state.
/// </para>
/// </remarks>
public sealed class StartWithWindows
{
    /// <summary>The <c>Run</c> value's name. It is what Task Manager's Startup tab lists as the entry.</summary>
    public const string ValueName = "Claude Dashboard";

    private readonly IStartupRegistry _registry;
    private readonly ILogger _logger;

    /// <summary>Creates the switch for <paramref name="exePath"/>, or for no install when it is null.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="logger"/> is null.</exception>
    public StartWithWindows(IStartupRegistry registry, string? exePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;
        ExePath = exePath;
    }

    /// <summary>This installed copy's exe under <c>current\</c>, or null when it is not installed.</summary>
    public string? ExePath { get; }

    /// <summary>Whether this copy can register.</summary>
    public bool Installed => ExePath is not null;

    /// <summary>The <c>Run</c> data for <see cref="ExePath"/>: the path, quoted.</summary>
    public string? RunData => ExePath is null ? null : $"\"{ExePath}\"";

    /// <summary>Whether a <c>StartupApproved\Run</c> mark says Windows has the entry switched off.</summary>
    public static bool IsSwitchedOff(byte[]? mark) => mark is { Length: 12 } && (mark[0] & 1) == 1;

    /// <summary>What Windows has now. Never throws.</summary>
    /// <remarks>
    /// A copy that is not installed does not look at all: it can neither register nor change
    /// anything, and the value it would find belongs to an installed copy — the operator's, when this
    /// is a test or a build from the repository.
    /// </remarks>
    public StartupState Read()
    {
        if (RunData is null)
        {
            return new StartupState(Installed: false, Registered: false, WindowsDisabled: false);
        }

        try
        {
            var data = _registry.ReadRun(ValueName);

            return new StartupState(
                Installed,
                Registered: RunData is not null && string.Equals(data, RunData, StringComparison.OrdinalIgnoreCase),
                WindowsDisabled: IsSwitchedOff(_registry.ReadApproval(ValueName)));
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return new StartupState(Installed, Registered: false, WindowsDisabled: false, ex.Message);
        }
    }

    /// <summary>
    /// Makes the <c>Run</c> value match <paramref name="wanted"/>, at a start. Leaves Windows' mark
    /// alone. Never throws.
    /// </summary>
    public StartupReconcileOutcome Reconcile(bool wanted)
    {
        if (RunData is null)
        {
            return StartupReconcileOutcome.NotInstalled;
        }

        try
        {
            var data = _registry.ReadRun(ValueName);

            if (wanted)
            {
                if (string.Equals(data, RunData, StringComparison.OrdinalIgnoreCase))
                {
                    return StartupReconcileOutcome.NothingToDo;
                }

                _registry.WriteRun(ValueName, RunData);

                _logger.Information(
                    "Wrote {Value} under {Key} so the dashboard starts when you sign in to Windows " +
                    "(\"startWithWindows\" is on). It {Was}.",
                    ValueName,
                    WindowsStartupRegistry.RunKey,
                    data is null ? "was not there" : "named another program");

                return StartupReconcileOutcome.Written;
            }

            if (data is null)
            {
                return StartupReconcileOutcome.NothingToDo;
            }

            _registry.DeleteRun(ValueName);

            _logger.Information(
                "Removed {Value} from {Key} because \"startWithWindows\" is off.",
                ValueName,
                WindowsStartupRegistry.RunKey);

            return StartupReconcileOutcome.Removed;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            _logger.Warning(
                ex,
                "Could not make {Value} under {Key} match \"startWithWindows\" ({Wanted}). The dashboard " +
                "runs as usual; the Settings window shows what Windows has.",
                ValueName,
                WindowsStartupRegistry.RunKey,
                wanted);

            return StartupReconcileOutcome.Failed;
        }
    }

    /// <summary>
    /// What the checkbox does: on writes the value and deletes Windows' off mark; off removes the
    /// value. Returns the state afterwards. Never throws.
    /// </summary>
    public StartupState Apply(bool on)
    {
        if (RunData is null)
        {
            return Read();
        }

        try
        {
            if (on)
            {
                _registry.WriteRun(ValueName, RunData);
                _registry.DeleteApproval(ValueName);
            }
            else
            {
                _registry.DeleteRun(ValueName);
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            _logger.Warning(
                ex,
                "Could not {Action} {Value} under {Key}. The Settings window shows what Windows has.",
                on ? "write" : "remove",
                ValueName,
                WindowsStartupRegistry.RunKey);
        }

        return Read();
    }

    /// <summary>
    /// For Velopack's uninstall hook: removes the value and its mark, and never throws. Velopack ends
    /// the process when this returns, and kills it after 30 seconds, so it does one read and two
    /// registry deletes and nothing else.
    /// </summary>
    /// <remarks>
    /// <strong>Only an entry that names the copy being removed</strong>, when that copy is known. The
    /// value's name is shared by every install of the dashboard for this user, and uninstalling one
    /// must not stop another from starting. When Velopack cannot say which copy this is, the entry is
    /// removed anyway: an entry naming a program that is gone is the worse leftover.
    /// </remarks>
    /// <param name="registry">The registry.</param>
    /// <param name="exePath">The exe of the copy being uninstalled, or null when unknown.</param>
    public static void RemoveOnUninstall(IStartupRegistry registry, string? exePath)
    {
        ArgumentNullException.ThrowIfNull(registry);

        try
        {
            if (exePath is not null
                && registry.ReadRun(ValueName) is { } data
                && !string.Equals(data, $"\"{exePath}\"", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            registry.DeleteRun(ValueName);
            registry.DeleteApproval(ValueName);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // An uninstall must never fail on this. The worst case is an entry that names a program
            // which is gone, and Windows skips that at sign-in.
        }
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;
}
