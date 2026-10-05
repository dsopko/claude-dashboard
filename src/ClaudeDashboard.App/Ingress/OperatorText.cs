using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// A title or a task description on its way out of <c>/state</c> (T1.46), written as a JSON string by
/// <see cref="OperatorTextJsonConverter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It prints its text</strong> (T1.76, issue #118; the operator's ruling of 2026-10-05). <see cref="ToString"/>
/// gives the text to a message template's <c>{Report}</c> and to an interpolated string, and <see cref="Text"/> gives
/// it to a destructured <c>{@Report}</c>. Until T1.76 it was kept out of the log file; the same titles are in
/// <c>dashboard.db</c> beside it, so that protected nothing.
/// </para>
/// <para>
/// <strong>Why it stays a type:</strong> it holds the converter that writes it as a plain string, and the equality of
/// a value.
/// </para>
/// </remarks>
[JsonConverter(typeof(OperatorTextJsonConverter))]
public readonly record struct OperatorText
{
    private readonly string? _value;

    /// <summary>Wraps a title or a task description.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public OperatorText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _value = value;
    }

    /// <summary>The text, the same as <see cref="Text"/>. The converter reads it.</summary>
    public string Reveal() => Text;

    /// <summary>The text, as a property, so that a destructured <c>{@Report}</c> prints it too (T1.76).</summary>
    public string Text => _value ?? string.Empty;

    /// <summary>How many characters the text holds.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>True for <c>default(OperatorText)</c> and for an empty string.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>The text itself: what a log line's <c>{Report}</c> and an interpolated string print (T1.76).</summary>
    public override string ToString() => Text;
}

/// <summary>
/// Writes an <see cref="OperatorText"/> as the JSON string it holds, so <c>/state</c> answers with the real title.
/// </summary>
public sealed class OperatorTextJsonConverter : JsonConverter<OperatorText>
{
    /// <inheritdoc/>
    public override OperatorText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } text ? new OperatorText(text) : default;

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, OperatorText value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.Reveal());
    }
}
