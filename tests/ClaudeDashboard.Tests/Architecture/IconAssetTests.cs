using System.IO;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// The application icon of issue #17: that it is declared, and that it holds the four sizes
/// Windows asks for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no behaviour here, and that is exactly why this exists.</strong>
/// <c>ApplicationIcon</c> is one line in a project file full of commented properties, and the
/// way it breaks is somebody deleting it while tidying. Nothing fails: the build succeeds, the
/// tests stay green, and the application quietly goes back to the stock .NET icon — a change
/// nobody sees until they next look at a taskbar, which may be months.
/// </para>
/// <para>
/// <strong>What is deliberately not asserted.</strong> Whether the icon reads well is not a
/// property of the bytes. A crop off by three pixels, a corner knock-out that leaked into the
/// interior, a 16 px image that turned to mud — every one of those produces a valid ICO with
/// the right four sizes and passes here. Those were checked by looking at the images, and the
/// result is written down in the task's status report. This file guards the wiring only, and
/// says so rather than implying more.
/// </para>
/// <para>
/// Declaration-level, like the rest of this folder: the source is what can silently stop being
/// true, and reading it catches the change on the commit that makes it.
/// </para>
/// </remarks>
public sealed class IconAssetTests
{
    /// <summary>The four sizes, and why each one: 16 title bar, 32/48 taskbar and Alt-Tab, 256 Explorer.</summary>
    private static readonly int[] Expected = [256, 48, 32, 16];

    /// <summary>
    /// <strong>The project names the icon, and the file it names is there.</strong>
    /// </summary>
    /// <remarks>
    /// Both halves, because they fail apart. A property pointing at nothing builds without an
    /// icon and without complaint; a file present but unreferenced is 280 KB of dead weight that
    /// looks like it is working.
    /// </remarks>
    [Fact]
    public void The_executable_declares_the_icon_it_ships()
    {
        Assert.Equal(@"Assets\app.ico", Declared());
        Assert.True(IconFile().Exists, $"{IconFile().FullName} does not exist.");
    }

    /// <summary>
    /// <strong>The icon carries exactly 256, 48, 32 and 16 — no more and no fewer.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly, rather than "at least". A missing 16 makes Windows scale the 32 down and the
    /// title bar goes soft; a stray extra size is a sign somebody rebuilt the file with a
    /// different command than the one recorded in the project file, and the next rebuild would
    /// not reproduce it.
    /// </para>
    /// <para>
    /// The header is read as well, because a file that is not an ICO at all still satisfies "it
    /// exists" — and <c>ApplicationIcon</c> pointing at a renamed PNG fails the build with a
    /// message about resource compilation that names no cause.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_icon_carries_the_four_sizes_windows_asks_for()
    {
        var bytes = File.ReadAllBytes(IconFile().FullName);

        Assert.True(bytes.Length >= 6, $"Too short to be an ICO: {bytes.Length} byte(s).");
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));  // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));  // type: 1 is an icon, 2 a cursor

        int count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(
            bytes.Length >= 6 + (count * 16),
            $"The header claims {count} image(s), which will not fit in {bytes.Length} byte(s).");

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);

            // The width and height bytes hold 256 as 0, because the field is one byte wide.
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];

            Assert.Equal(width, height);
            sizes.Add(width);
        }

        // Joined rather than compared as sequences so a failure reads "256, 48, 32" against
        // "256, 48, 32, 16" — the missing size named, rather than an index and a count.
        Assert.Equal(string.Join(", ", Expected), string.Join(", ", sizes.OrderDescending()));
    }

    /// <summary>What <c>ApplicationIcon</c> is set to, as the build sees it after every import.</summary>
    private static string? Declared() => RepoLayout.EffectiveProperty(RepoLayout.App, "ApplicationIcon");

    /// <summary>The icon file the project points at, resolved the way MSBuild resolves it.</summary>
    /// <remarks>
    /// Followed from the property rather than hard-coded, so this asserts the file that actually
    /// ships. A test that read a fixed path would keep passing while the property pointed
    /// somewhere else entirely.
    /// </remarks>
    private static FileInfo IconFile()
    {
        var declared = Declared();
        Assert.False(
            string.IsNullOrWhiteSpace(declared),
            "ClaudeDashboard.App.csproj sets no <ApplicationIcon>, so the application ships the stock .NET icon.");

        var project = RepoLayout.Project(RepoLayout.App).Directory!;
        return new FileInfo(Path.Combine(project.FullName, declared!.Replace('\\', Path.DirectorySeparatorChar)));
    }
}
