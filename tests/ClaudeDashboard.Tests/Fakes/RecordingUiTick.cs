using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// An <see cref="IUiTick"/> for the tests that have to supply one and are not about the tick.
/// </summary>
/// <remarks>
/// <see cref="ClaudeDashboard.App.Pipeline.EventConsumer"/> requires its tick, so that deleting
/// the registration throws at startup instead of leaving every age on screen frozen while the
/// suite stays green (T1.12b). The cost is that consumer tests unrelated to the tick still have
/// to hand one over. This is what they hand over: it counts, so a test that turns out to depend
/// on the tick can see it rather than guess, and it does nothing else.
/// </remarks>
internal sealed class RecordingUiTick : IUiTick
{
    /// <summary>How many times the consumer has ticked it.</summary>
    public int Calls { get; private set; }

    /// <summary>The instant of the most recent tick, or null if it has never been called.</summary>
    public DateTimeOffset? LastTick { get; private set; }

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        Calls++;
        LastTick = now;
    }
}
