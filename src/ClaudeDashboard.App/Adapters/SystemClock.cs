using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.App.Adapters;

/// <summary>
/// <see cref="IClock"/> over the system clock — the host's implementation of T1.6's seam.
/// </summary>
/// <remarks>
/// Ingress stamps every inbound event from this, which is the only point at which a hook
/// payload can acquire a time: Claude Code sends none (T1.1). Wall-clock rather than monotonic,
/// deliberately — see <see cref="IClock"/>.
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <inheritdoc/>
    public DateTimeOffset Now => DateTimeOffset.Now;
}
