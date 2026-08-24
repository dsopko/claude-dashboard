namespace ClaudeDashboard.Core;

/// <summary>
/// The convention every guarded value type in Core follows. Documented once, here, rather
/// than restated on each type.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionId"/>, <see cref="GroupKey"/>, <see cref="Ports.SoundId"/>,
/// <see cref="Ports.TabRef"/>, <see cref="Ports.WindowHandle"/> and
/// <see cref="Ports.DesktopId"/> are all <c>readonly record struct</c>s that validate in
/// their constructors. C# guarantees every struct a parameterless <c>default</c>, and
/// <c>default</c> does not run a constructor — so <c>default(SessionId)</c> exists and has
/// bypassed the non-empty check. This is a property of the language, not an oversight, and
/// it is worth stating what the codebase does about it.
/// </para>
/// <para>
/// <strong>These types stay structs.</strong> They are dictionary keys and hot-path values —
/// <see cref="SessionId"/> keys the Registry and is compared on every inbound event — and
/// converting them to classes to close the <c>default</c> hole would trade a real, repeated
/// allocation cost for a hole no correct caller falls into.
/// </para>
/// <para>
/// <strong>Instead, <c>default</c> is an explicitly supported "names nothing" value.</strong>
/// Every one of these types therefore:
/// </para>
/// <list type="number">
/// <item><description>
/// exposes an <c>IsEmpty</c> or <c>IsNone</c> member that is <see langword="true"/> for
/// <c>default</c>, so "names nothing" is always askable rather than inferable;
/// </description></item>
/// <item><description>
/// behaves rather than throws on <c>default</c> — a null backing string reads as
/// <see cref="string.Empty"/>, and equality and hashing work;
/// </description></item>
/// <item><description>
/// is rejected by whatever consumes it when a real value is required. <see cref="Session"/>
/// rejects a <c>default</c> <see cref="SessionId"/> or <see cref="GroupKey"/>,
/// <see cref="Group"/> rejects a <c>default</c> key, and <see cref="SessionRegistry"/>
/// ignores an event naming no session. Validation lives at the boundary that cares, because
/// that is the only place it can be enforced for a struct.
/// </description></item>
/// </list>
/// <para>
/// Point 1 is load-bearing, not cosmetic. <see cref="Ports.TabRef"/> is the cautionary case:
/// its <c>default</c> has a <see cref="Ports.WindowHandle.None"/> window and no tab index,
/// which is structurally identical to a legitimate window-level reference — the degraded-but
/// -usable result described in <see cref="Ports.ITerminalLocator"/>. Without
/// <see cref="Ports.TabRef.IsNone"/>, a caller following the documented "a non-null
/// <c>TabRef</c> means navigate at window granularity" rule would try to activate window
/// handle zero. Everywhere else the hole is theoretical; there it actively misleads.
/// </para>
/// </remarks>
internal static class ValueTypeConventions
{
    // Intentionally empty. This type exists to carry the documentation above, so the
    // convention has one home that <see cref="..."/> can point at from each value type.
}
