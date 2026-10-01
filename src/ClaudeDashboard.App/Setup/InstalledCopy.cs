using System.IO;
using Serilog;
using Velopack.Locators;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// Whether this copy was installed by Setup, and where its exe lives if so — asked of Velopack, not
/// guessed from a path (issue #36).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Velopack 1.2.161's locator.</strong> <see cref="VelopackLocator.Current"/> is the
/// process-wide locator that <c>VelopackApp.Build().Run()</c> set up as the first statement of
/// <c>Main</c>. An installed copy has a <see cref="IVelopackLocator.CurrentlyInstalledVersion"/>, is
/// not <see cref="IVelopackLocator.IsPortable"/>, and keeps its exe in
/// <see cref="IVelopackLocator.AppContentDir"/> — the <c>current\</c> folder, whose path survives
/// every update. <c>UpdateManager.IsInstalled</c> says the same thing but needs an update source to
/// construct, and this dashboard has none.
/// </para>
/// <para>
/// <strong>Anything Velopack throws means "not installed".</strong> A build run from the repository
/// has no install for the locator to find, and the answer there must be "do not register", never a
/// failed start.
/// </para>
/// </remarks>
public static class InstalledCopy
{
    /// <summary>The exe Velopack starts, and the one the <c>Run</c> value names.</summary>
    public const string ExeName = "ClaudeDashboard.App.exe";

    /// <summary>This copy's exe under <c>current\</c>, or null when it is portable or not installed.</summary>
    public static string? CurrentExe(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var locator = VelopackLocator.Current;

            return ExeFor(
                locator.IsPortable,
                locator.CurrentlyInstalledVersion?.ToString(),
                locator.AppContentDir,
                File.Exists,
                logger);
        }
#pragma warning disable CA1031 // Velopack documents no exception set for the locator; any one means "not installed".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.Debug(ex, "Velopack found no install for this copy, so it never registers to start with Windows.");

            return null;
        }
    }

    /// <summary>
    /// The decision, over what Velopack reported: an install that Setup made, with a version and a
    /// <c>current\</c> folder that holds the exe. Anything else is not an installed copy.
    /// </summary>
    internal static string? ExeFor(bool isPortable, string? version, string? appContentDir, Func<string, bool> exists, ILogger logger)
    {
        if (isPortable || version is null || string.IsNullOrEmpty(appContentDir))
        {
            logger.Debug(
                "Not an installed copy (portable: {Portable}, version: {Version}), so it never registers to start with Windows.",
                isPortable,
                version ?? "none");

            return null;
        }

        var exe = Path.Combine(appContentDir, ExeName);

        return exists(exe) ? exe : null;
    }
}
