using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// An <see cref="IAckPublisher"/> for the tests that have to supply one and do not care what it
/// does.
/// </summary>
/// <remarks>
/// <see cref="MainViewModel"/> requires a publisher, so that losing its registration throws at
/// startup instead of silently disabling every Ack button in the shipped app. The cost of that is
/// that tests about collapsing, motion, or the tick still have to hand one over. This is what they
/// hand over: it records what it was asked to acknowledge — so a test that turns out to depend on
/// the publisher can see it rather than guess — and it never goes near the event channel. Tests
/// that are actually about acknowledgment use the real <see cref="AckPublisher"/> over a
/// <see cref="RecordingEventSink"/>.
/// </remarks>
internal sealed class StubAckPublisher : IAckPublisher
{
    private readonly List<SessionId> _asked = [];

    /// <summary>The sessions this was asked to acknowledge, in order.</summary>
    public IReadOnlyList<SessionId> Asked => _asked;

    /// <summary>What <see cref="Acknowledge"/> answers. Accepted, unless a test says otherwise.</summary>
    public bool Accepts { get; set; } = true;

    /// <inheritdoc/>
    public bool Acknowledge(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _asked.Add(session.Id);
        return Accepts;
    }
}
