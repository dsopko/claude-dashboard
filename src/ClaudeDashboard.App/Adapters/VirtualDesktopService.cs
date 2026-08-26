using System.Runtime.InteropServices;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// Virtual-desktop placement for the dashboard's own window (Impl §5.4, §6.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pinning is an undocumented call, and this file is where that is contained.</strong>
/// Impl §6.3 filed it under the documented tier until 2026-08-26; it is not there.
/// <c>IVirtualDesktopManager</c> — the one interface Microsoft documents — has exactly three
/// methods, and none of them pins. Pinning lives on <c>IVirtualDesktopPinnedApps</c>, which is not
/// documented, is not in any header, and whose interface identifier Microsoft is free to change in
/// any Windows update.
/// </para>
/// <para>
/// <strong>Interface identifiers recorded against a build, because that is the only honest way to
/// carry them.</strong> The values below were used successfully on <strong>Windows 11 Pro,
/// 10.0.26200</strong> — see <see cref="RecordedAgainstBuild"/>. On a build where they are wrong,
/// the <c>QueryService</c> simply fails, this returns <see langword="false"/>, and the dashboard
/// is confined to one desktop. That is a lost convenience and not a broken product (TS §IV.7).
/// </para>
/// <para>
/// <strong>THE RESIDUAL: that covers one of the two ways an undocumented interface goes wrong, and
/// this is the single path in the product where "degrade, never crash" does not hold.</strong> The
/// handled case is a <em>changed identifier</em> — <c>QueryService</c> fails, the catch below runs,
/// the dashboard reports one desktop and lives. The unhandled case is an <em>unchanged identifier
/// over a changed vtable</em>: Microsoft keeps the GUID and reorders or inserts a method. Then
/// <c>QueryService</c> succeeds, every object is real, and the slot arithmetic is silently wrong —
/// a call to <c>PinView(IntPtr)</c> can land on a neighbouring slot such as <c>PinAppID(string)</c>
/// and dereference a window handle as a string pointer. That is an access violation. .NET treats it
/// as a corrupted-state exception and <strong>will not deliver it to the exception filter below at
/// all</strong>; the process dies, and the tray icon disappears with it.
/// </para>
/// <para>
/// This is stated rather than defended because <strong>it cannot be defended against</strong>. No
/// filter catches it, and no probe distinguishes a correct vtable from a plausible wrong one
/// without calling into it, which is the dangerous act itself. What can be done has been: the
/// surface is two methods on two interfaces, <c>IApplicationView</c> is never declared so its
/// layout is never assumed, and everything is in this one file. If the dashboard ever starts
/// vanishing without a log line after a Windows update, this paragraph is the reason and the fix
/// is to stop calling <see cref="PinToAllDesktops"/> on that build.
/// </para>
/// <para>
/// <strong>Nothing here is declared that is not called.</strong> Pinning needs a view object for
/// the window, and the temptation is to declare <c>IApplicationView</c> — a long interface whose
/// layout also shifts between builds. It is never called into, only handed straight back to
/// <c>PinView</c>, so it is carried as an opaque pointer and its shape is never asserted. That is
/// what keeps this file at sixty lines of interop instead of a vendored per-build wrapper.
/// </para>
/// <para>
/// <strong>How a real pin is told from a claimed one.</strong> Returning <see langword="true"/> is
/// exactly what a stub returns, and nothing inside this process can contradict "the window is on
/// every desktop". So the check is the <em>documented</em> interface:
/// <see cref="IsOnCurrentDesktop"/> wraps <c>IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop</c>,
/// and a pinned window answers <see langword="true"/> from a desktop it was never placed on while
/// an unpinned one answers <see langword="false"/> the moment the operator switches away. An
/// undocumented call, checked by a documented one.
/// </para>
/// <para>
/// That is the general shape, and Phase 4 inherits all of it, so state it in full:
/// <strong>find an oracle the implementation does not control, and a control that proves the
/// oracle was asked under the conditions you think it was.</strong>
/// </para>
/// <para>
/// <strong>The second half is not extra rigour; it is the half that works, and this file is the
/// proof.</strong> The first half was satisfied — a different interface, documented by Microsoft,
/// with no stake in the outcome, unreachable from a <c>return true</c> — and the first run of the
/// check still reported the pin verified when no desktop switch had happened. The oracle answers a
/// <em>different question</em>: <see cref="IsOnCurrentDesktop"/> reports presence, not pinning, and
/// presence is equally true of an unpinned window that never left. It is evidence about pinning
/// only when combined with a state change and a control proving the state change occurred; remove
/// any one of those three and it proves nothing. A later reader will be tempted to drop the
/// control, because it looks like ceremony. It is the load-bearing part. See
/// <c>tools/verify-pin.ps1</c>, which carries the whole argument.
/// </para>
/// </remarks>
public sealed class VirtualDesktopService : IVirtualDesktopService
{
    /// <summary>The Windows build these interface identifiers were observed working on.</summary>
    /// <remarks>
    /// Impl §6.3 requires this to be recorded rather than assumed portable. If pinning stops
    /// working after a Windows update, this is the first line to read and the identifiers below
    /// are the first thing to re-check.
    /// </remarks>
    public const string RecordedAgainstBuild = "Windows 11 Pro 10.0.26200";

