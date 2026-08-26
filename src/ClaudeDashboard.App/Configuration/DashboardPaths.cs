using System.IO;
namespace ClaudeDashboard.App.Configuration;

/// <summary>Where the root of the data folder came from (Impl Part 8).</summary>
public enum DataFolderSource
{
    /// <summary>The default under <c>%LOCALAPPDATA%</c>. No override was set.</summary>
    Default = 1,

    /// <summary>The caller named the folder directly. Tests do this; the app does not.</summary>
    Provided = 2,

    /// <summary><c>CLAUDE_DASHBOARD_HOME</c> was set and usable.</summary>
    Override = 3,

    /// <summary>
    /// <c>CLAUDE_DASHBOARD_HOME</c> was set and unusable, so the default is in force.
    /// <see cref="DashboardPaths.RootProblem"/> says why.
    /// </summary>
    RejectedOverride = 4,
}

/// <summary>
/// Where the dashboard keeps its files: <c>%LOCALAPPDATA%\ClaudeDashboard\</c>, or wherever
/// <c>CLAUDE_DASHBOARD_HOME</c> says (Impl Part 8).
/// </summary>
/// <remarks>
/// <para>
/// The root is injectable so tests exercise the real filesystem in a temporary directory rather
/// than a mocked one — the point of these paths is what they resolve to on disk, which a double
/// cannot tell you.
/// </para>
/// <para>
/// <strong>The override exists because there is otherwise no way to move this folder.</strong>
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> resolves the known folder
/// through the shell and ignores the <c>LOCALAPPDATA</c> environment variable — measured, not
/// assumed. So redirecting the environment relocates nothing, and a portable install, a roaming
/// profile that should not keep data under <c>Local</c>, and a second instance under test all
/// need somewhere else to put it.
/// </para>
/// <para>
/// <strong>This type cannot log.</strong> It is constructed before the logger exists — the
/// logger's own folder is one of the things it resolves. So a rejected override is recorded on
/// <see cref="RootSource"/> and <see cref="RootProblem"/> and reported by
/// <c>AppHost</c> at startup, the same shape <c>SettingsLoadResult</c> uses for the same reason.
/// </para>
/// </remarks>
public sealed class DashboardPaths
{
    /// <summary>The folder name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string FolderName = "ClaudeDashboard";

    /// <summary>The environment variable that overrides <see cref="Root"/> (Impl Part 8).</summary>
    public const string HomeVariable = "CLAUDE_DASHBOARD_HOME";

    /// <summary>
    /// Uses <c>CLAUDE_DASHBOARD_HOME</c> if it is set and usable, otherwise
    /// <c>%LOCALAPPDATA%\ClaudeDashboard\</c>.
    /// </summary>
    public DashboardPaths()
    {
        Root = ResolveRoot(
            Environment.GetEnvironmentVariable(HomeVariable),
            DefaultRoot,
            out var source,
            out var problem);

        RootSource = source;
        RootProblem = problem;
    }

