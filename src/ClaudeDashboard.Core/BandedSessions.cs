using System.Collections.Immutable;

namespace ClaudeDashboard.Core;

/// <summary>
/// One labelled band of the flat view: the band, and the sessions in it, already ordered
/// (TS §IV.2).
/// </summary>
/// <remarks>
/// Never empty — <see cref="AttentionEngine.Order"/> omits a band with no sessions rather than
/// returning it empty, so a caller can render one header per element without checking.
/// </remarks>
public sealed record BandedSessions
{
    private readonly ImmutableArray<Session> _sessions;

    /// <summary>Builds a band over at least one session.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessions"/> is empty.</exception>
    public BandedSessions(AttentionBand band, IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        Band = band;
        _sessions = sessions.ToImmutableArray();

        if (_sessions.IsEmpty)
        {
            throw new ArgumentException("A band with no sessions is not rendered.", nameof(sessions));
        }
    }

    /// <summary>Which band this is.</summary>
    public AttentionBand Band { get; }

    /// <summary>The sessions in it, in display order.</summary>
    public IReadOnlyList<Session> Sessions => _sessions;

    /// <summary>
    /// Value equality over the band and the session sequence. Written by hand for the same
    /// reason <see cref="TransitionLog"/> is: the synthesized version would compare
    /// <see cref="ImmutableArray{T}"/> by underlying array reference, so two identical bands
    /// would compare unequal and a bound collection would churn on every event.
    /// </summary>
    public bool Equals(BandedSessions? other) =>
        other is not null &&
        Band == other.Band &&
        _sessions.AsSpan().SequenceEqual(other._sessions.AsSpan());

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Band);
        foreach (var session in _sessions)
        {
            hash.Add(session);
        }

        return hash.ToHashCode();
    }
}
