using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// Reading the plan's limits out of a usage post, leniently (issue #133, MOD.4).
/// </summary>
/// <remarks>
/// The body is what another program sends, and ingress is a pure observer (Impl §3.3): nothing in it can make the
/// read throw. Only <c>sessionId</c> and <c>rateLimits</c> are read; a kind is kept as text.
/// </remarks>
public sealed class UsageReaderTests
{
    private static readonly DateTimeOffset HeardAt = new(2026, 10, 8, 19, 43, 48, TimeSpan.Zero);

    /// <summary>The first body the mod sent in the guide's lab ("What you get").</summary>
    private const string RealBody =
        """
        {
          "sessionId": "ab86443d-84b5-4342-85b4-a13111a93020",
          "context": { "tokens": 3575, "window": 1000000, "percent": 0 },
          "rateLimits": [
            { "kind": "five_hour", "percentUsed": 24, "resetsAt": "2026-10-08T23:10:00.000Z" },
            { "kind": "seven_day", "percentUsed": 13, "resetsAt": "2026-10-14T13:00:00.000Z" }
          ],
          "cost": { "usd": 0.0006151500000000001 },
          "changed": ["context", "rateLimits", "cost"]
        }
        """;

    // ---- The bounds ------------------------------------------------------------------------------

    /// <summary><strong>The first sixteen entries are read, and the number itself is pinned.</strong></summary>
    [Fact]
    public void The_most_entries_read_is_sixteen() =>
        Assert.Equal(16, UsageReader.MaxEntries);

    /// <summary>
    /// <strong>The longest session id kept is 128 characters, and the number itself is pinned</strong>: one of 128 is
    /// kept, and one of 129 reads as none.
    /// </summary>
    [Fact]
    public void The_longest_session_id_kept_is_128_characters()
    {
        Assert.Equal(128, UsageReader.MaxSessionIdLength);

        var longest = new string('s', 128);

        Assert.Equal(longest, Assert.Single(UsageReader.Read(WithSession($"\"{longest}\""), HeardAt)).SessionId);
        Assert.Null(Assert.Single(UsageReader.Read(WithSession($"\"{longest}s\""), HeardAt)).SessionId);
    }

    // ---- What is read ----------------------------------------------------------------------------

