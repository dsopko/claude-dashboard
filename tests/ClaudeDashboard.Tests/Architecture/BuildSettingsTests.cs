using System.IO;
using System.Reflection;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Proves the engineering standards from Execution Plan Part 1 are in effect for
/// <em>every</em> project, not merely declared in one of them: nullable reference
/// types on everywhere, analyzers on everywhere, warnings-as-errors on the product
/// projects (required for Core "at minimum"), and doc comments checked everywhere (issue #19).
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

    /// <summary>
    /// The compiler's checks of a doc comment (issue #19): a link that names nothing (CS1574) or
    /// more than one member (CS0419), or is malformed (CS1580, CS1581, CS1584, CS1658); a
    /// parameter tag that names no parameter (CS1572, CS1734, CS1711, CS1735) or is repeated
    /// (CS1571); a documented member with an undocumented parameter (CS1573, CS1712); XML that
    /// does not parse (CS1570); and a comment on nothing (CS1587).
    /// </summary>
    private static readonly string[] DocCommentChecks =
    [
        "CS0419", "CS1570", "CS1571", "CS1572", "CS1573", "CS1574", "CS1580", "CS1581",
        "CS1584", "CS1587", "CS1658", "CS1711", "CS1712", "CS1734", "CS1735",
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

    /// <summary>
    /// The compiler checks a doc comment's links and parameter tags only while it writes the
    /// documentation file, so every project writes one (issue #19).
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProject))]
    public void Every_project_writes_its_documentation_file(string project)
    {
        Assert.Equal("true", RepoLayout.EffectiveProperty(project, "GenerateDocumentationFile"));
    }

    /// <summary>
    /// A wrong doc comment fails the build in every project, the test project included, which
    /// otherwise stays out of warnings-as-errors (issue #19).
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProject))]
    public void Every_project_treats_a_wrong_doc_comment_as_an_error(string project)
    {
        var errors = Codes(RepoLayout.EffectiveProperty(project, "WarningsAsErrors"));

        Assert.All(DocCommentChecks, code => Assert.Contains(code, errors));
    }

    /// <summary>
    /// No project turns a doc comment check off. The test project turns off only CS1591, a public
    /// member with no comment, because a test's name is its description; the product projects keep
    /// that one too.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProject))]
    public void No_project_silences_a_doc_comment_check(string project)
    {
        var silenced = Codes(RepoLayout.EffectiveProperty(project, "NoWarn"));

        Assert.All(DocCommentChecks, code => Assert.DoesNotContain(code, silenced));

        if (ProductProjects.Contains(project))
        {
            Assert.DoesNotContain("CS1591", silenced);
        }
    }

    /// <summary>
    /// The documentation files exist for the compiler's check; nothing at runtime reads them, so
    /// the release package leaves them out.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProductProject))]
    public void Product_projects_do_not_publish_their_documentation_files(string project)
    {
        Assert.Equal("false", RepoLayout.EffectiveProperty(project, "PublishDocumentationFile"));
        Assert.Equal("false", RepoLayout.EffectiveProperty(project, "PublishReferencesDocumentationFiles"));
    }

    /// <summary>
    /// The settings above are read from the build files; this reads the result. The documentation
    /// files are next to the assemblies this test runs against only if the compiler wrote them, and
    /// it checks the doc comments only when it does.
    /// </summary>
    [Theory]
    [InlineData(RepoLayout.Core)]
    [InlineData(RepoLayout.App)]
    [InlineData(RepoLayout.Tests)]
    public void The_compiler_wrote_the_documentation_file(string project)
    {
        var file = new FileInfo(Path.Combine(AppContext.BaseDirectory, project + ".xml"));

        Assert.True(file.Exists, $"Expected the compiler's documentation file at '{file.FullName}'.");
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

    /// <summary>The codes in an MSBuild list such as <c>$(NoWarn);CA1707;CS1591</c>; none when it is not set.</summary>
    private static HashSet<string> Codes(string? list) =>
        (list ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
