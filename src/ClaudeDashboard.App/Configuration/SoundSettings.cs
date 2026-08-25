using System.Text.Json.Serialization;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// The sound settings a human may edit, and the one-way map onto Core's policy (Impl Part 7,
/// Part 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mapping runs one way: file → domain options.</strong> T1.7 claimed that and
/// nothing exercised it until now, because <see cref="DashboardSettings"/> deliberately carried
/// only what something already read. This is the first setting consumed, so it is the first
/// chance to get the direction wrong.
/// </para>
/// <para>
/// What that rules out, concretely: <see cref="SoundPolicyOptions"/> gains no
/// <c>JsonPropertyName</c>, no knowledge that a file exists, and no way to be written back. Core
/// owns the authoritative defaults; every property here is nullable and means "the operator did
/// not say", so an absent key and an absent file both land on Core's value rather than on a
/// second copy of it maintained here. A non-nullable <c>double MasterVolume = 1.0</c> would be
/// exactly that second copy, and it would silently win the day Core's default changed.
/// </para>
/// <para>
/// Out-of-range values fall back rather than throwing, like <see cref="DashboardSettings.Port"/>:
/// a typo in a hand-edited file must not stop the dashboard starting, and a dashboard that
/// refused to run because a volume read <c>1.5</c> would be a worse failure than a loud beep.
/// </para>
/// </remarks>
public sealed record SoundSettings
{
    /// <summary>
    /// How loud everything is, 0 to 1. Absent means Core's default.
    /// </summary>
    [JsonPropertyName("masterVolume")]
    public double? MasterVolume { get; init; }

    /// <summary>
    /// The gain a notice plays at, 0 to 1. Absent means Core's default.
    /// </summary>
    [JsonPropertyName("noticeGain")]
    public double? NoticeGain { get; init; }

    /// <summary>
    /// The gain a nudge plays at, 0 to 1. Absent means Core's default.
    /// </summary>
    [JsonPropertyName("nudgeGain")]
    public double? NudgeGain { get; init; }

    /// <summary>
    /// Whether an errored session nudges like a blocked one. Absent means Core's default.
    /// </summary>
    [JsonPropertyName("nudgeOnError")]
    public bool? NudgeOnError { get; init; }

    /// <summary>
    /// Applies whatever the operator actually said onto Core's defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starts from a fresh <see cref="SoundPolicyOptions"/> — Core's defaults — and overrides
    /// only the properties that are present and usable. Nothing here reproduces a default value.
    /// </para>
    /// <para>
    /// <strong>A nudge louder than a notice is repaired, not rejected.</strong>
    /// <see cref="SoundPolicyOptions.Validate"/> throws on that, and it is right to: it catches a
    /// programming error where it is introduced. But this is a hand-edited file, and throwing
    /// here would mean a dashboard that will not start because someone typed two numbers in the
    /// wrong order. So the pair is checked before it is applied, and a nudge that would outrank a
    /// notice is dropped back to it, which is the closest playable thing to what was asked for.
    /// </para>
    /// </remarks>
    /// <param name="defaults">
    /// The options to layer onto. Defaults to Core's own, which is the production case; a test
    /// passes its own to show the layering is real rather than a coincidence of equal values.
    /// </param>
    public SoundPolicyOptions Apply(SoundPolicyOptions? defaults = null)
    {
        var options = defaults ?? new SoundPolicyOptions();

        var notice = Usable(NoticeGain) ?? options.NoticeGain;
        var nudge = Usable(NudgeGain) ?? options.NudgeGain;

        return options with
        {
            MasterVolume = Usable(MasterVolume) ?? options.MasterVolume,
            NoticeGain = notice,
            NudgeGain = Math.Min(nudge, notice),
            NudgeOnError = NudgeOnError ?? options.NudgeOnError,
        };
    }

    /// <summary>A gain the operator gave that can actually be used, or null.</summary>
    private static double? Usable(double? gain) =>
        gain is >= 0.0 and <= 1.0 ? gain : null;
}