    private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid VirtualDesktopPinnedAppsService = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    private static readonly Guid ApplicationViewCollectionService = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    private readonly ILogger _logger;
    private readonly Func<object?> _shell;

    /// <summary>Creates the adapter over the real shell.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public VirtualDesktopService(ILogger logger)
        : this(logger, CreateImmersiveShell)
    {
    }

    /// <summary>
    /// Creates the adapter over <paramref name="shell"/>, so the unavailable path can be exercised.
    /// </summary>
    /// <remarks>
    /// <strong>The seam exists because the failure is the interesting case and it cannot be
    /// reached otherwise.</strong> On a machine where pinning works, the degrade path never runs;
    /// on a machine where it does not, nothing else does. Commenting out the call by hand proves
    /// something about a build nobody ships. This lets a test hand over a shell that throws — the
    /// same thing Windows does when the identifiers have moved — and observe that the dashboard
    /// starts, says so once, and carries on.
    /// </remarks>
    internal VirtualDesktopService(ILogger logger, Func<object?> shell)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(shell);

        _logger = logger;
        _shell = shell;
    }

    private static object? CreateImmersiveShell() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(ImmersiveShell, throwOnError: true)!);

    /// <inheritdoc/>
    /// <remarks>
    /// Null until Phase 4, which is the documented "fall back to <c>cwd</c> grouping" signal
    /// (Impl §6.3). Phase 1 needs no desktop identity, and returning one this build cannot use
    /// would put a value into grouping that nothing reads.
    /// </remarks>
    public DesktopId? GetDesktop(WindowHandle window) => null;

    /// <inheritdoc/>
    public bool PinToAllDesktops(WindowHandle window)
    {
        object? shell = null;
        object? views = null;
        object? pinned = null;
        var view = IntPtr.Zero;

        try
        {
            shell = _shell();

            if (shell is not IServiceProvider provider)
            {
                return Unavailable("the shell did not offer a service provider");
            }

            // ref, not in. Measured: the identical call with `in Guid` fails with
            // TYPE_E_ELEMENTNOTFOUND, which reads exactly like "this build does not have the
            // service" — while a probe using `ref` finds both services present with these same
            // identifiers. The marshaller does not treat a readonly reference the same way here.
            var viewsService = ApplicationViewCollectionService;
            var viewsIid = typeof(IApplicationViewCollection).GUID;
            var pinnedService = VirtualDesktopPinnedAppsService;
            var pinnedIid = typeof(IVirtualDesktopPinnedApps).GUID;

            provider.QueryService(ref viewsService, ref viewsIid, out views);
            provider.QueryService(ref pinnedService, ref pinnedIid, out pinned);

            if (views is not IApplicationViewCollection collection || pinned is not IVirtualDesktopPinnedApps apps)
            {
                return Unavailable("the pinning services are not present on this build");
            }

            collection.GetViewForHwnd(window.Value, out view);

            if (view == IntPtr.Zero)
            {
                return Unavailable("the window has no application view");
            }

            apps.PinView(view);

            _logger.Debug("Pinned the window to every virtual desktop.");

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException
                                      or TypeLoadException or ArgumentException or MemberAccessException)
        {
            // The expected failure on a build where the identifiers have moved. One line, once —
            // the dashboard runs perfectly well on a single desktop.
            return Unavailable(ex.Message);
        }
        finally
        {
            Release(view);
            Release(pinned);
            Release(views);
            Release(shell);
        }
    }

    /// <summary>
    /// Whether <paramref name="window"/> is on the desktop the operator is looking at, via the
    /// <strong>documented</strong> <c>IVirtualDesktopManager</c>.
    /// </summary>
    /// <remarks>
    /// Not on <see cref="IVirtualDesktopService"/> because nothing in the product needs it: it
    /// exists so that <see cref="PinToAllDesktops"/> can be checked by something other than its
    /// own return value. Switch to another desktop and ask — a pinned window says true, an
    /// unpinned one says false.
    /// </remarks>
    /// <returns>True or false when Windows answered; null when it could not be asked at all.</returns>
    public bool? IsOnCurrentDesktop(WindowHandle window)
    {
        object? manager = null;

        try
        {
            manager = Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), throwOnError: true)!);

            return manager is IVirtualDesktopManager desktops
                && desktops.IsWindowOnCurrentVirtualDesktop(window.Value);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException
                                      or TypeLoadException or ArgumentException or MemberAccessException)
        {
            _logger.Debug(ex, "Could not ask which virtual desktop the window is on.");

            return null;
        }
        finally
        {
            Release(manager);
        }
    }

    /// <summary>Records why an attempt failed, at a level that does not shout on a retry.</summary>
    /// <remarks>
    /// <strong>Debug, not Information, and the reason is ownership.</strong> A single attempt cannot
    /// know whether it is the last one — the shell registers a window's application view a moment
    /// after the window appears, so the first attempt legitimately fails on a perfectly working
    /// build. The one line the operator should see belongs to whoever is counting attempts, which
    /// is <c>WindowPresence</c>. Logging Information here produced one line per retry for a
    /// condition that was not a fault.
    /// </remarks>
    private bool Unavailable(string reason)
    {
        _logger.Debug(
            "Virtual-desktop pinning attempt failed ({Reason}). Identifiers recorded against {Build}.",
            reason,
            RecordedAgainstBuild);

        return false;
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero)
        {
            Marshal.Release(unknown);
        }
    }

    // ---- Interop. Everything below is a declaration, not a decision. ---------------------------

    /// <summary>The shell's service locator. Documented, and stable across builds.</summary>
    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        // ref, not in. One out parameter, no return value.
        //
        // BOTH OF THOSE ARE LOAD-BEARING, AND GETTING EITHER WRONG FAILS WITH THE SAME LIE.
        // The identical call declared with `in Guid` comes back TYPE_E_ELEMENTNOTFOUND, and so
        // does one declared with both a return value and an out parameter. That HRESULT reads as
        // "this build does not have the service" — so the next reader goes to the interface
        // identifiers above, which are the one part that was correct. A wrong diagnosis, pointing
        // squarely at the right-looking suspect.
        //
        // Measured rather than reasoned: a standalone probe using `ref` found both services
        // present with these exact identifiers on this exact build, while the adapter using `in`
        // reported them missing. If pinning ever starts reporting unavailable, check this
        // declaration before you touch a GUID.
        void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    /// <summary>
    /// Undocumented. Only <c>GetViewForHwnd</c> is used, so the preceding slots are declared as
    /// placeholders to get the vtable offset right and are never called.
    /// </summary>
    [ComImport]
    [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        int GetViews(out IntPtr views);

        int GetViewsByZOrder(out IntPtr views);

        int GetViewsByAppUserModelId(string id, out IntPtr views);

        int GetViewForHwnd(IntPtr window, out IntPtr view);
    }

    /// <summary>
    /// Undocumented, and the reason this file exists. The view is passed straight through and
    /// never called into.
    /// </summary>
    [ComImport]
    [Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        int IsAppIdPinned(string appId, out bool pinned);

        int PinAppID(string appId);

        int UnpinAppID(string appId);

        int IsViewPinned(IntPtr view, out bool pinned);

        int PinView(IntPtr view);

        int UnpinView(IntPtr view);
    }

    /// <summary>
    /// The one interface Microsoft documents (<c>shobjidl_core.h</c>). Three methods, none of
    /// which pins — which is the correction Impl §6.3 now carries.
    /// </summary>
    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        bool IsWindowOnCurrentVirtualDesktop(IntPtr window);

        Guid GetWindowDesktopId(IntPtr window);

        void MoveWindowToDesktop(IntPtr window, ref Guid desktop);
    }
}
