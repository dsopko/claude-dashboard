using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Puts the dashboard window where it belongs and keeps it present (Impl §5.4).
/// </summary>
/// <remarks>
/// <para>
/// Three separate things that all answer "is the window where the operator expects it": the saved
/// position, the always-on-top choice, and the pin to every virtual desktop. They are together
/// because they all need the window's handle and all run once, at the moment it first appears.
/// </para>
/// <para>
/// <strong>Pinning needs a real handle, which a window does not have until it is shown.</strong>
/// <c>SourceInitialized</c> is the first moment one exists, and pinning before that silently does
/// nothing — the call succeeds against a handle of zero and reports success. It is invisible
/// without a second desktop to look at.
/// </para>
/// <para>
/// <strong>The guard is the handle check in <see cref="Apply"/>, not the order it is called
/// in.</strong> An early version only subscribed to <c>SourceInitialized</c>, so calling it after
/// anything that had already shown the window meant the event had fired and would not fire again.
/// That was fixed here rather than in the caller: <c>Apply</c> now pins immediately when a handle
/// already exists and subscribes only when one does not, so <em>both orders are correct</em>. Any
/// call ordering elsewhere in start-up is free to change; nothing in the test suite pins it,
/// because nothing needs to. Do not preserve an ordering as though it were the guard — the guard
/// is eight lines below this remark.
/// </para>
/// <para>
/// <strong>Every failure here is a downgrade, never a throw.</strong> A window that cannot be
/// pinned is a window on one desktop; a window whose saved position is unusable opens on the
/// focused monitor. Neither is a reason for the dashboard not to start.
/// </para>
/// <para>
/// <strong>The success is logged as well as the failure, and that is a rule rather than a
/// courtesy.</strong> An early version registered its pin handler after something else had already
/// shown the window, so the handler never ran: nothing was pinned, and <em>nothing was logged
/// either way</em>. The absence of both lines is what gave it away — a path that is silent when it
/// works and silent when it fails tells a reader nothing at all, and cannot be distinguished from
/// a path that was never reached. This is the third time that shape has cost this project a
/// diagnosis.
/// </para>
/// </remarks>
public sealed class WindowPresence
{
    private readonly IVirtualDesktopService _desktops;
    private readonly ILogger _logger;

    /// <summary>Creates the presence policy.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public WindowPresence(IVirtualDesktopService desktops, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        ArgumentNullException.ThrowIfNull(logger);

        _desktops = desktops;
        _logger = logger;
    }

    /// <summary>Whether the window is pinned to every virtual desktop. Null until it is tried.</summary>
    public bool? Pinned { get; private set; }

    /// <summary>Whether the saved position was honoured. Null until the window is placed.</summary>
    public bool? RestoredPosition { get; private set; }

    /// <summary>
    /// Applies <paramref name="settings"/> to <paramref name="window"/> and pins it once it has a
    /// handle.
    /// </summary>
    /// <remarks>
    /// Placement is applied immediately because it must be in force before the window is shown;
    /// pinning waits for <c>SourceInitialized</c>, for the reason in this class's remarks.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public void Apply(Window window, WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        Place(window, settings);

        window.Topmost = settings.AlwaysOnTop;

        // THE GUARD. If the window already has a handle, SourceInitialized has fired and
        // subscribing to it would wait for an event that will never come again. That is not
        // hypothetical: a /show arriving during start-up is latched and shows the window, so on
        // that path the handle exists before this runs. Observed as a live run where nothing was
        // pinned and *nothing was logged either way* — the absence of both lines is what gave it
        // away, which is an argument for logging the success as well as the failure.
        //
        // With this check here, the caller may apply presence before or after anything else that
        // shows the window. Both paths pin.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Pin(window, PinAttempts);

            return;
        }

        window.SourceInitialized += (_, _) => Pin(window, PinAttempts);
    }

    /// <summary>Reads back where the window is now, for saving on the way out.</summary>
    /// <remarks>
    /// Uses <c>RestoreBounds</c> rather than the live properties: a window that is minimised or
    /// maximised reports the screen's shape, and saving that would restore the operator to a
    /// maximised window they never chose.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    public static WindowSettings Capture(Window window, bool alwaysOnTop)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bounds = window.RestoreBounds;

        if (double.IsNaN(bounds.Left) || bounds.Width <= 0)
        {
            bounds = new Rect(window.Left, window.Top, window.Width, window.Height);
        }

        return new WindowSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            AlwaysOnTop = alwaysOnTop,
        };
    }

    private void Place(Window window, WindowSettings settings)
    {
        var monitors = Monitors();
        var decision = WindowPlacement.Decide(settings, monitors, FocusedMonitor(monitors));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = decision.Bounds.Left;
        window.Top = decision.Bounds.Top;
        window.Width = decision.Bounds.Width;
        window.Height = decision.Bounds.Height;

        RestoredPosition = decision.Restored;

        if (!decision.Restored && settings.HasPosition)
        {
            _logger.Information(
                "The saved window position ({Left},{Top}) is on no monitor that exists now; opening on the current one instead.",
                settings.Left,
                settings.Top);
        }
    }

    /// <summary>How many times to try before deciding pinning is genuinely unavailable.</summary>
    /// <remarks>
    /// <strong>The first attempt fails on a working machine, and that is not a fault.</strong>
    /// Pinning needs the shell's application view for the window, and the shell creates that a
    /// moment after the window first appears — measured: <c>GetViewForHwnd</c> returns
    /// <c>TYPE_E_ELEMENTNOTFOUND</c> immediately after the window is shown and returns a real view
    /// for the same handle once it has settled. Without retrying, pinning never works and reports
    /// "unavailable on this build", which sends the next reader to the interface identifiers —
    /// the one part that was correct.
    /// </remarks>
    public const int PinAttempts = 8;

    private void Pin(Window window, int attemptsLeft)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle != IntPtr.Zero && _desktops.PinToAllDesktops(new WindowHandle(handle)))
        {
            Pinned = true;
            _logger.Information("Pinned the dashboard window to every virtual desktop.");

            return;
        }

        if (attemptsLeft > 1)
        {
            // Queued rather than slept: this is the UI thread, and the thing being waited for is
            // the shell finishing with a window this thread is responsible for showing.
            // ApplicationIdle runs after layout and render, which is where the view appears.
            window.Dispatcher.InvokeAsync(
                () => Pin(window, attemptsLeft - 1),
                DispatcherPriority.ApplicationIdle);

            return;
        }

        Pinned = false;

        // One line, at the end, from the only place that knows this was the last attempt.
        _logger.Information(
            "Could not pin the dashboard window to every virtual desktop after {Attempts} attempts; " +
            "it will live on one desktop. This is a lost convenience, not a fault — see the log at " +
            "Debug for the reason, and {Adapter} for the build its identifiers were recorded against.",
            PinAttempts,
            nameof(ClaudeDashboard.App.Adapters.VirtualDesktopService));
    }

    /// <summary>Every monitor's working area, in virtual-screen coordinates.</summary>
    /// <remarks>
    /// <c>System.Windows.Forms.Screen</c> is deliberately not used — it would pull a second UI
    /// framework into the app for one list. WPF exposes the virtual screen but not the individual
    /// monitors, so this reads them from the system metrics WPF itself is built on.
    /// </remarks>
    private static IReadOnlyList<ScreenRect> Monitors() =>
        [.. MonitorLayout.WorkingAreas()];

    private static ScreenRect FocusedMonitor(IReadOnlyList<ScreenRect> monitors) =>
        MonitorLayout.ForCursor(monitors);
}
