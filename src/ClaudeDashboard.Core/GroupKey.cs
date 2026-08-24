namespace ClaudeDashboard.Core;

/// <summary>
/// The key a session is grouped under (TS §IV.3; Impl §2.1).
/// </summary>
/// <remarks>
/// Impl §2.1 gives <see cref="Session"/> a <c>Group</c> member and also names <see cref="Group"/>
/// as "a derived container keyed by <c>Cwd</c>". Those cannot be the same thing: a container
/// holding its members while each member holds the container would be a cycle, and an
/// immutable record graph cannot express one. So <see cref="Session.Group"/> carries this
/// <em>key</em>, and <see cref="Group"/> is the container derived from sessions sharing it.
///
/// Phase 1 keys on <c>cwd</c>; Phase 4 keys on virtual-desktop id (TS §IV.3). The key is
/// therefore an opaque string, deliberately not a path type — Core is portable and must not
/// reason about Windows paths. Deciding <em>which</em> key a session gets, and any path
/// normalization that implies, belongs to the group resolver (T1.4).
/// </remarks>
public readonly record struct GroupKey
{
    private readonly string? _value;

    /// <summary>Wraps a non-empty group key.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null, empty, or whitespace.</exception>
    public GroupKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A group key must be a non-empty string.", nameof(value));
        }

        _value = value;
    }

    /// <summary>The underlying key. Never null; empty only for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True for <c>default(GroupKey)</c>, which names no group.</summary>
    /// <remarks>See <see cref="ValueTypeConventions"/> for why these types stay structs.</remarks>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public override string ToString() => Value;
}
