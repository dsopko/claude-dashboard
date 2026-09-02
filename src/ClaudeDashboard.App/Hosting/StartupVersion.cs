using System.Reflection;
using Serilog;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// The one place the app names its own version (PKG.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in the app named its version before this, and PKG.4 and every later support
/// question reads it from the log.</strong> The build stamps the informational version —
/// <c>0.1.0+sha</c> as <c>build\package.ps1</c>'s <c>-Version</c> and the SDK's source-revision
/// stamping produce it — and one Information line carries it, first thing after the logger
/// exists, so it is the first line of every day's log.
/// </para>
/// <para>
/// <strong>Read from the App assembly by type, not from
/// <see cref="Assembly.GetEntryAssembly"/>.</strong> In the product the two are the same
/// assembly; in the test process the entry assembly is the test host, so a test of the
/// entry-assembly read would assert the test runner's version and prove nothing about ours.
/// The attribute lives on this assembly either way.
/// </para>
/// <para>
/// <strong>The line carries the version and nothing else</strong> — no paths, no settings
/// (T1.24's rule applies even where nothing sensitive is nearby, because lines grow).
/// </para>
/// </remarks>
public static class StartupVersion
{
    /// <summary>The App assembly's informational version, as the build stamped it.</summary>
    public static string Value { get; } = Of(typeof(StartupVersion).Assembly);

    /// <summary>
    /// The informational version of <paramref name="assembly"/>, falling back to the assembly
    /// version when the attribute is absent.
    /// </summary>
    /// <remarks>
    /// The fallback exists for a build shape that strips the attribute; it loses the <c>+sha</c>
    /// half but still answers "which build is this" rather than logging an empty hole.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    /// <summary>Writes the one version line.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public static void Log(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        logger.Information("Claude Dashboard {Version:l}.", Value);
    }
}
