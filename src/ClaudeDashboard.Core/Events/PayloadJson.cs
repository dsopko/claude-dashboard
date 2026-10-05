namespace ClaudeDashboard.Core.Events;

/// <summary>
/// <strong>The raw hook body</strong>, carried to the event archive as it arrived (Impl Part 8; T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It prints its text</strong> (T1.76, issue #118; the operator's ruling of 2026-10-05: "log everything,
/// don't censor anything"). <see cref="ToString"/>, which is what a message template's <c>{Payload}</c> and an
/// interpolated string call, gives the body; <see cref="Text"/> gives it to a destructured <c>{@Payload}</c>.
/// Until T1.76 this type existed to keep the body out of the log file. The log file and <c>dashboard.db</c> sit
/// in the same folder, and the database holds the body in plain form, so that protected nothing.
/// </para>
/// <para>
/// <strong>Why it stays a type.</strong> It keeps the body as one value with the equality of a value, apart from
/// the mapped fields beside it; removing it would touch every event for no gain.
/// </para>
/// <para>
/// <strong>One residual from before, still true:</strong> a <c>System.Text.Json</c> syntax error reports the
/// offending character (<c>'M' is an invalid start of a value</c>), and <c>IngressEndpoints</c> logs that message.
/// </para>
/// </remarks>
public readonly record struct PayloadJson
{
    private readonly string? _value;

    /// <summary>Wraps a raw hook body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public PayloadJson(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _value = value;
    }

    /// <summary>The raw JSON, the same as <see cref="Text"/>. The archive's insert reads it.</summary>
    public string Reveal() => Text;

    /// <summary>
    /// The raw JSON, as a property, so that a destructured <c>{@Payload}</c> prints it too (T1.76).
    /// </summary>
    public string Text => _value ?? string.Empty;

    /// <summary>How many characters the body holds.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>True for <c>default(PayloadJson)</c>, which carries no body.</summary>
    /// <remarks>See <c>ValueTypeConventions</c> for why these types stay structs.</remarks>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>The body itself: what a log line's <c>{Payload}</c> and an interpolated string print (T1.76).</summary>
    public override string ToString() => Text;
}
