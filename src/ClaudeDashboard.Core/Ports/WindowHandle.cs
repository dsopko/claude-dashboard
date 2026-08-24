namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// An opaque reference to a top-level window, as the host understands it.
/// </summary>
/// <remarks>
/// This is the one place a Win32 concept reaches Core (Impl §1.3, §6.3). It is deliberately
/// a wrapper over <see cref="nint"/> — a C# language primitive — and <strong>not</strong>
/// <c>System.Windows.Interop</c>'s or Win32's HWND type, because Core must reference no
/// Win32, COM, or WPF surface (Impl §1.2). Core never dereferences it, compares it to
/// anything but another handle, or passes it anywhere except back to the adapter that
/// issued it.
/// </remarks>
public readonly record struct WindowHandle
{
    /// <summary>The handle naming no window.</summary>
    public static WindowHandle None => default;

    /// <summary>Wraps a host-supplied window handle.</summary>
    public WindowHandle(nint value) => Value = value;

    /// <summary>The raw handle value. Meaningful only to the host adapter that issued it.</summary>
    public nint Value { get; }

    /// <summary>True when this handle names no window.</summary>
    public bool IsNone => Value == 0;

    public override string ToString() =>
        IsNone ? "WindowHandle(none)" : $"WindowHandle(0x{Value:X})";
}
