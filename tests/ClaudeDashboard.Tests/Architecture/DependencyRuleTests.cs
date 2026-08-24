using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Enforces the dependency rule (Impl 1.2; Execution Plan Part 1): everything points
/// at Core, nothing points at App, and Core stays free of WPF, Win32/COM, and ASP.NET.
/// These tests are the guard rail — they fail the moment a project file acquires a
/// forbidden reference, before any code can be written against it.
/// </summary>
public sealed class DependencyRuleTests
{
    /// <summary>
    /// Package / framework references Core may never take. Matched case-insensitively
    /// against the reference name.
    /// </summary>
    private static readonly (string Pattern, string Reason)[] ForbiddenInCore =
    [
        (@"^Microsoft\.AspNetCore", "ASP.NET Core belongs in App (ingress) and Remote"),
        (@"^Microsoft\.Extensions\.Hosting$", "the Generic Host belongs in App"),
        (@"^Microsoft\.WindowsDesktop\.App", "WPF/WinForms framework reference"),
        (@"^Microsoft\.Windows\.", "Windows-only SDK surface"),
        (@"^Microsoft\.Win32", "Win32 interop belongs in App behind a port"),
        (@"^System\.Windows", "WPF/Windows presentation types"),
        (@"^PresentationCore$|^PresentationFramework$|^WindowsBase$", "WPF assemblies"),
        (@"^System\.Drawing", "GDI+ / Win32 graphics"),
        (@"Wpf|WinForms|WindowsForms", "WPF/WinForms library"),
        (@"^FlaUI", "UI Automation belongs in App behind ITerminalLocator"),
        (@"^NAudio", "audio belongs in App behind ISoundPlayer"),
        (@"^H\.NotifyIcon", "tray icon belongs in App"),
        (@"^CommunityToolkit\.Mvvm$", "MVVM/UI libraries belong in App"),
        (@"Interop", "COM interop belongs in App"),
    ];

    [Fact]
    public void Core_references_no_other_project()
    {
        Assert.Empty(RepoLayout.ProjectReferences(RepoLayout.Core));
    }

    [Fact]
    public void App_references_only_Core()
    {
        Assert.Equal([RepoLayout.Core], RepoLayout.ProjectReferences(RepoLayout.App));
    }

    [Fact]
    public void Remote_references_only_Core()
    {
        Assert.Equal([RepoLayout.Core], RepoLayout.ProjectReferences(RepoLayout.Remote));
    }

    [Fact]
    public void Tests_reference_Core_and_App()
    {
        Assert.Equal([RepoLayout.App, RepoLayout.Core], RepoLayout.ProjectReferences(RepoLayout.Tests));
    }

    [Fact]
    public void Nothing_except_the_test_project_references_App()
    {
        var offenders = RepoLayout.Projects.Keys
            .Where(name => name != RepoLayout.Tests)
            .Where(name => RepoLayout.ProjectReferences(name).Contains(RepoLayout.App))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Nothing but {RepoLayout.Tests} may reference {RepoLayout.App} (Impl 1.2). " +
            $"Offending projects: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void Core_takes_no_WPF_Win32_or_AspNet_reference()
    {
        var violations = new List<string>();

        foreach (var (path, xml) in RepoLayout.EffectiveBuildFiles(RepoLayout.Core))
        {
            foreach (var name in ReferenceNames(xml))
            {
                foreach (var (pattern, reason) in ForbiddenInCore)
                {
                    if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
                    {
                        violations.Add($"{path}: '{name}' — {reason}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{RepoLayout.Core} must contain zero WPF, zero Win32/COM and zero ASP.NET references " +
            $"(Impl 1.2). Found:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", violations));
    }

    [Fact]
    public void Core_targets_a_platform_neutral_framework()
    {
        Assert.Equal("net10.0", RepoLayout.EffectiveProperty(RepoLayout.Core, "TargetFramework"));
    }

    [Fact]
    public void Core_enables_no_Windows_desktop_or_web_SDK_feature()
    {
        foreach (var property in new[] { "UseWPF", "UseWindowsForms" })
        {
            var value = RepoLayout.EffectiveProperty(RepoLayout.Core, property);
            Assert.True(
                value is null || !value.Equals("true", StringComparison.OrdinalIgnoreCase),
                $"{RepoLayout.Core} must not set {property}=true (Impl 1.2).");
        }

        var sdk = (string?)RepoLayout.Load(RepoLayout.Project(RepoLayout.Core)).Root?.Attribute("Sdk");
        Assert.Equal("Microsoft.NET.Sdk", sdk);
    }

    [Fact]
    public void Remote_targets_a_platform_neutral_framework()
    {
        // Impl 1.2: Remote is a second consumer of Core, not a desktop project.
        Assert.Equal("net10.0", RepoLayout.EffectiveProperty(RepoLayout.Remote, "TargetFramework"));
    }

    [Fact]
    public void App_is_the_Windows_WPF_host()
    {
        Assert.Equal("net10.0-windows", RepoLayout.EffectiveProperty(RepoLayout.App, "TargetFramework"));
        Assert.Equal("true", RepoLayout.EffectiveProperty(RepoLayout.App, "UseWPF"));
    }

    /// <summary>
    /// Impl 6.5 — the host runs at the user's normal integrity, never elevated:
    /// an elevated process cannot inspect the non-elevated terminals it exists to watch.
    /// </summary>
    [Fact]
    public void App_requests_no_elevation()
    {
        var manifestName = RepoLayout.EffectiveProperty(RepoLayout.App, "ApplicationManifest");
        Assert.False(string.IsNullOrWhiteSpace(manifestName));

        var manifest = new FileInfo(Path.Combine(
            RepoLayout.Project(RepoLayout.App).Directory!.FullName, manifestName!));
        Assert.True(manifest.Exists, $"Application manifest '{manifest.FullName}' is missing.");

        var level = XDocument.Load(manifest.FullName)
            .Descendants()
            .Single(e => e.Name.LocalName == "requestedExecutionLevel")
            .Attribute("level")?.Value;

        Assert.Equal("asInvoker", level);
    }

    /// <summary>
    /// Backstop for the declaration-level checks above: whatever the compiler actually
    /// emitted for Core must not name a forbidden assembly either.
    /// </summary>
    [Fact]
    public void Compiled_Core_assembly_names_no_forbidden_assembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, RepoLayout.Core + ".dll");
        Assert.True(File.Exists(path), $"Expected the Core assembly beside the tests at '{path}'.");

        var referenced = Assembly.LoadFrom(path)
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        var violations = referenced
            .Where(name => ForbiddenInCore.Any(f => Regex.IsMatch(name, f.Pattern, RegexOptions.IgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Compiled {RepoLayout.Core} references: {string.Join(", ", violations)}.");
    }

    /// <summary>PackageReference, FrameworkReference, Reference and COMReference names in one project file.</summary>
    private static IEnumerable<string> ReferenceNames(XDocument xml) =>
        xml.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "FrameworkReference"
                                          or "Reference" or "COMReference")
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Split(',')[0].Trim());
}
