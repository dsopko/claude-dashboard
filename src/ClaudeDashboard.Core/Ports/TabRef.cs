namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Where a session lives on screen: a terminal window, and the tab within it when tab-level
/// resolution succeeded (Impl §1.3, §6.1).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TabIndex"/> is nullable on purpose — it <em>is</em> TS §IV.7's degradation
/// ladder made expressible. When tab-level UI Automation works, the reference names a
/// window and a tab; when it fails, or when two tabs show identical recent text and the
/// match is ambiguous (TS §III.7), the adapter returns a window-only reference and callers
/// navigate and acknowledge at window granularity instead. A window-only <see cref="TabRef"/>
/// is a degraded success, not a failure — failure is a null <see cref="TabRef"/>.
/// </para>
/// <para>
/// Impl §6.1 describes a <c>TabRef</c> as "window handle + tab index/element". The UIA
/// <em>element</em> is deliberately absent: it is a Windows-only type that cannot cross into
/// Core (Impl §1.2). The adapter re-resolves the element from the window and index when it
/// needs one.
/// </para>
/// </remarks>
public readonly record struct TabRef
{
    /// <summary>References a specific tab within a window.</summary>
    /// <exception cref="ArgumentException"><paramref name="window"/> names no window.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tabIndex"/> is negative.</exception>
    public TabRef(WindowHandle window, int tabIndex)
        : this(window)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tabIndex);

        TabIndex = tabIndex;
    }

    /// <summary>References a window without resolving a tab within it — the degraded case.</summary>
    /// <exception cref="ArgumentException"><paramref name="window"/> names no window.</exception>
    public TabRef(WindowHandle window)
    {
        if (window.IsNone)
        {
            throw new ArgumentException("A tab reference must name a window.", nameof(window));
        }

        Window = window;
        TabIndex = null;
    }

    /// <summary>The terminal window.</summary>
    public WindowHandle Window { get; }

    /// <summary>The zero-based tab index, or null when only the window could be resolved.</summary>
    public int? TabIndex { get; }

    /// <summary>True when the reference resolved all the way to a tab.</summary>
    public bool IsTabResolved => TabIndex is not null;

    /// <summary>
    /// True for <c>default(TabRef)</c>, which references nothing.
    /// </summary>
    /// <remarks>
    /// Check this before acting on a <see cref="TabRef"/> that did not come straight from a
    /// locator call. A <c>default</c> instance is structurally identical to a legitimate
    /// window-level reference — <see cref="WindowHandle.None"/> and no tab index — so
    /// without this member the documented "a non-null <see cref="TabRef"/> means navigate at
    /// window granularity" rule would send a caller to window handle zero. See
    /// <see cref="ValueTypeConventions"/> for why these types stay structs.
    /// </remarks>
    public bool IsNone => Window.IsNone;

    public override string ToString() =>
        Window.IsNone
            ? "TabRef(none)"
            : TabIndex is { } index
                ? $"TabRef({Window}, tab {index})"
                : $"TabRef({Window}, window-level)";
}
