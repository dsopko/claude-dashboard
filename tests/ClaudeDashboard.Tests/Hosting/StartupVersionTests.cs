using System.Reflection;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The one version line (PKG.2).
/// </summary>
/// <remarks>
/// The line is what PKG.4's gate and every later support question reads out of the log, so what
/// is asserted is the wiring that produces it: the value comes from the App assembly's
/// informational-version attribute, and the emitted line carries exactly that value and nothing
/// else. The <em>content</em> of the version — <c>0.1.0+sha</c> — is the build's to stamp and the
/// published exe's to prove; a dev-built test assembly carries whatever the SDK stamps a dev
/// build with, and asserting a literal here would pin the wrong thing.
/// </remarks>
public sealed class StartupVersionTests
{
    /// <summary>The value is the App assembly's informational version, read live.</summary>
    /// <remarks>
    /// The attribute is asserted present first, so the fallback cannot be what quietly passed
    /// this test: an SDK build always writes the attribute, and if that ever stops being true
    /// this fails loudly rather than the value silently degrading to the four-part assembly
    /// version.
    /// </remarks>
    [Fact]
    public void The_value_is_the_informational_version_of_the_App_assembly()
    {
        var attribute = typeof(StartupVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(attribute.InformationalVersion, StartupVersion.Value);
    }

    /// <summary>An assembly without the attribute falls back to its assembly version.</summary>
    /// <remarks>
    /// Driven with an assembly that genuinely lacks the attribute rather than a mock: the
    /// runtime's own System.Private.CoreLib carries one, but a dynamic assembly carries none, so
    /// the fallback arm is reached for real.
    /// </remarks>
    [Fact]
    public void An_assembly_without_the_attribute_falls_back_to_its_version()
    {
        var bare = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("bare-for-version-test") { Version = new Version(9, 8, 7, 6) },
            System.Reflection.Emit.AssemblyBuilderAccess.Run);

        Assert.Equal("9.8.7.6", StartupVersion.Of(bare));
    }

    /// <summary>
    /// The line is emitted at Information and carries the version and nothing else.
    /// </summary>
    /// <remarks>
    /// Equality on the whole rendered message, not Contains: "nothing else on that line" is the
    /// requirement — no paths, no settings — and only an exact match can assert an absence.
    /// </remarks>
    [Fact]
    public void The_line_carries_the_version_and_nothing_else()
    {
        var sink = new RecordingLogSink();

        using (var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger())
        {
            StartupVersion.Log(logger);
        }

        var entry = Assert.Single(sink.Events);

        Assert.Equal(LogEventLevel.Information, entry.Level);

        // The template renders the version literally (:l), so the file line reads
        // "Claude Dashboard 0.1.0+sha." rather than putting quotes around what PKG.4 greps for.
        Assert.Equal($"Claude Dashboard {StartupVersion.Value}.", RecordingLogSink.Render(entry));
    }
}
