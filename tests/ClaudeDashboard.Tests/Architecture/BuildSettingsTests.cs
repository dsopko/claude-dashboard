using System.IO;
using System.Reflection;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Proves the engineering standards from Execution Plan Part 1 are in effect for
/// <em>every</em> project, not merely declared in one of them: nullable reference
/// types on everywhere, analyzers on everywhere, warnings-as-errors on the product
/// projects (required for Core "at minimum").
/// </summary>
public sealed class BuildSettingsTests
{
    private static readonly string[] AllProjects =
    [
        RepoLayout.Core, RepoLayout.App, RepoLayout.Remote, RepoLayout.Tests,
    ];

    private static readonly string[] ProductProjects =
    [
        RepoLayout.Core, RepoLayout.App, RepoLayout.Remote,
    ];

    [Theory]
    [MemberData(nameof(EveryProject))]
    public void Every_project_enables_nullable_reference_types(string project)
    {
        Assert.Equal("enable", RepoLayout.EffectiveProperty(project, "Nullable"));
    }

    [Theory]
    [MemberData(nameof(EveryProject))]
    public void Every_project_enables_analyzers(string project)
    {
        Assert.Equal("true", RepoLayout.EffectiveProperty(project, "EnableNETAnalyzers"));

        var mode = RepoLayout.EffectiveProperty(project, "AnalysisMode");
        Assert.False(
            string.IsNullOrWhiteSpace(mode) || mode.Equals("None", StringComparison.OrdinalIgnoreCase),
            $"{project} must run analyzers at a real analysis mode; found '{mode}'.");
    }

    [Theory]
    [MemberData(nameof(EveryProductProject))]
    public void Product_projects_treat_warnings_as_errors(string project)
    {
        Assert.Equal("true", RepoLayout.EffectiveProperty(project, "TreatWarningsAsErrors"));
    }

    [Theory]
    [MemberData(nameof(EveryProject))]
    public void Every_project_targets_dotnet_10(string project)
    {
        var tfm = RepoLayout.EffectiveProperty(project, "TargetFramework");
        Assert.True(
            tfm is "net10.0" or "net10.0-windows",
            $"{project} must target .NET 10 (Impl 1.1); found '{tfm}'.");
    }

    /// <summary>
    /// The settings above are read from the build files; this reads the result. A
    /// nullable-annotated member only produces <c>NullableAttribute</c> metadata when the
    /// compiler genuinely had nullable context enabled, so its presence is proof the
    /// property took effect rather than merely being written down.
    /// </summary>
    [Fact]
    public void Nullable_context_is_actually_applied_by_the_compiler()
    {
        var probe = typeof(BuildSettingsTests).GetMethod(
            nameof(NullableProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var attributes = probe.ReturnParameter.GetCustomAttributesData()
            .Concat(probe.GetCustomAttributesData())
            .Select(a => a.AttributeType.FullName)
            .ToList();

        Assert.Contains(
            attributes,
            name => name is "System.Runtime.CompilerServices.NullableAttribute"
                         or "System.Runtime.CompilerServices.NullableContextAttribute");
    }

    /// <summary>Solution-wide settings must live in a shared props file, not be copy-pasted per project.</summary>
    [Fact]
    public void Shared_settings_come_from_a_repository_wide_props_file()
    {
        var shared = new FileInfo(Path.Combine(RepoLayout.Root.FullName, "Directory.Build.props"));
        Assert.True(shared.Exists, $"Expected a repository-wide props file at '{shared.FullName}'.");

        foreach (var project in AllProjects)
        {
            var files = RepoLayout.EffectiveBuildFiles(project);
            Assert.Contains(files, f => f.Path == "Directory.Build.props");
        }
    }

    public static TheoryData<string> EveryProject() => ToTheoryData(AllProjects);

    public static TheoryData<string> EveryProductProject() => ToTheoryData(ProductProjects);

    private static TheoryData<string> ToTheoryData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
        {
            data.Add(value);
        }

        return data;
    }

    /// <summary>
    /// Carries a nullable annotation so the compiler must emit nullable metadata for it.
    /// Under <c>Nullable=disable</c> the <c>string?</c> return would not compile cleanly and
    /// no nullable metadata would be emitted, so the test above would fail.
    /// </summary>
    private static string? NullableProbe() => null;
}
