using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// Where events are durably recorded (Impl Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a layer. <see cref="SqliteEventStore"/> is the only implementation that
/// ships; this exists so the archive's behaviour when the disk refuses can be tested at all. On a
/// machine where writing works, the degrade path never runs — the same problem the virtual-desktop
/// adapter has, and the same answer.
/// </para>
/// <para>
/// <strong>Write-only in Phase 1, deliberately.</strong> Nothing reads the database back until
/// Phase 5, so there is no query method here and adding one "while we are in the file" would be
/// building a surface with no caller and no test that could hold it honest.
/// </para>
/// <para>
/// This interface lives in App rather than Core because nothing in Core calls it. Core carries
/// <see cref="PayloadJson"/> only because the event does.
/// </para>
/// </remarks>
public interface IEventStore
{
    /// <summary>Appends one event. Never throws.</summary>
    /// <returns>
    /// <see langword="true"/> if the row was written. <see langword="false"/> if it was not — a
    /// dead disk is not a dead dashboard (TS §IV.7), so a failure here is a lost row and nothing
    /// more.
    /// </returns>
    bool Append(InboundEvent inboundEvent);
}
