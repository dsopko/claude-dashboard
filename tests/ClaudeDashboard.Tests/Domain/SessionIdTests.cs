using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class SessionIdTests
{
    [Fact]
    public void Wraps_the_session_id_string()
    {
        var id = new SessionId("abc-123");

        Assert.Equal("abc-123", id.Value);
        Assert.Equal("abc-123", id.ToString());
        Assert.False(id.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Rejects_an_id_that_names_no_session(string? value)
    {
        var thrown = Assert.Throws<ArgumentException>(() => new SessionId(value!));

        Assert.Equal("value", thrown.ParamName);
    }

    [Fact]
    public void Has_value_equality()
    {
        var a = new SessionId("abc-123");
        var b = new SessionId("abc-123");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Distinguishes_different_ids()
    {
        Assert.NotEqual(new SessionId("abc-123"), new SessionId("abc-124"));
    }

    /// <summary>Session ids are opaque identifiers, so comparison must not fold case.</summary>
    [Fact]
    public void Compares_ordinally()
    {
        Assert.NotEqual(new SessionId("ABC"), new SessionId("abc"));
    }

    /// <summary>
    /// A struct always has a <c>default</c>, so the type has to behave rather than throw on it.
    /// </summary>
    [Fact]
    public void Default_names_no_session_and_does_not_throw()
    {
        var uninitialized = default(SessionId);

        Assert.True(uninitialized.IsEmpty);
        Assert.Equal(string.Empty, uninitialized.Value);
        Assert.Equal(string.Empty, uninitialized.ToString());
        Assert.Equal(default, uninitialized);
        _ = uninitialized.GetHashCode();
    }

    /// <summary>Usable as the Registry key (Impl §2.1) — which is the whole point of the type.</summary>
    [Fact]
    public void Works_as_a_dictionary_key()
    {
        var map = new Dictionary<SessionId, string>
        {
            [new SessionId("abc-123")] = "first",
        };

        Assert.Equal("first", map[new SessionId("abc-123")]);
        Assert.False(map.ContainsKey(new SessionId("abc-124")));
    }
}
