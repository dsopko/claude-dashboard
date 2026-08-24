namespace ClaudeDashboard.Core;

/// <summary>
/// Claude Code's <c>session_id</c>, the Registry key (TS §II.3; Impl §2.1).
/// </summary>
/// <remarks>
/// A wrapper rather than a bare string so a session id cannot be silently confused with
/// a <c>prompt_id</c>, a <c>cwd</c>, or any other opaque string on the same payload.
/// Comparison is ordinal: the value is an opaque identifier, never a display string.
/// </remarks>
public readonly record struct SessionId
{
    private readonly string? _value;

    /// <summary>Wraps a non-empty <c>session_id</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null, empty, or whitespace.</exception>
    public SessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A session id must be a non-empty string.", nameof(value));
        }

        _value = value;
    }

    /// <summary>The underlying <c>session_id</c>. Never null; empty only for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True for <c>default(SessionId)</c>, which names no session.</summary>
    /// <remarks>See <see cref="ValueTypeConventions"/> for why these types stay structs.</remarks>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public override string ToString() => Value;
}
