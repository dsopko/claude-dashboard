using System.Reflection;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// Rosters in <c>settings.json</c>: the shape, what survives a restart, and what a hand edit
/// cannot make the store hold (T1.25, issue #16).
/// </summary>
public sealed class RosterSettingsTests
{
    /// <summary>Rosters survive a round trip through the file's own serializer.</summary>
    /// <remarks>
    /// This is criterion "a roster survives a restart" reduced to the part that can fail: the
    /// application reads the file at startup and nowhere else, so a round trip through the same
    /// options the store uses is the whole of the persistence.
    /// </remarks>
    [Fact]
    public void Rosters_survive_a_round_trip()
    {
        var settings = new DashboardSettings
        {
            Rosters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["orchestration"] = ["Director", "Coder", "Reviewer"],
            },
        };

        var json = JsonSerializer.Serialize(settings);
        var back = JsonSerializer.Deserialize<DashboardSettings>(json)!;

        Assert.Equal(settings, back);
        Assert.Equal(["Director", "Coder", "Reviewer"], back.Rosters["orchestration"]);
    }

    /// <summary>
    /// <strong>Equality compares the rosters by content, not by reference.</strong>
    /// </summary>
    /// <remarks>
    /// A record's synthesized equality compares a dictionary by reference, so before
    /// <c>DashboardSettings.Equals</c> was written by hand, settings that had been through the file
    /// compared unequal to the ones that produced them. The same trap <c>Group</c> already carries a
    /// hand-written comparison for.
    /// </remarks>
    [Fact]
    public void Two_settings_with_the_same_rosters_are_equal()
    {
        static DashboardSettings Build() => new()
        {
            Rosters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["orchestration"] = ["Director"],
            },
        };

        Assert.Equal(Build(), Build());
        Assert.NotEqual(Build(), new DashboardSettings());
    }

    /// <summary>
    /// <strong>Every property of the settings takes part in equality.</strong>
    /// </summary>
    /// <remarks>
    /// The hand-written <c>Equals</c> is what makes the round trip work, and a hand-written
    /// comparison is exactly the kind that silently omits the next member somebody adds. This
    /// asserts the set it has to cover, in the same idiom <c>UnprotectedTextInventory</c> uses: add
    /// a property and this fails until you have said whether equality should see it.
    /// </remarks>
    [Fact]
    public void Every_settings_property_is_covered_by_equality()
    {
        var found = typeof(DashboardSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["InstallHooksAtStart", "Logging", "Port", "Rosters", "Sound", "Window"], found);
    }

    /// <summary>An absent section is no rosters, and says nothing about it.</summary>
    /// <remarks>
    /// The ordinary case: every operator who has never made a roster. It must be silent, or the log
    /// would carry a line about rosters on every start for everyone who does not use them.
    /// </remarks>
    [Fact]
    public void An_absent_section_yields_no_rosters_and_no_corrections()
    {
        var settings = JsonSerializer.Deserialize<DashboardSettings>("""{"port":52789}""")!;
        var (book, corrections) = new RosterSettings { Rosters = settings.Rosters }.ToBook();

        Assert.True(book.IsEmpty);
        Assert.Empty(corrections);
    }

    /// <summary>
    /// <strong>A hand edit holding a name in two rosters is corrected, and the correction never
    /// names the member.</strong>
    /// </summary>
    /// <remarks>
    /// Rule 4 is an invariant of the store and a file is not the store. The message says how many
    /// names were kept rather than which, because a member name is a session title and a title can
    /// carry the operator's words.
    /// </remarks>
    [Fact]
    public void A_name_in_two_rosters_is_corrected_without_naming_it()
    {
        const string Secret = "zqx-member-name";

        var (book, corrections) = new RosterSettings
        {
            Rosters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["orchestration"] = ["Director", Secret],
                ["docs"] = [Secret],
            },
        }.ToBook();

        Assert.Equal("orchestration", book.RosterFor(Secret));
        Assert.DoesNotContain(book.Rosters, roster => roster.Name == "docs");

        var correction = Assert.Single(corrections);

        Assert.Contains("docs", correction, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, correction, StringComparison.Ordinal);
    }

    /// <summary>A roster with no members in the file does not become an empty roster in the store.</summary>
    [Fact]
    public void A_roster_with_no_members_is_dropped_and_reported()
    {
        var (book, corrections) = new RosterSettings
        {
            Rosters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["empty"] = [],
            },
        }.ToBook();

        Assert.True(book.IsEmpty);
        Assert.Contains("empty", Assert.Single(corrections), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A malformed section loses the whole file, exactly as a malformed port does.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted rather than left implicit, because it is a decision and not an accident. A tolerant
    /// converter on this one property would leave two behaviours for one class of fault, and the
    /// newer one would look like the rule.
    /// </para>
    /// <para>
    /// The application still starts: <c>SettingsStore</c> reports the problem and falls back to
    /// defaults, which is where "degrade, never crash" is honoured. What is lost is the operator's
    /// other settings for that run, and that is a pre-existing behaviour of every field in the
    /// file — recorded in the acceptance notes rather than fixed here.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_malformed_section_fails_the_file_the_same_way_a_malformed_port_does()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DashboardSettings>("""{"rosters":5}"""));

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DashboardSettings>("""{"port":"not a number"}"""));
    }
}
