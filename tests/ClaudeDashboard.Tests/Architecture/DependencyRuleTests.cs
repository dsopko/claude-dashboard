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

        var level = Manifest()
            .Descendants()
            .Single(e => e.Name.LocalName == "requestedExecutionLevel")
            .Attribute("level")?.Value;

        Assert.Equal("asInvoker", level);
    }

    /// <summary>
    /// Per-Monitor v2 is declared in the manifest (Impl §5.4).
    /// </summary>
    /// <remarks>
    /// The sibling of the elevation test, and it exists because the two declarations sat side by
    /// side in one file with only one of them guarded: deleting <c>asInvoker</c> turned a test
    /// red, and deleting <c>dpiAwareness</c> — or the whole <c>application</c> element that also
    /// carries <c>longPathAware</c> — left everything green. The only thing standing behind DPI
    /// awareness was a future acceptance criterion asking somebody to look at a window on two
    /// monitors once, and a sighting is not a guard.
    /// </remarks>
    [Fact]
    public void App_declares_per_monitor_dpi_awareness()
    {
        var awareness = Manifest()
            .Descendants()
            .SingleOrDefault(e => e.Name.LocalName == "dpiAwareness");

        Assert.True(awareness is not null, "app.manifest declares no <dpiAwareness> element (Impl §5.4).");
        Assert.Equal("PerMonitorV2", awareness!.Value.Trim());
    }

    /// <summary>
    /// Exactly one thing in the product may reach <c>IUiTick</c> (T1.13b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what makes a deferral safe rather than merely reasoned.</strong> T1.13b
    /// declined to add a monotonic guard to <c>MainViewModel.Tick</c>, which assigns whatever
    /// instant it is handed without comparing it to the last. That is correct <em>because</em>
    /// <c>EventConsumer</c> is the only caller, on one thread, so ticks are posted in the order
    /// their instants were read and a stale one can never follow a fresh one. The whole decision
    /// rests on that count, and nothing tested it: add a second caller and every existing test
    /// stays green while ages on screen start jumping backwards for up to fifteen seconds.
    /// </para>
    /// <para>
    /// <strong>If this test fails because a second caller is genuinely wanted, add the guard —
    /// do not delete the test.</strong> The guard is two lines in <c>MainViewModel.Tick</c> and
    /// <c>TrayViewModel.Tick</c>: ignore an instant earlier than the one already held. Deleting
    /// this instead would restore exactly the state T1.13b was cleaning up — a property nothing
    /// checks, with a comment somewhere claiming it holds.
    /// </para>
    /// <para>
    /// <strong>Why it matches both names, which the first version of this test did not.</strong>
    /// It originally matched only <c>IUiTick</c>, and <strong>every call site in the product uses
    /// the concrete <c>UiTick</c></strong>. So the test watched three files while the real callers
    /// sat in <c>Program</c>, <c>TrayIcon</c> and <c>MainViewModel</c>, and a genuine second caller
    /// — a private field of the concrete type, constructed, with <c>Tick</c> called on it — was
    /// planted in another file and the test passed. The mutation that had "verified" it used the
    /// <em>interface</em>, so it was shaped to the pattern rather than to a real caller: a plant
    /// that confirms the checker instead of testing it.
    /// </para>
    /// <para>
    /// The wider set is the point rather than a cost. <c>MainViewModel</c> and <c>TrayIcon</c> are
    /// what <c>IUiTickTarget</c> exists for and <c>Program</c> is the wiring, so those are exactly
    /// where a second caller would appear.
    /// </para>
    /// <para>
    /// <strong>What it observes, and what it does not.</strong> It reads source, so it catches a
    /// new field, a new constructor parameter, or a resolution from the container, under either
    /// name, in any file outside the six. It does not parse call sites, so a second call from
    /// inside one of those six is invisible to it. That is a narrower net than "one caller", and
    /// it is the one that costs nothing.
    /// </para>
    /// <para>
    /// <strong>On source versus assembly, corrected — the first version of this remark
    /// generalised too far.</strong> It is true that a stale <em>product</em> assembly cannot fool
    /// a source-reading test, which is how this one still reported correctly when a plant failed
    /// to compile. But a stale <em>test</em> assembly still runs old assertion logic, so the
    /// immunity is one-sided. And the property that grants it — never observing what executes —
    /// is the same one that makes it weaker. This test is its own counterexample: immune to the
    /// stale-build trap, and it missed a real second caller for two days. <strong>Immunity to one
    /// failure mode says nothing about correctness.</strong> Source suits structural properties,
    /// where source is the authority anyway; behaviour wants the assembly, and the answer to a
    /// build that fails quietly is to check the build rather than change instrument.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_the_event_consumer_reaches_the_ui_tick()
    {
        var appDirectory = RepoLayout.Project(RepoLayout.App).Directory!;

        var mentions = appDirectory
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))

            // Both the interface and the concrete class, because every call site in the product
            // uses the concrete one. Still word-bounded, so IUiTickTarget — a different type,
            // implemented by the view models — does not count as reaching the tick.
            .Where(file => Regex.IsMatch(File.ReadAllText(file.FullName), @"\bI?UiTick\b"))
            .Select(file => file.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "AppHost.cs",
                "EventConsumer.cs",
                "MainViewModel.cs",
                "Program.cs",
                "TrayIcon.cs",
                "UiTick.cs",
            ],
            mentions);
    }

    /// <summary>Loads the application manifest the .csproj actually names.</summary>
    /// <remarks>
    /// Shared by both manifest tests rather than copied, so that a manifest which moved or was
    /// unhooked from the build fails them together instead of one silently passing against a
    /// stale path.
    /// </remarks>
    private static XDocument Manifest()
    {
        var manifestName = RepoLayout.EffectiveProperty(RepoLayout.App, "ApplicationManifest");
        Assert.False(string.IsNullOrWhiteSpace(manifestName));

        var manifest = new FileInfo(Path.Combine(
            RepoLayout.Project(RepoLayout.App).Directory!.FullName, manifestName!));
        Assert.True(manifest.Exists, $"Application manifest '{manifest.FullName}' is missing.");

        return XDocument.Load(manifest.FullName);
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
