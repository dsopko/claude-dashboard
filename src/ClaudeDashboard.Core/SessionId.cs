namespace ClaudeDashboard.Core;

/// <summary>
/// Claude Code's <c>session_id</c>, the Registry key (TS §II.3; Impl §2.1).
/// </summary>
/// <remarks>
/// <para>
/// A wrapper rather than a bare string so a session id cannot be silently confused with
/// a <c>prompt_id</c>, a <c>cwd</c>, or any other opaque string on the same payload.
/// </para>
/// <para>
/// <strong>Opaque to this application.</strong> The value is never parsed, never split for
/// meaning, and never assumed to be a GUID — Claude Code supplies it and any non-empty string is
/// accepted. Comparison is ordinal, so two ids differing only by case are two different sessions.
/// </para>
/// <para>
/// <strong>It may be shown to the operator, and since T1.23 it is.</strong> This remark used to end
/// "never a display string", which stopped being true when the expanded row started showing the
/// first eight characters with the whole value in a tooltip (issue #15). The sentence is rewritten
/// rather than preserved by routing the display through a differently-named property: the value
/// is displayed, and a claim that it is not would be one a future reader would trust and be wrong
/// about. What survives is the half that was always doing the work — <em>opaque</em>, meaning
/// this application reads no structure into it, not <em>hidden</em>.
/// </para>
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
