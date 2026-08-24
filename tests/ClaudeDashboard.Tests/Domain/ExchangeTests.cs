using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class ExchangeTests
{
    private static readonly DateTimeOffset Started = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructs_an_unanswered_exchange()
    {
        var exchange = new Exchange { Prompt = "run the tests", StartedAt = Started };

        Assert.Equal("run the tests", exchange.Prompt);
        Assert.Null(exchange.Answer);
        Assert.Null(exchange.AnsweredAt);
        Assert.Null(exchange.PromptId);
        Assert.False(exchange.IsAnswered);
    }

    [Fact]
    public void Constructs_an_answered_exchange()
    {
        var answeredAt = Started.AddMinutes(2);
        var exchange = new Exchange
        {
            Prompt = "run the tests",
            Answer = "29 passed",
            PromptId = "p-1",
            StartedAt = Started,
            AnsweredAt = answeredAt,
        };

        Assert.Equal("29 passed", exchange.Answer);
        Assert.Equal("p-1", exchange.PromptId);
        Assert.Equal(answeredAt, exchange.AnsweredAt);
        Assert.True(exchange.IsAnswered);
    }

    [Fact]
    public void Rejects_a_null_prompt()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Exchange { Prompt = null!, StartedAt = Started });
    }

    /// <summary>
    /// An empty prompt is legal. Ingress is a pure observer (Impl §3.3) and must never drop or
    /// reject a real event just because its text is empty.
    /// </summary>
    [Fact]
    public void Accepts_an_empty_prompt()
    {
        var exchange = new Exchange { Prompt = string.Empty, StartedAt = Started };

        Assert.Equal(string.Empty, exchange.Prompt);
    }

    [Fact]
    public void Has_value_equality()
    {
        var a = new Exchange { Prompt = "p", Answer = "a", PromptId = "id", StartedAt = Started };
        var b = new Exchange { Prompt = "p", Answer = "a", PromptId = "id", StartedAt = Started };

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Distinguishes_exchanges_that_differ_in_any_member()
    {
        var baseline = new Exchange { Prompt = "p", Answer = "a", StartedAt = Started };

        Assert.NotEqual(baseline, baseline with { Prompt = "other" });
        Assert.NotEqual(baseline, baseline with { Answer = "other" });
        Assert.NotEqual(baseline, baseline with { PromptId = "id" });
        Assert.NotEqual(baseline, baseline with { StartedAt = Started.AddSeconds(1) });
        Assert.NotEqual(baseline, baseline with { AnsweredAt = Started });
    }

    /// <summary>An unanswered exchange and one answered with null text are not the same thing.</summary>
    [Fact]
    public void Distinguishes_no_answer_from_an_empty_answer()
    {
        var unanswered = new Exchange { Prompt = "p", StartedAt = Started };
        var emptyAnswer = unanswered with { Answer = string.Empty };

        Assert.NotEqual(unanswered, emptyAnswer);
    }

    /// <summary>
    /// Prompt and answer text are data, never instruction (TS §II.5): stored byte-for-byte,
    /// never trimmed, escaped, or interpreted.
    /// </summary>
    [Theory]
    [InlineData("  leading and trailing  ")]
    [InlineData("line one\nline two")]
    [InlineData("<script>alert('x')</script>")]
    [InlineData("$(rm -rf /)")]
    [InlineData("{\"looks\": \"like json\"}")]
    public void Stores_text_verbatim(string text)
    {
        var exchange = new Exchange { Prompt = text, Answer = text, StartedAt = Started };

        Assert.Equal(text, exchange.Prompt);
        Assert.Equal(text, exchange.Answer);
    }

    /// <summary>
    /// T1.2 answers an exchange by producing a new one; the original must be untouched.
    /// </summary>
    [Fact]
    public void With_expression_leaves_the_original_unchanged()
    {
        var unanswered = new Exchange { Prompt = "p", StartedAt = Started };

        var answered = unanswered with { Answer = "done", AnsweredAt = Started.AddMinutes(1) };

        Assert.False(unanswered.IsAnswered);
        Assert.Null(unanswered.Answer);
        Assert.True(answered.IsAnswered);
        Assert.Equal("p", answered.Prompt);
    }
}
