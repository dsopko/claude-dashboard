using System.IO;
using System.Xml.Linq;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// The shipping shape: win-x64, self-contained, one file (Impl §10.2; T1.19).
/// </summary>
/// <remarks>
/// <para>
/// Declaration-level, like the rest of this folder, and for the same reason: what the build is
/// <em>told</em> to produce is the thing that can silently stop being true. Whether the published
/// artefact actually came out that way is not a property of the source and is not asserted here —
/// it is verified by publishing and inspecting the executable, which is recorded in the task's
/// status report because it needs a publish to observe.
/// </para>
/// <para>
/// <strong>The publish settings are conditioned on a RuntimeIdentifier, and that condition is
/// load-bearing rather than tidy.</strong> A RID set unconditionally makes every build
/// runtime-specific and moves the output under a RID folder, so the test project — which loads
/// the Core assembly from beside its own binaries — would look in a path that no longer exists.
/// The whole suite would go red for a reason having nothing to do with what it tests.
/// </para>
/// </remarks>
public sealed class PackagingTests
{
    private static XDocument AppProject() => RepoLayout.Load(RepoLayout.Project(RepoLayout.App));

    /// <summary>The property group that only applies when a runtime identifier is supplied.</summary>
    private static XElement PublishGroup() =>
        AppProject()
            .Descendants("PropertyGroup")
            .Single(group => group.Attribute("Condition")?.Value.Contains("RuntimeIdentifier", StringComparison.Ordinal) == true);

    private static string? Setting(string name) => PublishGroup().Element(name)?.Value;

    /// <summary>Self-contained and one file, which is what "no machine-wide runtime" means.</summary>
    /// <remarks>
    /// Impl §10.2 asks for a single-file, self-contained publish so the operator installs an
    /// executable rather than a runtime. Losing either turns the deliverable into something that
    /// works on this machine because a runtime happens to be installed on it.
    /// </remarks>
    [Fact]
    public void The_publish_is_self_contained_and_single_file()
    {
        Assert.Equal("true", Setting("SelfContained"));
        Assert.Equal("true", Setting("PublishSingleFile"));
    }

    /// <summary>
    /// Native libraries go inside the file too, or "single file" is not true.
    /// </summary>
    /// <remarks>
    /// Without this the managed assemblies are bundled and the native dependencies — NAudio's
    /// interop and H.NotifyIcon's shell calls among them — sit beside the executable as loose
    /// DLLs. The app still runs from its publish folder, so the failure only appears when someone
    /// copies "the exe" somewhere and it stops working.
    /// </remarks>
    [Fact]
    public void Native_libraries_are_bundled_rather_than_left_beside_the_executable() =>
        Assert.Equal("true", Setting("IncludeNativeLibrariesForSelfExtract"));

    /// <summary>
    /// The publish settings apply only when a runtime identifier is given.
    /// </summary>
    /// <remarks>
    /// The guard described in this class's remarks. Asserted on the condition itself, because the
    /// damage from losing it lands on every other test in the suite and would be read as anything
    /// but a packaging change.
    /// </remarks>
    [Fact]
    public void The_publish_settings_do_not_apply_to_an_ordinary_build()
    {
        var condition = PublishGroup().Attribute("Condition")!.Value;

        Assert.Contains("RuntimeIdentifier", condition, StringComparison.Ordinal);
        Assert.Contains("!=", condition, StringComparison.Ordinal);

        // …and the ordinary property group must not name one, which is the other way to break it.
        var unconditional = AppProject()
            .Descendants("PropertyGroup")
            .Where(group => group.Attribute("Condition") is null);

        Assert.DoesNotContain(unconditional, group => group.Element("RuntimeIdentifier") is not null);
    }

    /// <summary>
    /// The manifest is still declared, and it is the same file the elevation and DPI tests read.
    /// </summary>
    /// <remarks>
    /// Packaging is where a manifest quietly stops being applied — a single-file publish generates
    /// its own apphost, and whether the source manifest reaches it is a packaging behaviour rather
    /// than a compilation one. That half is verified against the published executable itself and
    /// reported with the task; this half is the declaration those two tests already depend on, so
    /// that "packaging changed and the manifest was dropped from the project" cannot pass quietly.
    /// </remarks>
    [Fact]
    public void The_application_manifest_is_still_declared()
    {
        var manifest = RepoLayout.EffectiveProperty(RepoLayout.App, "ApplicationManifest");

        Assert.Equal("app.manifest", manifest);
        Assert.True(
            new FileInfo(Path.Combine(
                RepoLayout.Project(RepoLayout.App).Directory!.FullName,
                manifest!)).Exists);
    }

    /// <summary>Not MSIX, and the reason is not preference (Impl §10.2).</summary>
    /// <remarks>
    /// Its sandboxing fights writing the scheduled task and merging Claude Code's settings, and
    /// this tool must do both. A packaging property that turned it on would break two features
    /// whose failures look nothing like packaging.
    /// </remarks>
    [Fact]
    public void The_app_is_not_packaged_as_msix()
    {
        var text = File.ReadAllText(RepoLayout.Project(RepoLayout.App).FullName);

        Assert.DoesNotContain("EnableMsixTooling", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WindowsPackageType", text, StringComparison.OrdinalIgnoreCase);
    }
}
