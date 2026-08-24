using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// An <see cref="IClock"/> the test drives.
/// </summary>
/// <remarks>
/// <para>
/// The reason <see cref="IClock"/> exists. T1.2's stale-drop guard and T1.5's widening
/// 2 → 5 → 10 minute nudge schedule are both timing behavior that would otherwise be
/// untestable without really waiting; here a test states the time it wants and asserts.
/// </para>
/// <para>
/// <see cref="Advance"/> refuses to move backwards. Time going backwards is not a scenario
/// under test — it is a mistake in a test — and a nudge schedule silently un-firing because
/// a test subtracted where it meant to add is exactly the kind of bug a fake should refuse
/// to help create. A test that genuinely wants an earlier instant sets <see cref="Now"/>.
/// </para>
/// </remarks>
public sealed class FakeClock : IClock
{
    /// <summary>An arbitrary fixed instant, so tests that do not care about the wall date need not invent one.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Starts at <see cref="DefaultStart"/>.</summary>
    public FakeClock()
        : this(DefaultStart)
    {
    }

    /// <summary>Starts at <paramref name="start"/>.</summary>
    public FakeClock(DateTimeOffset start) => Now = start;

    /// <summary>The current instant. Settable, including backwards, for tests that need it.</summary>
    public DateTimeOffset Now { get; set; }

    /// <summary>Moves the clock forward and returns the new instant.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="by"/> is negative.</exception>
    public DateTimeOffset Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        Now += by;
        return Now;
    }

    /// <summary>Moves the clock forward by whole minutes — the unit the nudge schedule is written in.</summary>
    public DateTimeOffset AdvanceMinutes(double minutes) => Advance(TimeSpan.FromMinutes(minutes));

    public override string ToString() => $"FakeClock({Now:O})";
}
