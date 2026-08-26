using System.Runtime.InteropServices;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Which monitors exist, and which one the operator is on (Impl §5.4).
/// </summary>
/// <remarks>
/// <para>
/// WPF exposes the virtual screen as a single rectangle and does not enumerate monitors, so this
/// asks Windows directly. <c>System.Windows.Forms.Screen</c> would do it in one line and is not
/// used: pulling a second UI framework into the process for one list is a poor trade, and it would
/// have to be initialised on the UI thread.
/// </para>
/// <para>
/// <strong>Working areas, not full bounds.</strong> A window centred on a monitor's full bounds
/// sits partly behind the taskbar. The working area is what the operator can actually see.
/// </para>
/// <para>
/// Degrades rather than throwing. If enumeration fails there is still a virtual screen, and a
/// window placed on it is reachable — which is the only property that matters here.
/// </para>
/// </remarks>
internal static class MonitorLayout
{
    private const int MonitorInfoFlagPrimary = 1;

    /// <summary>Every monitor's working area, in virtual-screen coordinates.</summary>
    public static IReadOnlyList<ScreenRect> WorkingAreas()
    {
        var found = new List<ScreenRect>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        return found;

        bool Collect(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                found.Add(ToScreenRect(info.Work));
            }

            return true;
        }
    }

    /// <summary>
    /// The monitor the mouse is on, which is the best available answer to "where is the operator
    /// looking" without the focus tracking Phase 3 adds.
    /// </summary>
    /// <remarks>
    /// Not the primary monitor. On a three-monitor desk the primary is frequently not the one
    /// being used, and opening there puts the window on a screen the operator is not facing.
    /// </remarks>
    public static ScreenRect ForCursor(IReadOnlyList<ScreenRect> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0)
        {
            return new ScreenRect(0, 0, WindowPlacement.FallbackWidth, WindowPlacement.FallbackHeight);
        }

        try
        {
            if (GetCursorPos(out var cursor))
            {
                var under = monitors.FirstOrDefault(monitor =>
                    cursor.X >= monitor.Left && cursor.X < monitor.Right
                    && cursor.Y >= monitor.Top && cursor.Y < monitor.Bottom);

                if (under != default)
                {
                    return under;
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Fall through to the first monitor.
        }

        return monitors[0];
    }

    private static ScreenRect ToScreenRect(Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr context, ref Rect bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr context, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}