    /// <summary>The real body reads as its two limits, with the session, the instant given, and UTC reset times.</summary>
    [Fact]
    public void The_real_body_reads_as_its_two_limits()
    {
        var windows = UsageReader.Read(RealBody, HeardAt);

        Assert.Collection(
            windows,
            fiveHour =>
            {
                Assert.Equal("five_hour", fiveHour.Kind);
                Assert.Equal(24, fiveHour.PercentUsed);
                Assert.Equal(new DateTimeOffset(2026, 10, 8, 23, 10, 0, TimeSpan.Zero), fiveHour.ResetsAt);
                Assert.Equal(TimeSpan.Zero, fiveHour.ResetsAt!.Value.Offset);
                Assert.Equal(HeardAt, fiveHour.HeardAt);
                Assert.Equal("ab86443d-84b5-4342-85b4-a13111a93020", fiveHour.SessionId);
            },
            sevenDay =>
            {
                Assert.Equal("seven_day", sevenDay.Kind);
                Assert.Equal(13, sevenDay.PercentUsed);
                Assert.Equal(new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero), sevenDay.ResetsAt);
            });
    }

    /// <summary>
    /// <strong>A body of the wrong shape reads as nothing, and never throws</strong>: not JSON, empty, a value that is
    /// not an object, no <c>rateLimits</c>, a <c>rateLimits</c> that is not a list, and JSON nested past the parser's
    /// depth.
    /// </summary>
    [Fact]
    public void A_body_of_the_wrong_shape_reads_as_nothing()
    {
        string[] bodies =
        [
            "{not json",
            string.Empty,
            "null",
            "42",
            "\"rateLimits\"",
            """[{ "kind": "five_hour", "percentUsed": 24 }]""",
            """{ "sessionId": "s-1" }""",
            """{ "rateLimits": null }""",
            """{ "rateLimits": { "kind": "five_hour", "percentUsed": 24 } }""",
            """{ "rateLimits": "five_hour 24" }""",
            """{ "rateLimits": [ """ + new string('[', 200) + new string(']', 200) + " ] }",
        ];

        Assert.All(bodies, body => Assert.Empty(UsageReader.Read(body, HeardAt)));
    }

    /// <summary>
    /// <strong>An entry of the wrong shape is passed over, and the rest are read</strong>: an entry that is not an
    /// object, a kind that is not a string (a number is not bound as text), and a percentage that is absent or not a
    /// number.
    /// </summary>
    [Fact]
    public void An_entry_of_the_wrong_shape_is_passed_over_and_the_rest_are_read()
    {
        var windows = UsageReader.Read(
            """
            { "rateLimits": [
                5,
                "five_hour",
                null,
                { "kind": 5, "percentUsed": 10 },
                { "kind": null, "percentUsed": 10 },
                { "percentUsed": 10 },
                { "kind": "no_percent" },
                { "kind": "text_percent", "percentUsed": "12" },
                { "kind": "seven_day", "percentUsed": 8.5 },
                { "kind": "five_hour", "percentUsed": 24 }
            ] }
            """,
            HeardAt);

        Assert.Equal(["seven_day", "five_hour"], windows.Select(window => window.Kind));
        Assert.Equal([8.5, 24], windows.Select(window => window.PercentUsed));
    }

    /// <summary>
    /// <strong>A reset time that is not a time reads as none</strong>, and the limit is still read. A time with an
    /// offset is read as the same instant in UTC; one with no offset is taken as UTC.
    /// </summary>
    [Fact]
    public void A_reset_time_that_is_not_a_time_reads_as_none()
    {
        var windows = UsageReader.Read(
            """
            { "rateLimits": [
                { "kind": "a_absent", "percentUsed": 1 },
                { "kind": "b_null", "percentUsed": 1, "resetsAt": null },
                { "kind": "c_word", "percentUsed": 1, "resetsAt": "soon" },
                { "kind": "d_number", "percentUsed": 1, "resetsAt": 1760000000 },
                { "kind": "e_offset", "percentUsed": 1, "resetsAt": "2026-10-08T19:10:00-04:00" },
                { "kind": "f_no_offset", "percentUsed": 1, "resetsAt": "2026-10-08T23:10:00" }
            ] }
            """,
            HeardAt);

        var instant = new DateTimeOffset(2026, 10, 8, 23, 10, 0, TimeSpan.Zero);

        Assert.Equal(
            [null, null, null, null, instant, instant],
            windows.Select(window => window.ResetsAt));
        Assert.All(windows.Where(window => window.ResetsAt is not null), window => Assert.Equal(TimeSpan.Zero, window.ResetsAt!.Value.Offset));
    }

    /// <summary>
    /// <strong>A session id that is not a short string reads as none</strong>, and the limits are still read: absent,
    /// null, empty, a number, an object, and longer than 128 characters.
    /// </summary>
    [Fact]
    public void A_session_id_that_is_not_a_short_string_reads_as_none()
    {
        string[] ids = ["null", "\"\"", "42", """{ "id": "s-1" }""", $"\"{new string('s', UsageReader.MaxSessionIdLength + 1)}\""];

        Assert.Null(Assert.Single(UsageReader.Read("""{ "rateLimits": [ { "kind": "five_hour", "percentUsed": 24 } ] }""", HeardAt)).SessionId);
        Assert.All(ids, id => Assert.Null(Assert.Single(UsageReader.Read(WithSession(id), HeardAt)).SessionId));
    }

    /// <summary><strong>Only the first sixteen entries are read</strong>; a post cannot make the read grow without end.</summary>
    [Fact]
    public void Only_the_first_sixteen_entries_are_read()
    {
        var entries = string.Join(", ", Enumerable.Range(1, 20).Select(n => $$"""{ "kind": "kind_{{n}}", "percentUsed": {{n}} }"""));

        var windows = UsageReader.Read($$"""{ "rateLimits": [ {{entries}} ] }""", HeardAt);

        Assert.Equal(Enumerable.Range(1, 16).Select(n => $"kind_{n}"), windows.Select(window => window.Kind));
    }

    private static string WithSession(string sessionJson) =>
        $$"""{ "sessionId": {{sessionJson}}, "rateLimits": [ { "kind": "five_hour", "percentUsed": 24 } ] }""";
}
