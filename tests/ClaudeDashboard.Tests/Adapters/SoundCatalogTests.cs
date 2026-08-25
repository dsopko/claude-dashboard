using System.IO;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Adapters;

/// <summary>
/// Where a sound file comes from (Impl Part 7, Part 8).
/// </summary>
public sealed class SoundCatalogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _shipped;
    private readonly string _overrides;

    public SoundCatalogTests()
    {
        _shipped = Path.Combine(_root, "shipped");
        _overrides = Path.Combine(_root, "overrides");

        Directory.CreateDirectory(_shipped);
        Directory.CreateDirectory(_overrides);
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

    /// <summary>The shipped file is found when there is no override.</summary>
    [Fact]
    public void The_shipped_file_is_used_by_default()
    {
        var shipped = Write(_shipped, "finished.wav");

        Assert.Equal(shipped, Catalog().Resolve(SoundId.Finished));
    }

    /// <summary>
    /// <strong>An override wins.</strong> This is the whole feature: replace a sound by dropping
    /// a file in, never by editing anything.
    /// </summary>
    /// <remarks>
    /// Asserted with <em>both</em> files present, because "the override is used" is satisfied by
    /// a catalog that only ever looks in the config folder — which would silently lose every
    /// shipped sound the operator had not replaced.
    /// </remarks>
    [Fact]
    public void An_override_beats_the_shipped_file()
    {
        Write(_shipped, "finished.wav");
        var custom = Write(_overrides, "finished.wav");

        Assert.Equal(custom, Catalog().Resolve(SoundId.Finished));

        // …and a sound with no override still resolves to the shipped one.
        var shippedError = Write(_shipped, "error.wav");
        Assert.Equal(shippedError, Catalog().Resolve(SoundId.Error));
    }

    /// <summary>Neither folder having it is null, not a path that does not exist.</summary>
    /// <remarks>
    /// The caller turns null into silence and a log line. A fabricated path would push that
    /// decision into the audio layer, where it would arrive as a file-not-found exception on the
    /// consumer thread.
    /// </remarks>
    [Fact]
    public void A_sound_nobody_shipped_resolves_to_nothing()
    {
        Assert.Null(Catalog().Resolve(SoundId.Question));
    }

    /// <summary>A missing override folder is the ordinary case, not a failure.</summary>
    [Fact]
    public void A_missing_override_folder_is_not_an_error()
    {
        Directory.Delete(_overrides, recursive: true);
        var shipped = Write(_shipped, "permission.wav");

        Assert.Equal(shipped, Catalog().Resolve(SoundId.Permission));
    }

    /// <summary>
    /// The app's own folders come from <see cref="DashboardPaths"/>, so paths resolve in one
    /// place (Impl Part 8).
    /// </summary>
    [Fact]
    public void The_folders_come_from_DashboardPaths()
    {
        var paths = new DashboardPaths(_root);

        Assert.Equal(Path.Combine(_root, "sounds"), paths.SoundFolder);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "sounds"), DashboardPaths.ShippedSoundFolder);

        // …and the shipped folder really has the sounds in it, so the app that resolves against
        // it finds something. This is what catches the build no longer copying the assets.
        Assert.True(
            Directory.Exists(DashboardPaths.ShippedSoundFolder),
            $"No sounds folder beside the tests at {DashboardPaths.ShippedSoundFolder}.");

        var catalog = new SoundCatalog(paths);

        foreach (var sound in new[] { SoundId.Finished, SoundId.Permission, SoundId.Question, SoundId.Error })
        {
            Assert.True(catalog.Resolve(sound) is not null, $"No shipped file for {sound.Name}.");
        }
    }

    [Fact]
    public void It_needs_its_folders()
    {
        Assert.Throws<ArgumentNullException>(() => new SoundCatalog((DashboardPaths)null!));
        Assert.Throws<ArgumentNullException>(() => new SoundCatalog(null!, _shipped));
        Assert.Throws<ArgumentNullException>(() => new SoundCatalog(_overrides, null!));
    }

    private SoundCatalog Catalog() => new(_overrides, _shipped);

    private static string Write(string folder, string file)
    {
        var path = Path.Combine(folder, file);
        File.WriteAllText(path, "not really audio; the catalog only looks for the file");

        return path;
    }
}
