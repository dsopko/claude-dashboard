using System.Globalization;
using System.IO;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// Reads and writes <c>port.txt</c> — the port this user's dashboard last bound (Impl §3.1, Part 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It became an input as well as an output at T1.21.</strong> It was written so a
/// command-style hook could rediscover the URL, and nothing read it. §3.1's first attempt now
/// does: the port recorded here is the one this user tries first, which is what keeps a user on
/// the same port across restarts once the derivation has found them one.
/// </para>
/// <para>
/// <strong>The second instance reads it too, and that is load-bearing.</strong> §5.3 has a second
/// launch signal the first with <c>POST /show</c>. While the port was fixed, both processes knew
/// it from the settings file. With a per-user port they do not, and this file is the only thing
/// that tells the second launch where the first one actually is. A missing or unreadable
/// <c>port.txt</c> therefore degrades single-instance signalling to the base port, which is the
/// old behaviour and no worse than it.
/// </para>
/// <para>
/// <strong>Never fatal, in either direction.</strong> A dashboard that will not start because it
/// could not read a cache of its own last port would be trading a whole feature for a
/// convenience.
/// </para>
/// </remarks>
public static class PortFile
{
    /// <summary>The port recorded for this data folder, or null if there is not a usable one.</summary>
    /// <remarks>
    /// Null covers every way this can fail to answer — no file, an empty file, a hand-edited file
    /// with a word in it, a number outside the port range, or a folder that cannot be read. They
    /// are one case to the caller: <em>no recorded port</em>, fall through to the derivation.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static int? Read(DashboardPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            var text = File.ReadAllText(paths.PortFile).Trim();

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535
                    ? port
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Records the bound port. Returns false if it could not be written.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static bool Write(DashboardPaths paths, int port)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            File.WriteAllText(paths.PortFile, port.ToString(CultureInfo.InvariantCulture));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
