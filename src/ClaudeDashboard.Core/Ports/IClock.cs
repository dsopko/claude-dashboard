namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// The source of "now" for everything in Core that reasons about time (Impl §1.3).
/// Implemented in App over the system clock; implemented in Tests by a clock the test drives.
/// </summary>
/// <remarks>
/// <para>
/// Injectable so state-machine timing and nudge scheduling are testable (Impl §1.3) — nothing
/// in Core calls <see cref="DateTimeOffset.UtcNow"/> directly, or a test could only assert
/// timing by really waiting.
/// </para>
/// <para>
/// <strong>This port is deliberately only <see cref="Now"/>.</strong> It exposes no timer,
/// no delay, and no scheduling primitive, because Core does not schedule anything: TS §IV.5
/// describes the sound policy as "a pure timer over Registry state", meaning each
/// nudge-eligible session simply <em>holds</em> a next-nudge time that is compared against
/// <see cref="Now"/> when the engine is asked to evaluate. Deciding <em>when</em> to ask —
/// a real timer, a dispatcher tick, a background loop — is a host concern and lives in App.
/// The consequence for T1.5 is that its widening 2 → 5 → 10 minute schedule is tested by
/// setting the clock forward and evaluating, with no waiting and no virtual scheduler.
/// </para>
/// <para>
/// <see cref="Now"/> is wall-clock rather than monotonic, deliberately. Its readings are
/// stored on events and sessions, persisted, replayed on warm restart (Impl §8), and
/// rendered as ages, all of which require a real point in time. A backwards wall-clock
/// correction can delay a nudge by the size of the correction; that is a soft failure on a
/// minutes-scale schedule, and adding a second monotonic reading to guard against it would
/// buy nothing T1.5 actually needs.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>
    /// The current instant, including offset. Must be non-decreasing under normal operation
    /// and cheap enough to call on the ingress hot path, where every inbound event is stamped.
    /// </summary>
    DateTimeOffset Now { get; }
}
