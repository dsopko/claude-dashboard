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
    /// same words as a plain string. <c>UnprotectedTextInventory</c> is where the extent of that
    /// is asserted, and <c>One_line_shows_the_body_redacted_and_the_mapped_prompt_not</c> shows
    /// both sides on one rendered line. This test must not be mistaken for covering either.
    /// </para>
    /// <para>
    /// Named in plain <c>c</c> tags rather than <c>see cref</c> on purpose:
    /// <c>GenerateDocumentationFile</c> is off, so no cref in this repository is validated, and
    /// this very paragraph carried a dangling one to a test that had been renamed — found in the
    /// same review that found the dangling <c>Value</c> in <see cref="PayloadJson"/>. A reference
    /// that looks checked and is not is worse than prose.
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
    /// One rendered line showing both sides of the boundary: the body redacted, the mapped prompt
    /// not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The extent of the gap is asserted by <c>UnprotectedTextInventory</c>, not here.</strong>
    /// This test exists for the contrast, which no inventory can show: the two fields carry the same
    /// words, travel on the same object, and are rendered by one log statement — and one comes out
    /// redacted while the other comes out whole. That is the clearest statement of what
    /// <see cref="PayloadJson"/> buys and where it stops.
    /// </para>
    /// <para>
    /// If the second assertion here fails, read its message before changing anything. It is the
    /// expected consequence of issue #11 landing, and the repair is to delete the assertion — never
    /// to point it at some other field that still leaks.
    /// </para>
    /// </remarks>
    [Fact]
    public void One_line_shows_the_body_redacted_and_the_mapped_prompt_not()
    {
        const string Mapped = "THE-MAPPED-PROMPT-AS-THE-DOMAIN-HOLDS-IT";

        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var inboundEvent = Storage.TestEvents.Hook(Body) with { Prompt = Mapped };

        logger.Warning("Declined {Event}", inboundEvent);

        var rendered = Assert.Single(sink.Messages);

        // What PayloadJson buys, and what must stay true for ever.
        Assert.True(
            !rendered.Contains(Secret, StringComparison.Ordinal),
            "THE RAW HOOK BODY REACHED A LOG LINE. This is the guarantee PayloadJson exists for and it " +
            $"has been broken. Rendered line: {rendered}");

        // Where it stops. Assert.Contains has no message overload, and its failure output truncates
        // the rendered line and says only "sub-string not found" — which reads as a stale expectation
        // and invites the perverse repair of updating it. So the instruction travels with the failure.
        Assert.True(
            rendered.Contains(Mapped, StringComparison.Ordinal),
            "THE MAPPED PROMPT NO LONGER APPEARS IN A RENDERED EVENT. If you have just wrapped " +
            "UserPromptSubmit.Prompt (issue #11) THIS IS THE EXPECTED FAILURE: delete this assertion and " +
            "this test's second half, then delete the entry from UnprotectedTextInventory and the residual " +
            "paragraphs in PayloadJson, InboundEvent.Payload, SqliteEventStore and EventArchive. " +
            "DO NOT re-point this assertion at another field that still leaks in order to restore green. " +
            $"Rendered line: {rendered}");
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

    /// <summary>
    /// The two log-formatting routes differ, and that is why the inventory covers plain classes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SessionViewModel</c> is a plain class, so its <c>ToString</c> prints a type name and
    /// leaks nothing — and an inventory scoped to records was justified with exactly that
    /// sentence. It is true and it is the wrong half. <c>{@Row}</c> reflects over public properties
    /// of <em>any</em> type, and this is the type UI code is most likely to log: <c>{@Row}</c>
    /// while working out why a row rendered oddly is a more natural line than logging a domain
    /// object.
    /// </para>
    /// <para>
    /// <strong>This test is the argument against narrowing the scan again.</strong> The claim that
    /// record-ness decides anything about <c>{@}</c> is falsifiable, so it is measured here rather
    /// than asserted in a remark. <c>UnprotectedTextInventory</c> asserts the extent of the gap;
    /// this asserts why its predicate is the shape it is.
    /// </para>
    /// </remarks>
    [Fact]
    public void Destructuring_reaches_a_plain_class_that_plain_rendering_does_not()
    {
        const string Prompt = "THE-PROMPT-A-ROW-IS-BOUND-TO";

        var session = new ClaudeDashboard.Core.Session
        {
            Id = new ClaudeDashboard.Core.SessionId("s1"),
            State = ClaudeDashboard.Core.SessionState.Unread,
            Latest = new ClaudeDashboard.Core.Exchange
            {
                Prompt = Prompt,
                StartedAt = DateTimeOffset.UnixEpoch,
            },
            Cwd = @"C:\work",
            WorkspaceGroup = new ClaudeDashboard.Core.GroupKey("work"),
            EnteredAt = DateTimeOffset.UnixEpoch,
            LastActivity = DateTimeOffset.UnixEpoch,
        };

        var row = new ClaudeDashboard.App.Ui.SessionViewModel(session);

        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        logger.Debug("Row {Row}", row);
        logger.Debug("Row {@Row}", row);

        var plain = sink.Messages[0];
        var destructured = sink.Messages[1];

        Assert.True(
            !plain.Contains(Prompt, StringComparison.Ordinal),
            $"a plain class rendered with {{Row}} revealed the prompt; the two routes were supposed to differ. " +
            $"Rendered: {plain}");

        Assert.True(
            destructured.Contains(Prompt, StringComparison.Ordinal),
            "DESTRUCTURING A ROW NO LONGER REVEALS THE PROMPT. If you have just wrapped " +
            "SessionViewModel.Prompt (issue #11) THIS IS THE EXPECTED FAILURE: delete this assertion, and " +
            "delete the entry from UnprotectedTextInventory. DO NOT re-point it at another leaking property " +
            "to restore green. If instead the two routes have stopped differing, the inventory's predicate " +
            $"needs revisiting rather than this test. Rendered: {destructured}");
    }
}