    /// <summary>Uses <paramref name="root"/> as the data folder.</summary>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty, or whitespace.</exception>
    public DashboardPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("The data folder needs a path.", nameof(root));
        }

        Root = root;
        RootSource = DataFolderSource.Provided;
    }

    /// <summary>The default root, ignoring any override.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>The data folder itself.</summary>
    public string Root { get; }

    /// <summary>Where <see cref="Root"/> came from — reported at startup so the operator can see it.</summary>
    public DataFolderSource RootSource { get; }

    /// <summary>Why an override was rejected, when one was. Null otherwise.</summary>
    public string? RootProblem { get; }

    /// <summary>The human-editable settings file (Impl Part 8).</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// The port actually bound, in plain text, so a command-style hook can rediscover the URL
    /// (Impl Part 8, Part 9).
    /// </summary>
    /// <remarks>
    /// The dashboard's own file, which is why it lives here and Claude Code's settings do not.
    /// It records what was <em>bound</em>, not what was configured: the two differ whenever the
    /// operator overrides the port, and a file naming a port nothing is listening on would send a
    /// reader somewhere useless.
    /// </remarks>
    public string PortFile => Path.Combine(Root, "port.txt");

    /// <summary>The durable event log (Impl Part 8).</summary>
    /// <remarks>
    /// Under the same root as everything else, so <c>CLAUDE_DASHBOARD_HOME</c> moves it too. It is
    /// the one file here that contains the operator's prompts and Claude's answers, and it is
    /// unpruned until Phase 5 — see <c>SqliteEventStore</c> for what that costs per day and why no
    /// explicit ACL is set on it.
    /// </remarks>
    public string DatabaseFile => Path.Combine(Root, "dashboard.db");

    /// <summary>The rolling log folder — "the only console a resident app has" (Impl Part 8).</summary>
    public string LogFolder => Path.Combine(Root, "logs");

    /// <summary>The rolling log file template; Serilog appends the date and any size-roll suffix.</summary>
    public string LogFile => Path.Combine(LogFolder, "dashboard-.log");

    /// <summary>
    /// Where the operator's own sound files go, overriding the ones that ship (Impl Part 8).
    /// </summary>
    /// <remarks>
    /// A file here named for a <c>SoundId</c> — <c>finished.wav</c>, <c>error.wav</c> — replaces
    /// the shipped one. This folder is not created at startup: its absence is the ordinary case
    /// and means "no overrides", which is a different thing from a folder that failed to appear.
    /// </remarks>
    public string SoundFolder => Path.Combine(Root, "sounds");

    /// <summary>
    /// Where the sounds that ship with the app live — beside the executable, not under the
    /// operator's data folder.
    /// </summary>
    /// <remarks>
    /// <see cref="AppContext.BaseDirectory"/> rather than the current directory, which for a
    /// tray app started from a shortcut is wherever the shell felt like. It is a property rather
    /// than a constant so a test can see the same value the app does.
    /// </remarks>
    public static string ShippedSoundFolder => Path.Combine(AppContext.BaseDirectory, "sounds");

    /// <summary>
    /// Decides what <c>CLAUDE_DASHBOARD_HOME</c> resolves to, falling back to
    /// <paramref name="fallback"/> for anything unusable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws. A typo in an environment variable must not stop the dashboard starting, for
    /// the same reason a typo in <c>settings.json</c> must not (see <c>SettingsStore</c>): the
    /// operator has no console and no window, and a process that refuses to start does not
    /// present as a configuration error — it presents as the dashboard being gone.
    /// </para>
    /// <para>
    /// <strong>An override must be creatable, not merely well-formed.</strong> A path that
    /// parses but cannot be made is the failure this actually protects against — a drive that is
    /// not mounted, a folder under someone else's profile. So the directory is created here
    /// rather than only at <see cref="TryEnsureCreated"/>, and a failure to create falls back.
    /// The creation is attempted only when an override is present, so the ordinary case still
    /// touches nothing at construction.
    /// </para>
    /// </remarks>
    internal static string ResolveRoot(
        string? overrideValue,
        string fallback,
        out DataFolderSource source,
        out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            // Absent is the ordinary case and is not a problem to report.
            source = DataFolderSource.Default;
            return fallback;
        }

        var candidate = overrideValue.Trim();

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                // A relative path would resolve against the current directory, which for a tray
                // app started from a shortcut or a scheduled task is whatever the shell chose.
                problem = "the value is not a fully qualified path";
                source = DataFolderSource.RejectedOverride;
                return fallback;
            }

            var full = Path.GetFullPath(candidate);
            Directory.CreateDirectory(full);
            source = DataFolderSource.Override;
            return full;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            problem = ex.Message;
            source = DataFolderSource.RejectedOverride;
            return fallback;
        }
    }

    /// <summary>Creates the data and log folders if they do not exist.</summary>
    /// <remarks>
    /// Returns false rather than throwing if they cannot be created. A dashboard that cannot
    /// write logs should still run — losing diagnostics is a smaller failure than not starting
    /// at all, and Impl §10.1 restarts a failed start on a one-minute loop the operator would
    /// only see as the app being gone.
    /// </remarks>
    public bool TryEnsureCreated(out string? failure)
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogFolder);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failure = ex.Message;
            return false;
        }
    }
}
