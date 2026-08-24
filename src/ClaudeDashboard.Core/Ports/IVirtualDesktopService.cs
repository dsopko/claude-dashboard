namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Reports and manipulates Windows virtual desktop placement (TS §III.9; Impl §1.3, §6.3).
/// <strong>Implemented in Phase 4</strong> over a vendored, version-pinned VirtualDesktop
/// wrapper — except <see cref="PinToAllDesktops"/>, which T1.16 uses in Phase 1 for the
/// dashboard's own window.
/// </summary>
/// <remarks>
/// The likeliest port here to break on a Windows update: the underlying COM interface GUIDs
/// shift between builds, and Impl §6.3 expects this adapter to need occasional maintenance.
/// That is precisely why it follows the seam's failure convention rather than throwing — if
/// desktop awareness stops working, window activation still jumps to the session and
/// grouping still falls back to its documented tier (TS §IV.7).
/// </remarks>
public interface IVirtualDesktopService
{
    /// <summary>
    /// The virtual desktop <paramref name="window"/> is on, or null if it could not be
    /// determined — which is the signal to fall back to <c>cwd</c> grouping.
    /// </summary>
    DesktopId? GetDesktop(WindowHandle window);

    /// <summary>
    /// Pins <paramref name="window"/> to every virtual desktop, so the dashboard stays
    /// visible wherever the operator is working (Impl §5.4).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the window was pinned; <see langword="false"/> if pinning is
    /// unavailable on this build. A <see langword="false"/> means the dashboard is simply
    /// confined to one desktop — a lost convenience, not a broken product.
    /// </returns>
    bool PinToAllDesktops(WindowHandle window);
}
