namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Identifies a Windows virtual desktop — the Phase 4 grouping key (TS §III.9, §IV.3;
/// Impl §6.3).
/// </summary>
/// <remarks>
/// Wraps a <see cref="Guid"/> because that is what the platform issues, and because
/// <see cref="Guid"/> is a portable BCL type: Core learns nothing about Windows from it.
/// Turning one of these into a <see cref="GroupKey"/> is the group resolver's job, not this
/// type's.
/// </remarks>
public readonly record struct DesktopId
{
    /// <summary>The id naming no desktop.</summary>
    public static DesktopId None => default;

    /// <summary>Wraps a host-supplied desktop id.</summary>
    public DesktopId(Guid value) => Value = value;

    /// <summary>The raw desktop id.</summary>
    public Guid Value { get; }

    /// <summary>True when this id names no desktop.</summary>
    /// <remarks>See <see cref="ValueTypeConventions"/> for why these types stay structs.</remarks>
    public bool IsNone => Value == Guid.Empty;

    public override string ToString() => IsNone ? "DesktopId(none)" : $"DesktopId({Value:D})";
}
