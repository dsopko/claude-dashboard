using ClaudeDashboard.App.Configuration;
using System.Windows;
using System.Windows.Threading;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// A <c>/show</c> that arrives before the window exists (Impl §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap is short and the failure inside it is total.</strong> <c>/show</c> answers
/// <c>200</c> whatever happens, so the second instance reads success, logs that it asked the
/// resident one to surface, and exits. Drop the request and the operator double-clicked a
/// shortcut, saw nothing at all, and every record of the launch says it worked — the same silent
/// failure this task exists to remove, arriving through the code that removes it.
/// </para>
/// <para>
/// Real windows on the harness's UI thread rather than a double: the thing being asserted is that
/// a window ends up visible, and only a window can show that.
/// </para>
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class WindowSurfacerTests(StaHarness harness)
{
    private readonly StaHarness _harness = harness;

    /// <summary>A request that arrives first is honoured when the window turns up.</summary>
    /// <remarks>
    /// Both halves asserted. The pending flag alone would be satisfied by a latch that never
    /// fired; the window alone would be satisfied by a surfacer that showed unconditionally,
    /// which would pop the dashboard up on every launch.
    /// </remarks>
    [Fact]
    public void A_request_that_arrives_before_the_window_is_honoured_when_it_appears()
    {
        var (pendingBefore, pendingAfter, visible) = _harness.Invoke(() =>
        {
            var surfacer = new WindowSurfacer(Logger.None);
            using var registry = new RegistryHarness();
            var window = NewWindow(registry);

            surfacer.Request();
            var before = surfacer.HasPendingRequest;

            surfacer.Attach(window);
            _harness.Pump(DispatcherPriority.Background);

            var result = (before, surfacer.HasPendingRequest, window.IsVisible);
            window.Hide();

            return result;
        });

        Assert.True(pendingBefore, "The request must be latched while there is no window.");
        Assert.False(pendingAfter, "Attaching must consume the latched request.");
        Assert.True(visible, "The window must be surfaced once it exists.");
    }

    /// <summary>Attaching without a request leaves the dashboard where the operator left it.</summary>
    /// <remarks>
    /// The control, and it matters: the dashboard starts to the tray (Impl §5.1). A surfacer that
    /// showed on every attach would put the window on screen at every logon, which is a different
    /// product.
    /// </remarks>
    [Fact]
    public void Attaching_without_a_request_shows_nothing()
    {
        var visible = _harness.Invoke(() =>
        {
            var surfacer = new WindowSurfacer(Logger.None);
            using var registry = new RegistryHarness();
            var window = NewWindow(registry);

            surfacer.Attach(window);
            _harness.Pump(DispatcherPriority.Background);

            var result = window.IsVisible;
            window.Hide();

            return result;
        });

        Assert.False(visible);
    }

    /// <summary>Once the window exists, a request surfaces it the ordinary way.</summary>
    [Fact]
    public void A_request_after_the_window_exists_surfaces_it()
    {
        var (visible, pending) = _harness.Invoke(() =>
        {
            var surfacer = new WindowSurfacer(Logger.None);
            using var registry = new RegistryHarness();
            var window = NewWindow(registry);

            surfacer.Attach(window);
            window.Hide();

            surfacer.Request();
            _harness.Pump(DispatcherPriority.Background);

            var result = (window.IsVisible, surfacer.HasPendingRequest);
            window.Hide();

            return result;
        });

        Assert.True(visible);
        Assert.False(pending, "A request with a window present must not latch.");
    }

    /// <summary>Several early requests collapse into one surfacing, not a queue of them.</summary>
    [Fact]
    public void Repeated_early_requests_surface_the_window_once()
    {
        var pending = _harness.Invoke(() =>
        {
            var surfacer = new WindowSurfacer(Logger.None);
            using var registry = new RegistryHarness();
            var window = NewWindow(registry);

            surfacer.Request();
            surfacer.Request();
            surfacer.Request();

            surfacer.Attach(window);
            _harness.Pump(DispatcherPriority.Background);

            var result = surfacer.HasPendingRequest;
            window.Hide();

            return result;
        });

        Assert.False(pending);
    }

    [Fact]
    public void It_needs_a_logger_and_a_window() =>
        _harness.Invoke(() =>
        {
            Assert.Throws<ArgumentNullException>(() => new WindowSurfacer(null!));
            Assert.Throws<ArgumentNullException>(() => new WindowSurfacer(Logger.None).Attach(null!));

            return true;
        });

    /// <summary>A window off the side of every monitor, so nothing flashes up during a test run.</summary>
    private static MainWindow NewWindow(RegistryHarness registry)
    {
        var window = new MainWindow(new MainViewModel(
            registry.Projection,
            new MotionPolicy(() => false, observeChanges: false),
            new StubAckPublisher(),
            new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence()))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };

        return window;
    }
}
