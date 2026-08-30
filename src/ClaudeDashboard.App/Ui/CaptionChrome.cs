using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The two things a drawn caption still needs from Win32 (design option 2c).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything else is XAML.</strong> <c>WindowChrome</c> already gives the caption its
/// drag region, its double-click, its system menu on right-click and Alt+Space, and its resize
/// borders. Two things it does not give, and neither can be expressed in markup:
/// </para>
/// <para>
/// <strong>The maximized overflow.</strong> A maximized window with a thick frame has a window
/// rect inflated past the work area by the frame on every side. Normally that inflation is
/// non-client and invisible; with the non-client area gone it becomes client area hanging off
/// the screen, and the caption buttons go with it. <see cref="MaximizedInset"/> is the amount to
/// give back, read from the metrics <em>at the window's own DPI</em> — so it is right at 150%
/// and not merely right at 100%.
/// </para>
/// <para>
/// <strong>Snap Layouts.</strong> The flyout Windows 11 shows over the maximize button appears
/// only for a window that answers <c>WM_NCHITTEST</c> with <c>HTMAXBUTTON</c> there. That answer
/// takes the button out of WPF's input path — so the click has to be handled here too, and so
/// does the hover, which is why <c>hovered</c> is a callback rather than the button's own
/// <c>IsMouseOver</c>.
/// </para>
/// <para>
/// <strong>Degrade, never crash</strong> (Execution Plan Part 1). Every failure here costs one
/// affordance and nothing else: no metrics means no inset, no presentation source means no Snap
/// Layouts, and the window is still a window either way.
/// </para>
/// </remarks>
internal sealed class CaptionChrome
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;

    /// <summary>The hit-test answer that earns the Snap Layouts flyout.</summary>
    private const int HtMaxButton = 9;

    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    private readonly Window _window;
    private readonly Func<FrameworkElement?> _maximizeButton;
    private readonly Action<bool> _hovered;

    private CaptionChrome(Window window, Func<FrameworkElement?> maximizeButton, Action<bool> hovered)
    {
        _window = window;
        _maximizeButton = maximizeButton;
        _hovered = hovered;
    }

    /// <summary>
    /// Starts answering for <paramref name="window"/>'s maximize button. Call once the window
    /// has a source — <c>OnSourceInitialized</c> — and not before.
    /// </summary>
    /// <param name="window">The window whose caption is drawn.</param>
    /// <param name="maximizeButton">
    /// The maximize or restore button, whichever is showing. A function rather than an element,
    /// because the two swap on <see cref="Window.WindowState"/>.
    /// </param>
    /// <param name="hovered">
    /// Told whether the pointer is over that button. The button cannot know: the hit-test answer
    /// above sends the pointer to Windows instead of to WPF.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void Attach(Window window, Func<FrameworkElement?> maximizeButton, Action<bool> hovered)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(maximizeButton);
        ArgumentNullException.ThrowIfNull(hovered);

        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }

        source.AddHook(new CaptionChrome(window, maximizeButton, hovered).OnMessage);
    }

    /// <summary>
    /// How far to inset the window's content while maximized, in device-independent pixels.
    /// </summary>
    /// <param name="window">The window, at whose own DPI the metrics are read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    public static Thickness MaximizedInset(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var dpi = VisualTreeHelper.GetDpi(window);
        var raw = (uint)Math.Round(dpi.DpiScaleX * 96.0);

        // The padded border counts on every edge; it is what makes the frame thicker than
        // SM_CXSIZEFRAME alone says, and leaving it out undershoots by four pixels at 100%.
        var horizontal = Metric(SmCxSizeFrame, raw) + Metric(SmCxPaddedBorder, raw);
        var vertical = Metric(SmCySizeFrame, raw) + Metric(SmCxPaddedBorder, raw);

        var x = horizontal / dpi.DpiScaleX;
        var y = vertical / dpi.DpiScaleY;

        return new Thickness(x, y, x, y);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest when OverMaximizeButton(lParam):
                _hovered(true);
                handled = true;

                return new IntPtr(HtMaxButton);

            case WmNcHitTest:
            case WmNcMouseLeave:
                // Left unhandled: WindowChrome still owns every other part of the caption.
                _hovered(false);

                return IntPtr.Zero;

            // Swallowed so the press neither starts a drag nor opens the system menu. The window
            // is toggled on the release, which is where a caption button acts.
            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                handled = true;

                return IntPtr.Zero;

            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                handled = true;
                Toggle();

                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>Whether the screen point in <paramref name="lParam"/> is over the button.</summary>
    /// <remarks>
    /// Both coordinates are signed 16-bit and the pointer really can be left of or above the
    /// window, so they are widened through <see cref="short"/> rather than masked as unsigned.
    /// <c>PointFromScreen</c> carries the DPI, which is what keeps this right at 150%.
    /// </remarks>
    private bool OverMaximizeButton(IntPtr lParam)
    {
        if (_maximizeButton() is not { IsVisible: true } button)
        {
            return false;
        }

        var bits = lParam.ToInt64();
        var point = new Point((short)(bits & 0xFFFF), (short)((bits >> 16) & 0xFFFF));

        try
        {
            var local = button.PointFromScreen(point);

            return local.X >= 0
                && local.X < button.ActualWidth
                && local.Y >= 0
                && local.Y < button.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // The button is not connected to a source yet. No flyout, no failure.
            return false;
        }
    }

    private void Toggle()
    {
        if (_window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(_window);

            return;
        }

        SystemCommands.MaximizeWindow(_window);
    }

    /// <summary>
    /// One system metric at <paramref name="dpi"/>, falling back to the primary monitor's when
    /// the per-DPI entry point is missing.
    /// </summary>
    private static int Metric(int index, uint dpi)
    {
        try
        {
            return GetSystemMetricsForDpi(index, dpi);
        }
        catch (EntryPointNotFoundException)
        {
            return GetSystemMetrics(index);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
