using System.IO;
namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// Where the dashboard keeps its files: <c>%LOCALAPPDATA%\ClaudeDashboard\</c> (Impl Part 8).
/// </summary>
/// <remarks>
/// The root is injectable so tests exercise the real filesystem in a temporary directory rather
/// than a mocked one — the point of these paths is what they resolve to on disk, which a double
/// cannot tell you.
/// </remarks>
public sealed class DashboardPaths
{
    /// <summary>The folder name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string FolderName = "ClaudeDashboard";

    /// <summary>Uses the real <c>%LOCALAPPDATA%\ClaudeDashboard\</c>.</summary>
    public DashboardPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName))
    {
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
    }

    /// <summary>The data folder itself.</summary>
    public string Root { get; }

    /// <summary>The human-editable settings file (Impl Part 8).</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The rolling log folder — "the only console a resident app has" (Impl Part 8).</summary>
    public string LogFolder => Path.Combine(Root, "logs");

    /// <summary>The rolling log file template; Serilog appends the date and any size-roll suffix.</summary>
    public string LogFile => Path.Combine(LogFolder, "dashboard-.log");

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
