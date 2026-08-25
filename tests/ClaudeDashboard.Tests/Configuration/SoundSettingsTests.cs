using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Configuration;

/// <summary>
/// The first setting anything consumes, and the first exercise of the one-way mapping
/// (Impl Part 7, Part 8; T1.7).
/// </summary>
public sealed class SoundSettingsTests
{
    /// <summary>An empty file leaves Core's defaults exactly as they are.</summary>
    /// <remarks>
    /// The important half is that these come from <see cref="SoundPolicyOptions"/> rather than
    /// being written out here: a second copy of a default in the settings layer would silently
    /// win the day Core's changed, and nothing would fail.
    /// </remarks>
    [Fact]
    public void Nothing_said_means_Cores_defaults()
    {
        var defaults = new SoundPolicyOptions();
        var mapped = new SoundSettings().Apply();

        Assert.Equal(defaults.MasterVolume, mapped.MasterVolume);
        Assert.Equal(defaults.NoticeGain, mapped.NoticeGain);
        Assert.Equal(defaults.NudgeGain, mapped.NudgeGain);
        Assert.Equal(defaults.NudgeOnError, mapped.NudgeOnError);
        Assert.Equal(defaults.NudgeLadder, mapped.NudgeLadder);
    }

    /// <summary>What the operator did say is applied.</summary>
    [Fact]
    public void What_the_operator_said_is_applied()
    {
        var mapped = new SoundSettings
        {
            MasterVolume = 0.4,
            NoticeGain = 0.9,
            NudgeGain = 0.2,
            NudgeOnError = false,
        }.Apply();

        Assert.Equal(0.4, mapped.MasterVolume);
        Assert.Equal(0.9, mapped.NoticeGain);
        Assert.Equal(0.2, mapped.NudgeGain);
        Assert.False(mapped.NudgeOnError);
    }

    /// <summary>
    /// <strong>The layering is real, not a coincidence of equal values.</strong>
    /// </summary>
    /// <remarks>
    /// Every assertion above would also pass against a mapper that ignored its argument and
    /// returned a fresh <see cref="SoundPolicyOptions"/>, because the defaults happen to match.
    /// This layers onto options that are <em>not</em> the defaults and checks that the untouched
    /// ones survive.
    /// </remarks>
    [Fact]
    public void It_layers_onto_what_it_is_given()
    {
        var unusual = new SoundPolicyOptions
        {
            NudgeLadder = [TimeSpan.FromMinutes(7)],
            UnreadNudgeAfter = null,
            NudgeOnError = false,
            NoticeGain = 0.8,
            NudgeGain = 0.1,
            MasterVolume = 0.5,
        };

        var mapped = new SoundSettings { MasterVolume = 0.25 }.Apply(unusual);

        // The one thing said, applied…
        Assert.Equal(0.25, mapped.MasterVolume);

        // …and everything not said, preserved from the argument rather than reset to a default.
        Assert.Equal(unusual.NudgeLadder, mapped.NudgeLadder);
        Assert.Null(mapped.UnreadNudgeAfter);
        Assert.False(mapped.NudgeOnError);
        Assert.Equal(0.8, mapped.NoticeGain);
        Assert.Equal(0.1, mapped.NudgeGain);
    }

    /// <summary>A value outside 0…1 falls back rather than stopping the dashboard.</summary>
    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void An_impossible_gain_falls_back(double gain)
    {
        var mapped = new SoundSettings { MasterVolume = gain, NoticeGain = gain }.Apply();

        Assert.Equal(SoundPolicyOptions.DefaultMasterVolume, mapped.MasterVolume);
        Assert.Equal(SoundPolicyOptions.DefaultNoticeGain, mapped.NoticeGain);
    }

    /// <summary>
    /// A nudge louder than a notice is repaired rather than thrown on.
    /// </summary>
    /// <remarks>
    /// <see cref="SoundPolicyOptions.Validate"/> throws on this, correctly — it catches a
    /// programming error where it is introduced. But this is a hand-edited file, and a dashboard
    /// that would not start because two numbers were typed in the wrong order is a worse failure
    /// than a nudge at the same volume as a notice. Asserted by constructing an engine, which is
    /// what actually calls Validate: if this returned the invalid pair, that would throw.
    /// </remarks>
    [Fact]
    public void A_nudge_louder_than_a_notice_is_brought_down_rather_than_rejected()
    {
        var mapped = new SoundSettings { NoticeGain = 0.5, NudgeGain = 0.9 }.Apply();

        Assert.Equal(0.5, mapped.NoticeGain);
        Assert.Equal(0.5, mapped.NudgeGain);

        // The proof that this is playable: Validate is what the engine runs, and it does not throw.
        mapped.Validate();
    }

    /// <summary>The JSON names are what a human would write.</summary>
    [Fact]
    public void It_reads_the_names_in_the_file()
    {
        var settings = JsonSerializer.Deserialize<DashboardSettings>(
            """
            { "port": 52789, "sound": { "masterVolume": 0.3, "nudgeOnError": false } }
            """);

        Assert.NotNull(settings);
        Assert.Equal(0.3, settings.Sound.MasterVolume);
        Assert.False(settings.Sound.NudgeOnError);

        // …and what was not written stays unsaid, so it lands on Core's default rather than on
        // a zero the deserialiser invented.
        Assert.Null(settings.Sound.NoticeGain);
        Assert.Equal(SoundPolicyOptions.DefaultNoticeGain, settings.Sound.Apply().NoticeGain);
    }

    /// <summary>
    /// <strong>The mapping runs one way.</strong> Core's options know nothing of a file.
    /// </summary>
    /// <remarks>
    /// T1.7 claimed this and nothing exercised it until a setting was actually consumed. Asserted
    /// structurally rather than by inspection: a <c>JsonPropertyName</c> on a Core type would be
    /// the first sign of the direction reversing, and it would arrive as a convenience.
    /// </remarks>
    [Fact]
    public void Core_options_carry_no_serialisation_attributes()
    {
        var attributed = typeof(SoundPolicyOptions)
            .GetProperties()
            .Where(property => property
                .GetCustomAttributes(inherit: true)
                .Any(attribute => attribute.GetType().Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true))
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            attributed.Count == 0,
            $"{nameof(SoundPolicyOptions)} has serialisation attributes on: {string.Join(", ", attributed)}. "
            + "Core owns the defaults; App maps a file onto them, and the arrow does not point back.");
    }
}
