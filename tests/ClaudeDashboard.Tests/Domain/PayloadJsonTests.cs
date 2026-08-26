using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The wrapper that keeps the <strong>raw hook body</strong> out of the log by construction, and
/// the record of what it does not cover (T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A privacy claim has no oracle unless you go and find one.</strong> "The payload does not
/// reach the log" is the kind of claim that is easy to assert against our own idea of how logging
/// works and impossible to falsify that way. So the assertions here ask <em>Serilog</em> what it
/// rendered, using a real logger and a real sink. If Serilog ever renders a value some other way —
/// a destructuring operator, a different formatter, a future version — these fail, and a rule
/// written in a document would not have.
/// </para>
/// <para>
/// The interpolated-string case matters just as much and is easier to write by accident: a
/// developer reaching for <c>$"...{payload}..."</c> calls <c>ToString</c> directly, and no logging
/// library is involved at all.
/// </para>
/// </remarks>
public sealed class PayloadJsonTests
{
    private const string Secret = "MY-PROMPT-ABOUT-THE-DIVORCE-SETTLEMENT";

    private static readonly string Body = $$"""{"prompt":"{{Secret}}"}""";

    // ---- The value is reachable, deliberately and in exactly one way ----------------------------

    [Fact]
    public void The_body_is_readable_through_value()
    {
        Assert.Equal(Body, new PayloadJson(Body).Reveal());
        Assert.Equal(Body.Length, new PayloadJson(Body).Length);
    }

    [Fact]
    public void A_default_payload_carries_nothing_and_says_so()
    {
        var empty = default(PayloadJson);

        Assert.True(empty.IsEmpty);
        Assert.Equal(string.Empty, empty.Reveal());
        Assert.Equal(0, empty.Length);
    }

    [Fact]
    public void It_refuses_a_null_body() =>
        Assert.Throws<ArgumentNullException>(() => new PayloadJson(null!));

    // ---- What it renders as, asked of the thing that renders it ---------------------------------

    /// <summary>
    /// <strong>Serilog itself is asked what a <c>{Payload}</c> in a template produces.</strong>
    /// </summary>
    /// <remarks>
    /// The oracle the implementation does not control. A test that compared
    /// <c>payload.ToString()</c> to a string would be checking our own method against our own
    /// expectation, and would still pass on the day Serilog started rendering these differently.
    /// </remarks>
    [Fact]
    public void Serilog_renders_a_size_and_never_the_body()
    {
        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        logger.Information("A careless log line about {Payload}", new PayloadJson(Body));

        var rendered = Assert.Single(sink.Messages);

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains(Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), rendered, StringComparison.Ordinal);
    }

    /// <summary>The same, when somebody asks Serilog to destructure it.</summary>
    /// <remarks>
    /// <c>@</c> is what a developer reaches for when the default rendering looks unhelpful — which
    /// is exactly what this type's rendering is designed to look like. So it is the likeliest next
    /// thing anyone types, and it must not be the way round it.
    /// </remarks>
    [Fact]
    public void Serilog_does_not_reveal_the_body_when_asked_to_destructure_it()
    {
        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        logger.Information("Destructured: {@Payload}", new PayloadJson(Body));

        Assert.DoesNotContain(Secret, Assert.Single(sink.Messages), StringComparison.Ordinal);
    }

    /// <summary>And when the whole event is logged, the <em>body</em> still does not appear.</summary>
    /// <remarks>
    /// <para>
    /// The realistic accident: somebody logs the event to see what arrived. The body travels
    /// inside the event, and it stays redacted there.
    /// </para>
    /// <para>
    /// <strong>Read the name of this test literally.</strong> It says the body, not the event.
    /// Logging a whole event <em>does</em> reveal the mapped <c>Prompt</c> field, which holds the
    /// same words as a plain string — see
    /// <see cref="The_mapped_prompt_is_not_protected_and_that_is_a_known_gap"/>, which is the
    /// residual this one must not be mistaken for covering.
    /// </para>
    /// </remarks>
    [Fact]
    public void Logging_a_whole_event_does_not_reveal_its_body()
    {
        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var inboundEvent = Storage.TestEvents.Hook(Body);

        logger.Information("The whole event: {@Event}", inboundEvent);
        logger.Information("The event, plainly: {Event}", inboundEvent);

        foreach (var message in sink.Messages)
        {
            Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
        }

        // The control: the sink really received both lines, and they really are about this event.
        Assert.Equal(2, sink.Messages.Count);
        Assert.Contains(sink.Messages, message => message.Contains("UserPromptSubmit", StringComparison.Ordinal));
    }

    /// <summary>An interpolated string reveals nothing either, with no logger involved.</summary>
    [Fact]
    public void An_interpolated_string_shows_a_size_and_never_the_body()
    {
        var payload = new PayloadJson(Body);

        var written = $"about to write {payload}";

        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
        Assert.Contains("chars", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The mapped prompt is NOT protected. This records the gap; it does not bless it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same words the payload carries also live as a plain <see langword="string"/> on
    /// <c>UserPromptSubmit.Prompt</c>, and the Registry copies them onto <c>Exchange</c>. Those are
    /// public properties of records with a compiler-generated <c>ToString</c>, so a plain
    /// <c>{Event}</c> — no destructuring operator, nothing unusual — prints the operator's prompt.
    /// </para>
    /// <para>
    /// <strong>Why a test rather than a sentence.</strong> A residual written only in prose drifts
    /// out of date silently. <strong>When the wrapper for those two fields lands, this test fails</strong>
    /// — which is the intended signal, and the reader who sees it should delete this test and the
    /// paragraphs it points at, in <c>PayloadJson</c>, <c>InboundEvent</c> and
    /// <c>SqliteEventStore</c>, rather than adjust it to keep passing.
    /// </para>
    /// <para>
    /// Nothing in <c>src/</c> logs a whole event today. This is a gap in a guarantee, not a live
    /// disclosure.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_mapped_prompt_is_not_protected_and_that_is_a_known_gap()
    {
        const string Mapped = "THE-MAPPED-PROMPT-AS-THE-DOMAIN-HOLDS-IT";

        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var inboundEvent = Storage.TestEvents.Hook(Body) with { Prompt = Mapped };

        logger.Warning("Declined {Event}", inboundEvent);

        var rendered = Assert.Single(sink.Messages);

        // The gap, stated as the assertion so it cannot be read as an aspiration.
        Assert.Contains(Mapped, rendered, StringComparison.Ordinal);

        // And the boundary of what PayloadJson does cover: on the very same line, the raw body is
        // still redacted. This is the difference between the two fields, in one rendering.
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_payload_renders_as_none() =>
        Assert.Equal("<payload: none>", default(PayloadJson).ToString());

    // ---- It still behaves like a value ----------------------------------------------------------

    [Fact]
    public void Two_payloads_with_the_same_body_are_equal()
    {
        Assert.Equal(new PayloadJson(Body), new PayloadJson(Body));
        Assert.NotEqual(new PayloadJson(Body), new PayloadJson("{}"));
        Assert.Equal(new PayloadJson(Body).GetHashCode(), new PayloadJson(Body).GetHashCode());
    }

}
