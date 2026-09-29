using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// A title or a task description on its way out of <c>/state</c> (T1.46), kept out of the log by
/// construction. Modelled on <see cref="Core.Events.PayloadJson"/>, and it protects exactly what
/// that type protects: this one value, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the text is sent at all.</strong> The operator ruled that <c>/state</c> carries
/// session titles and background-task descriptions, so a caller can tell sessions apart. Both are
/// operator-adjacent text: a title can be a model-written summary of the first prompt, and a
/// description is what the agent said the task is. They are in
/// <c>UnprotectedTextInventory.CarriesOperatorText</c> everywhere else they live.
/// </para>
/// <para>
/// <strong>Wrapped, so the inventory does not grow.</strong> A response type with a public
/// <see langword="string"/> title would be one more place a careless <c>{@Report}</c> prints the
/// operator's words. This has no public string property, so Serilog's destructurer finds only
/// <see cref="Length"/> and <see cref="IsEmpty"/>, and <see cref="ToString"/> — what a plain
/// <c>{Report}</c> renders — gives a size. The text leaves only through
/// <see cref="OperatorTextJsonConverter"/>, which is the endpoint's serializer and nothing else.
/// </para>
/// <para>
/// <strong>What this does not protect.</strong> The same words stay plain strings on
/// <c>Session.Title</c> and <c>WaitingTask.Description</c>, where this copies them from. Wrapping
/// those is issue #11's work, not this type's.
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

    /// <summary>
    /// The text. <strong>The only way to it.</strong> A method rather than a property, because
    /// Serilog's <c>{@}</c> reflects over properties and never calls methods — the reason
    /// <see cref="Core.Events.PayloadJson.Reveal"/> is a method too.
    /// </summary>
    /// <remarks>
    /// One caller in the product: <see cref="OperatorTextJsonConverter"/>. A second needs the
    /// same argument this type's remarks make.
    /// </remarks>
    public string Reveal() => _value ?? string.Empty;

    /// <summary>How many characters the text holds. Safe to log, and safe to destructure.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>True for <c>default(OperatorText)</c> and for an empty string.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>A size, never the content. This is what Serilog renders for <c>{Report}</c>.</summary>
    /// <remarks>
    /// <strong>Do not "improve" this to show a prefix.</strong> A prefix of a title can be the
    /// beginning of a summary of the operator's prompt.
    /// </remarks>
    public override string ToString() =>
        IsEmpty ? "<text: none>" : $"<text: {Length} chars>";
}

/// <summary>
/// Writes an <see cref="OperatorText"/> as the JSON string it holds, so <c>/state</c> answers
/// with the real title while the log never sees it.
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
