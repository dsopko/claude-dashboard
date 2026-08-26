using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Xml.Linq;

namespace ClaudeDashboard.App.Setup;

/// <summary>The result of a scheduled-task operation.</summary>
/// <param name="Succeeded">Whether <c>schtasks</c> reported success.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">What it printed, standard output and error together.</param>
public readonly record struct TaskCommandResult(bool Succeeded, int ExitCode, string Output);

/// <summary>
/// The logon task that starts the dashboard, and restarts it if it dies (Impl §10.1, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Defined as XML rather than as <c>schtasks</c> switches.</strong> The command-line form
/// cannot express restart-on-failure at all, and that is not a detail — Impl §10.1 specifies
/// "restart every 1 minute, up to 3 times", and it is the only thing that shortens the window in
/// which a crashed dashboard leaves its hooks registered with nothing listening.
/// </para>
/// <para>
/// <strong>Normal integrity, never elevated.</strong> <c>LeastPrivilege</c>, matching
/// <c>app.manifest</c>'s <c>asInvoker</c>: an elevated process cannot inspect the non-elevated
/// terminal windows this dashboard exists to watch (Impl §6.5), so raising it would break the
/// feature as well as the working agreement.
/// </para>
/// <para>
/// <strong>Verified by reading it back.</strong> A zero exit code from <c>schtasks</c> says the
/// command was accepted, not that the registration says what was intended — and every property
/// that matters here (the trigger, the run level, the restart policy, the path) is one the
/// operator would only discover was wrong at the next logon, by the dashboard not being there.
/// </para>
/// </remarks>
public static class LogonTask
{
    /// <summary>The task name the dashboard registers under.</summary>
    public const string DefaultTaskName = "ClaudeDashboard";

    /// <summary>Impl §10.1's restart policy: every minute, up to three times.</summary>
    public const string RestartInterval = "PT1M";

    /// <summary>…and the count that goes with it.</summary>
    public const int RestartCount = 3;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Builds the task definition for <paramref name="executablePath"/>.</summary>
    /// <param name="executablePath">The dashboard executable to start at logon.</param>
    /// <param name="userId">The account the trigger belongs to, as <c>DOMAIN\user</c>.</param>
    /// <remarks>
    /// Pure: it takes a path and a user and returns text. That is what lets the shape be asserted
    /// without registering anything on the machine running the tests.
    /// </remarks>
    /// <exception cref="ArgumentException">Either argument is null, empty, or whitespace.</exception>
    public static string BuildDefinition(string executablePath, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var task = new XElement(
            Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(
                Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Starts Claude Dashboard at logon and restarts it if it stops unexpectedly."),
                new XElement(Ns + "URI", "\\" + DefaultTaskName)),
            new XElement(
                Ns + "Triggers",
                new XElement(
                    Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "UserId", userId))),
            new XElement(
                Ns + "Principals",
                new XElement(
                    Ns + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userId),
                    new XElement(Ns + "LogonType", "InteractiveToken"),

                    // Never HighestAvailable. See this type's remarks: elevation breaks the
                    // feature, not only the working agreement.
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(
                Ns + "Settings",
                new XElement(
                    Ns + "RestartOnFailure",
                    new XElement(Ns + "Interval", RestartInterval),
                    new XElement(Ns + "Count", RestartCount.ToString(CultureInfo.InvariantCulture))),
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),

                // A tray app on a laptop must not vanish when the charger comes out, and must not
                // decline to start because it was already unplugged.
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),

                // It runs from logon to logoff. A time limit would stop it mid-afternoon.
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", 7)),
            new XElement(
                Ns + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(
                    Ns + "Exec",
                    new XElement(Ns + "Command", executablePath))));

