using System.Text.Json.Serialization;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// Where the dashboard window was left, and whether it floats (Impl §5.4, Part 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every field is nullable and means "the operator has not said".</strong> The same
/// direction as <c>SoundSettings</c>: the file layers onto a default, never the other way round.
/// A first run has no saved position and must open somewhere sensible rather than at 0,0.
/// </para>
/// <para>
/// <strong>Position is stored, not trusted.</strong> A monitor that was there yesterday may be
/// gone today — a laptop undocked, a screen unplugged — and restoring a window to coordinates no
/// display covers puts it somewhere the operator cannot reach and cannot see. So these are an
/// input to <c>WindowPlacement</c>, which decides, rather than values applied directly.
/// </para>
/// </remarks>
public sealed record WindowSettings
{
    /// <summary>The left edge, in virtual-screen coordinates, or null on a first run.</summary>
    [JsonPropertyName("left")]
    public double? Left { get; init; }

    /// <summary>The top edge, in virtual-screen coordinates, or null on a first run.</summary>
    [JsonPropertyName("top")]
    public double? Top { get; init; }

    /// <summary>The window width, or null to use the XAML default.</summary>
    [JsonPropertyName("width")]
    public double? Width { get; init; }

    /// <summary>The window height, or null to use the XAML default.</summary>
    [JsonPropertyName("height")]
    public double? Height { get; init; }

    /// <summary>
    /// Whether the window floats above other windows. Default <strong>off</strong> (Impl §5.4).
    /// </summary>
    /// <remarks>
    /// Off by design rather than by oversight: on-top is what an operator wants when the dashboard
    /// lives on a spare status monitor, and an imposition everywhere else.
    /// </remarks>
    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; init; }

    /// <summary>Whether a saved position is present at all.</summary>
    public bool HasPosition => Left is not null && Top is not null;
}
