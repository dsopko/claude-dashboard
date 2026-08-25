using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// Where a <see cref="SoundId"/>'s audio file lives (Impl Part 7, Part 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Override first, shipped second.</strong> A file named for the sound under the config
/// directory's <c>sounds</c> folder wins; otherwise the one that ships beside the executable is
/// used. That ordering is the whole feature — the operator replaces a sound by dropping a file
/// in, and never by editing anything.
/// </para>
/// <para>
/// Resolution is a separate object from playback because it is the half that can be asserted
/// exactly: which path, for which id, with and without an override present. What NAudio then
/// does with that path is the half that needs a device.
/// </para>
/// <para>
/// <strong>Not sealed, and <see cref="Resolve"/> is virtual, for one reason.</strong> The player's
/// contract says it never throws, and the only way to assert that against the <em>unforeseen</em>
/// failure — as opposed to the three named ones — is to have something in the chain actually
/// throw. A test overrides this to do so. Without the seam that assertion is vacuous: it passes
/// against a Play in which nothing can throw, which is not the same as one that swallows what
/// does.
/// </para>
/// </remarks>
public class SoundCatalog
{
    private readonly string _overrideFolder;
    private readonly string _shippedFolder;

    /// <summary>Creates a catalog over the app's own folders.</summary>
    /// <param name="paths">Where the operator's overrides live.</param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public SoundCatalog(DashboardPaths paths)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).SoundFolder,
            DashboardPaths.ShippedSoundFolder)
    {
    }

    /// <summary>Creates a catalog over explicit folders, for tests.</summary>
    /// <param name="overrideFolder">Searched first.</param>
    /// <param name="shippedFolder">Searched second.</param>
    public SoundCatalog(string overrideFolder, string shippedFolder)
    {
        ArgumentNullException.ThrowIfNull(overrideFolder);
        ArgumentNullException.ThrowIfNull(shippedFolder);

        _overrideFolder = overrideFolder;
        _shippedFolder = shippedFolder;
    }

    /// <summary>The file extension every sound uses.</summary>
    /// <remarks>
    /// WAV, and only WAV. NAudio's <c>WaveFileReader</c> needs no codec, so a sound that plays on
    /// the developer's machine plays on the operator's — an MP3 would depend on a media stack a
    /// server SKU may not have, and the failure would be silence, which is the one failure this
    /// application cannot afford to make ambiguous.
    /// </remarks>
    public const string Extension = ".wav";

    /// <summary>
    /// The file to play for <paramref name="sound"/>, or null if neither folder has one.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw or a fabricated path: a missing sound is a degradation the
    /// caller logs and moves past, not an error. The caller is what turns null into silence.
    /// </remarks>
    /// <param name="sound">The sound wanted.</param>
    public virtual string? Resolve(SoundId sound)
    {
        var file = sound.Name + Extension;

        var custom = Path.Combine(_overrideFolder, file);

        if (File.Exists(custom))
        {
            return custom;
        }

        var shipped = Path.Combine(_shippedFolder, file);

        return File.Exists(shipped) ? shipped : null;
    }
}