        return new XDocument(new XDeclaration("1.0", "UTF-16", null), task).ToString();
    }

    /// <summary>The account the current process is running as, as <c>DOMAIN\user</c>.</summary>
    public static string CurrentUserId =>
        string.IsNullOrEmpty(Environment.UserDomainName)
            ? Environment.UserName
            : $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>Registers <paramref name="taskName"/> from a definition file.</summary>
    public static TaskCommandResult Register(string taskName, string definitionPath) =>
        Run($"/create /tn \"{taskName}\" /xml \"{definitionPath}\" /f");

    /// <summary>Reads the registration back as XML, which is the only way to verify it.</summary>
    public static TaskCommandResult ReadBack(string taskName) =>
        Run($"/query /tn \"{taskName}\" /xml ONE");

    /// <summary>Removes <paramref name="taskName"/>.</summary>
    public static TaskCommandResult Remove(string taskName) =>
        Run($"/delete /tn \"{taskName}\" /f");

    /// <summary>
    /// Reads the properties worth checking out of a registration.
    /// </summary>
    /// <remarks>
    /// Parsed from the XML Windows gives back rather than from what was sent, so that what is
    /// asserted is what the task scheduler stored.
    /// </remarks>
    public static LogonTaskFacts Describe(string registrationXml)
    {
        ArgumentNullException.ThrowIfNull(registrationXml);

        var root = XDocument.Parse(registrationXml).Root
            ?? throw new FormatException("The task registration had no root element.");

        string? Value(string name) => root.Descendants(Ns + name).FirstOrDefault()?.Value;

        var restart = root.Descendants(Ns + "RestartOnFailure").FirstOrDefault();

        return new LogonTaskFacts(
            Command: Value("Command"),
            RunLevel: Value("RunLevel"),
            HasLogonTrigger: root.Descendants(Ns + "LogonTrigger").Any(),
            RestartInterval: restart?.Element(Ns + "Interval")?.Value,
            RestartCount: int.TryParse(
                restart?.Element(Ns + "Count")?.Value,
                CultureInfo.InvariantCulture,
                out var count) ? count : null);
    }

    /// <summary>Runs <c>schtasks</c> and captures everything it said.</summary>
    /// <remarks>
    /// Never throws for a non-zero exit: a task that already exists, or a machine where the
    /// service is unavailable, is an outcome to report rather than an exception to propagate out
    /// of startup.
    /// </remarks>
    private static TaskCommandResult Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return new TaskCommandResult(false, -1, "schtasks.exe did not start.");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new TaskCommandResult(process.ExitCode == 0, process.ExitCode, output.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or SecurityException)
        {
            return new TaskCommandResult(false, -1, ex.Message);
        }
    }
}

/// <summary>The properties of a registered task that decide whether it will work.</summary>
/// <param name="Command">The executable the task starts.</param>
/// <param name="RunLevel">
/// The integrity level, or null when the registration does not name one. <strong>Null is the
/// ordinary case for a task that is not elevated</strong> — see <see cref="IsElevated"/>.
/// </param>
/// <param name="HasLogonTrigger">Whether it is triggered at logon.</param>
/// <param name="RestartInterval">How long it waits before restarting a failed run.</param>
/// <param name="RestartCount">How many times it will do so.</param>
public readonly record struct LogonTaskFacts(
    string? Command,
    string? RunLevel,
    bool HasLogonTrigger,
    string? RestartInterval,
    int? RestartCount)
{
    /// <summary>The run level Windows records for a task that asks for elevation.</summary>
    public const string ElevatedRunLevel = "HighestAvailable";

    /// <summary>
    /// Whether this task would run elevated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Asked as "is it elevated", never as "is it LeastPrivilege", and that is a
    /// measurement rather than a preference.</strong> Registering a task whose definition says
    /// <c>LeastPrivilege</c> and reading it back from Windows returns a principal with <em>no
    /// <c>RunLevel</c> element at all</em> — observed on 2026-08-26 against a real registration.
    /// Windows omits the default rather than storing it, and it rewrites the principal's user to a
    /// SID while it is there.
    /// </para>
    /// <para>
    /// So a verification that looked for <c>LeastPrivilege</c> in a read-back would fail on a
    /// perfectly correct task, and the tempting repair — dropping the check — would pass a task
    /// that had been changed to elevate. The only form that is true of both what is written and
    /// what is stored is the negative one.
    /// </para>
    /// </remarks>
    public bool IsElevated =>
        string.Equals(RunLevel, ElevatedRunLevel, StringComparison.OrdinalIgnoreCase);
}
