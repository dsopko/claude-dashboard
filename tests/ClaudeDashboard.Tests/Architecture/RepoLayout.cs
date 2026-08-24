using System.IO;
using System.Xml.Linq;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Locates the repository on disk and reads project files as MSBuild XML, so the
/// architecture tests can assert on what the build is <em>declared</em> to do rather
/// than on what a particular compilation happened to emit.
/// </summary>
/// <remarks>
/// Declaration-level checking is deliberate. The C# compiler drops references an
/// assembly does not use, so metadata alone would let a forbidden reference sit in a
/// .csproj undetected until someone wrote code against it. Reading the project files
/// catches it the moment it is added.
/// </remarks>
internal static class RepoLayout
{
    internal const string Core = "ClaudeDashboard.Core";
    internal const string App = "ClaudeDashboard.App";
    internal const string Remote = "ClaudeDashboard.Remote";
    internal const string Tests = "ClaudeDashboard.Tests";

    private const char WindowsSeparator = '\\';
    private const char PosixSeparator = '/';

    /// <summary>The repository root, found by walking up from the test binaries to the solution file.</summary>
    internal static DirectoryInfo Root { get; } = FindRoot();

    /// <summary>Every .csproj in the repository, keyed by project name (file name without extension).</summary>
    internal static IReadOnlyDictionary<string, FileInfo> Projects { get; } = FindProjects(Root);

    internal static FileInfo Project(string name) =>
        Projects.TryGetValue(name, out var file)
            ? file
            : throw new InvalidOperationException(
                $"Project '{name}' was not found under '{Root.FullName}'. " +
                $"Found: {string.Join(", ", Projects.Keys.Order(StringComparer.Ordinal))}.");

    /// <summary>Parses a project file, stripping any MSBuild XML namespace so queries stay simple.</summary>
    internal static XDocument Load(FileInfo file)
    {
        var doc = XDocument.Load(file.FullName, LoadOptions.None);
        foreach (var element in doc.Descendants())
        {
            element.Name = XName.Get(element.Name.LocalName);
        }

        return doc;
    }

    /// <summary>
    /// The project file plus every Directory.Build.props / Directory.Build.targets that
    /// MSBuild imports into it, from the repository root down. A forbidden property or
    /// reference is just as forbidden when it is inherited from a shared props file.
    /// </summary>
    internal static IReadOnlyList<(string Path, XDocument Xml)> EffectiveBuildFiles(string projectName)
    {
        var project = Project(projectName);
        var files = new List<(string, XDocument)>();

        var chain = new List<DirectoryInfo>();
        for (var dir = project.Directory; dir is not null; dir = dir.Parent)
        {
            chain.Add(dir);
            if (string.Equals(dir.FullName, Root.FullName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        chain.Reverse();
        foreach (var dir in chain)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var candidate = new FileInfo(Path.Combine(dir.FullName, name));
                if (candidate.Exists)
                {
                    files.Add((Relative(candidate), Load(candidate)));
                }
            }
        }

        files.Add((Relative(project), Load(project)));
        return files;
    }

    internal static string Relative(FileInfo file) =>
        Path.GetRelativePath(Root.FullName, file.FullName).Replace(WindowsSeparator, PosixSeparator);

    /// <summary>Reads an MSBuild property as the last writer in the import chain sets it.</summary>
    internal static string? EffectiveProperty(string projectName, string propertyName)
    {
        string? value = null;
        foreach (var (_, xml) in EffectiveBuildFiles(projectName))
        {
            foreach (var element in xml.Descendants("PropertyGroup").Elements(propertyName))
            {
                value = element.Value.Trim();
            }
        }

        return value;
    }

    /// <summary>The project names a project references directly.</summary>
    internal static IReadOnlyList<string> ProjectReferences(string projectName) =>
        Load(Project(projectName))
            .Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileNameWithoutExtension(v!.Replace(WindowsSeparator, PosixSeparator)))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static DirectoryInfo FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
            {
                return dir;
            }
        }

        throw new InvalidOperationException(
            $"No solution file found walking up from '{AppContext.BaseDirectory}'.");
    }

    private static Dictionary<string, FileInfo> FindProjects(DirectoryInfo root)
    {
        var projects = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var file in root.EnumerateFiles("*.csproj", SearchOption.AllDirectories))
        {
            var path = file.FullName.Replace(WindowsSeparator, PosixSeparator);
            if (path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            projects[Path.GetFileNameWithoutExtension(file.Name)] = file;
        }

        return projects;
    }
}
