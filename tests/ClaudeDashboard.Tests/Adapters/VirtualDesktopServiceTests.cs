using System.Runtime.InteropServices;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Adapters;

/// <summary>
/// The undocumented pin, and what happens when it is not there (Impl §6.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is testable here is the failure, and only the failure.</strong> Nothing in this
/// process can contradict "the window is on every desktop" — that claim is settled by switching to
/// a second desktop and asking Windows, which is recorded in the task's status report and in the
/// acceptance supplement. What these tests hold is the degrade path: on a build where the
/// interface identifiers have moved, the dashboard starts, says so once, and runs on one desktop.
/// </para>
/// <para>
/// That path is unreachable on a machine where pinning works, which is why the adapter takes a
/// shell factory. Commenting out the call by hand would prove something about a build nobody
/// ships.
/// </para>
/// </remarks>
public sealed class VirtualDesktopServiceTests
{
    private static readonly WindowHandle SomeWindow = new(0x1234);

    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    /// <summary>A shell that is not there at all — the build where the class is gone.</summary>
    [Fact]
    public void A_shell_that_cannot_be_created_degrades_to_false()
    {
        var log = new RecordingLogSink();
        var logger = Logger(log);

        var pinned = new VirtualDesktopService(
            logger,
            () => throw new TypeLoadException("Class not registered"))
            .PinToAllDesktops(SomeWindow);

        Assert.False(pinned);
    }

    /// <summary>A shell that exists but offers nothing — the build where the identifiers moved.</summary>
    /// <remarks>
    /// The likelier of the two. The immersive shell is stable; what shifts is the service and
    /// interface identifiers queried from it.
    /// </remarks>
    [Fact]
    public void A_shell_without_the_pinning_services_degrades_to_false()
    {
        var log = new RecordingLogSink();

        var pinned = new VirtualDesktopService(Logger(log), () => new object())
            .PinToAllDesktops(SomeWindow);

        Assert.False(pinned);
    }

    /// <summary>
    /// A failed attempt records why, and the build its identifiers were recorded against — at
    /// Debug, not at a level that shouts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The level is the finding, not a style choice.</strong> This logged at Information
    /// until a live run showed why it must not: the first attempt fails on a perfectly working
    /// machine, because the shell registers a window's application view a moment after the window
    /// appears. <c>WindowPresence</c> therefore retries, and an Information line here produced one
    /// per retry for something that was not a fault.
    /// </para>
    /// <para>
    /// A single attempt cannot know whether it is the last, so the operator-facing line belongs to
    /// whatever is counting attempts. What belongs here is the reason, for whoever turns Debug on.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_failed_attempt_records_the_reason_and_the_build_at_debug()
    {
        var log = new RecordingLogSink();

        new VirtualDesktopService(Logger(log), () => new object()).PinToAllDesktops(SomeWindow);

        var line = Assert.Single(log.Events, entry => entry.Level == LogEventLevel.Debug);
        var rendered = line.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains(VirtualDesktopService.RecordedAgainstBuild, rendered, StringComparison.Ordinal);

        // …and nothing louder, so a retry cannot fill the operator's log with a non-fault.
        Assert.DoesNotContain(log.Events, entry => entry.Level >= LogEventLevel.Information);
    }

    /// <summary>Nothing here throws, whatever the shell does.</summary>
    /// <remarks>
    /// This runs during window creation. A throw would take the window with it, turning a lost
    /// convenience into a dashboard that does not appear — the precise inversion TS §IV.7 forbids.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(TypeLoadException))]
    public void No_failure_of_the_shell_escapes_as_an_exception(Type failure)
    {
        var log = new RecordingLogSink();
        var service = new VirtualDesktopService(
            Logger(log),
            () => throw (Exception)Activator.CreateInstance(failure)!);

        Assert.False(service.PinToAllDesktops(SomeWindow));
    }

    /// <summary>
    /// Desktop identity stays null until Phase 4, which is the documented fall-back signal.
    /// </summary>
    /// <remarks>
    /// Impl §6.3: a null is how grouping is told to use <c>cwd</c>. Returning a real identity now
    /// would put a value into grouping that nothing reads and that Phase 1 cannot honour.
    /// </remarks>
    [Fact]
    public void The_desktop_identity_is_null_until_phase_four() =>
        Assert.Null(new VirtualDesktopService(Logger(new RecordingLogSink())).GetDesktop(SomeWindow));

    [Fact]
    public void It_needs_a_logger() =>
        Assert.Throws<ArgumentNullException>(() => new VirtualDesktopService(null!));
}
