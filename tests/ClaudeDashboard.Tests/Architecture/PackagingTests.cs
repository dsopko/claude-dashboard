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
    /// <para>
    /// <strong>Measured, because two confident accounts of this were both wrong.</strong> Planting
    /// an unconditional <c>&lt;RuntimeIdentifier&gt;win-x64&lt;/RuntimeIdentifier&gt;</c> and
    /// building from a deleted <c>obj</c> and <c>bin</c>: the build <strong>succeeds</strong>, the
    /// app's output moves to <c>bin/Debug/net10.0-windows/win-x64/</c>, and the suite still
    /// passes — 1012 of 1013, with <em>this test</em> the only failure.
    /// </para>
    /// <para>
    /// So the first version of this remark was wrong to say the damage "lands on every other test
    /// in the suite": nothing else notices, because the test project takes its dependencies
    /// through a project reference rather than from that path. And the later suggestion that the
    /// assertion can never fire — on the grounds that an unconditional RID fails the build with
    /// <c>BG1002</c> — did not reproduce here from a clean tree.
    /// </para>
    /// <para>
    /// Both corrections pointed the same way, which is what makes the real answer worth writing
    /// down: the change is <strong>quiet</strong>. It builds, it tests, it moves every artefact
    /// path, and this assertion is the only thing that says so. That is a better reason to keep it
    /// than either of the ones it replaces.
    /// </para>
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
