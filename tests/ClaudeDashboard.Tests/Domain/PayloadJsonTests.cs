using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// <see cref="PayloadJson"/>: the raw hook body, as a value. Since T1.76 (issue #118; the operator's ruling of
/// 2026-10-05) it prints its text: a log line that names it shows the body, plainly and destructured.
/// </summary>
public sealed class PayloadJsonTests
{
    private const string Prompt = "a prompt the log may show";

    private static readonly string Body = $$"""{"prompt":"{{Prompt}}"}""";

    // ---- The value ------------------------------------------------------------------------------

    [Fact]
    public void The_body_is_readable()
    {
        Assert.Equal(Body, new PayloadJson(Body).Reveal());
        Assert.Equal(Body, new PayloadJson(Body).Text);
        Assert.Equal(Body.Length, new PayloadJson(Body).Length);
    }

    [Fact]
    public void A_default_payload_carries_nothing_and_says_so()
    {
        var empty = default(PayloadJson);

        Assert.True(empty.IsEmpty);
        Assert.Equal(string.Empty, empty.Reveal());
        Assert.Equal(string.Empty, empty.ToString());
        Assert.Equal(0, empty.Length);
    }

    [Fact]
    public void It_refuses_a_null_body() =>
        Assert.Throws<ArgumentNullException>(() => new PayloadJson(null!));

    [Fact]
    public void Two_payloads_with_the_same_body_are_equal()
    {
        Assert.Equal(new PayloadJson(Body), new PayloadJson(Body));
        Assert.NotEqual(new PayloadJson(Body), new PayloadJson("{}"));
        Assert.Equal(new PayloadJson(Body).GetHashCode(), new PayloadJson(Body).GetHashCode());
    }

    // ---- What a log line shows (T1.76) ----------------------------------------------------------

    /// <summary>
    /// <strong>A log line that names the payload shows the body</strong>: plainly (<c>{Payload}</c>, which calls
    /// <c>ToString</c>), destructured (<c>{@Payload}</c>, which reads its properties), and in an interpolated string.
    /// </summary>
    [Fact]
    public void A_log_line_shows_the_body()
    {
        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var payload = new PayloadJson(Body);

        logger.Information("Plainly: {Payload}", payload);
        logger.Information("Destructured: {@Payload}", payload);

        Assert.Equal(2, sink.Messages.Count);
        Assert.All(sink.Messages, message => Assert.Contains(Prompt, message, StringComparison.Ordinal));
        Assert.Equal(Body, payload.ToString());
        Assert.Contains(Prompt, $"about to write {payload}", StringComparison.Ordinal);
    }
}
