using System.Text.Json;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// A <c>Stop</c>'s <c>session_crons</c>, read for the prompts a tick is recognised by (T1.44).
/// </summary>
/// <remarks>The entry shape is the one measured on the operator's archive: <c>{ id, schedule, prompt, recurring }</c>.</remarks>
public sealed class SessionCronReaderTests
{
    /// <summary>Each entry's prompt, compared exactly.</summary>
    [Fact]
    public void Each_crons_prompt_is_read_exactly()
    {
        var prompts = SessionCronReader.Read(Parse("""
            [
              {"id":"c1","schedule":"*/30 * * * *","prompt":"check the coder","recurring":true},
              {"id":"c2","schedule":"0 9 * * *","prompt":"morning summary","recurring":true}
            ]
            """));

        Assert.Equal(2, prompts.Count);
        Assert.True(prompts.Contains("check the coder"));
        Assert.True(prompts.Contains("morning summary"));
        Assert.False(prompts.Contains("check the coder "));
        Assert.False(prompts.Contains("Check the coder"));
    }

    /// <summary><strong>Degrade, never crash:</strong> any malformed list reads as none, so no prompt is a tick.</summary>
    [Theory]
    [InlineData("\"not a list\"")]
    [InlineData("{\"prompt\":\"x\"}")]
    [InlineData("null")]
    [InlineData("[1, \"two\", null, []]")]
    [InlineData("[{\"id\":\"c1\"}]")]
    [InlineData("[{\"prompt\":7}]")]
    [InlineData("[{\"prompt\":\"\"}]")]
    public void A_malformed_list_reads_as_none(string list) =>
        Assert.Equal(0, SessionCronReader.Read(Parse(list)).Count);

    /// <summary>An absent field reads as none.</summary>
    [Fact]
    public void An_absent_field_reads_as_none() => Assert.Equal(0, SessionCronReader.Read(null).Count);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
