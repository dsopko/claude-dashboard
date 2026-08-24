using System.Collections;
using System.Collections.Immutable;

namespace ClaudeDashboard.Core;

/// <summary>
/// A session's "small transition log" (Impl §2.1): a bounded, immutable, oldest-first
/// history of its recent <see cref="StateTransition"/>s.
/// </summary>
/// <remarks>
/// Bounded on purpose. A long-lived session can see thousands of transitions, and the log
/// exists to explain <em>how a row got the way it is</em>, not to be an audit trail —
/// durable history is the persistence layer's job (Impl §8). Appending past
/// <see cref="Capacity"/> drops the oldest entry.
///
/// This is a hand-written value type rather than a bare <see cref="ImmutableArray{T}"/>
/// because <see cref="Session"/> is a record: its generated equality compares members with
/// <c>EqualityComparer&lt;T&gt;.Default</c>, and <see cref="ImmutableArray{T}"/> compares by
/// underlying array reference. Two sessions with identical histories would compare unequal.
/// Implementing sequence equality here makes <see cref="Session"/>'s value equality honest.
/// </remarks>
public sealed class TransitionLog : IReadOnlyList<StateTransition>, IEquatable<TransitionLog>
{
    /// <summary>The most transitions a log retains before dropping the oldest.</summary>
    public const int Capacity = 32;

    /// <summary>The empty log — the starting point for every session.</summary>
    public static readonly TransitionLog Empty = new(ImmutableArray<StateTransition>.Empty);

    private readonly ImmutableArray<StateTransition> _entries;

    private TransitionLog(ImmutableArray<StateTransition> entries) => _entries = entries;

    /// <summary>Builds a log from an existing sequence, keeping at most the last <see cref="Capacity"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    public static TransitionLog From(IEnumerable<StateTransition> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var materialized = entries.ToImmutableArray();
        if (materialized.Length > Capacity)
        {
            materialized = ImmutableArray.CreateRange(materialized, materialized.Length - Capacity, Capacity, static e => e);
        }

        return materialized.IsEmpty ? Empty : new TransitionLog(materialized);
    }

    /// <summary>
    /// Returns a new log with <paramref name="transition"/> appended, dropping the oldest
    /// entry if the log is already at <see cref="Capacity"/>. This instance is unchanged.
    /// </summary>
    public TransitionLog Append(StateTransition transition)
    {
        var appended = _entries.Add(transition);
        if (appended.Length > Capacity)
        {
            appended = ImmutableArray.CreateRange(appended, appended.Length - Capacity, Capacity, static e => e);
        }

        return new TransitionLog(appended);
    }

    /// <summary>The number of retained transitions.</summary>
    public int Count => _entries.Length;

    /// <summary>The transition at <paramref name="index"/>, oldest first.</summary>
    public StateTransition this[int index] => _entries[index];

    public IEnumerator<StateTransition> GetEnumerator() => ((IEnumerable<StateTransition>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(TransitionLog? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null && _entries.AsSpan().SequenceEqual(other._entries.AsSpan());
    }

    public override bool Equals(object? obj) => Equals(obj as TransitionLog);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var entry in _entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(TransitionLog? left, TransitionLog? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(TransitionLog? left, TransitionLog? right) => !(left == right);
}
