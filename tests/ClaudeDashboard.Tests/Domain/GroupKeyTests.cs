using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class GroupKeyTests
{
    [Fact]
    public void Wraps_the_key_string()
    {
        var key = new GroupKey(@"C:\projects\dashboard");

        Assert.Equal(@"C:\projects\dashboard", key.Value);
        Assert.Equal(@"C:\projects\dashboard", key.ToString());
        Assert.False(key.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_a_key_that_names_no_group(string? value)
    {
        var thrown = Assert.Throws<ArgumentException>(() => new GroupKey(value!));

        Assert.Equal("value", thrown.ParamName);
    }

    [Fact]
    public void Has_value_equality()
    {
        var a = new GroupKey(@"C:\projects\dashboard");
        var b = new GroupKey(@"C:\projects\dashboard");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Default_names_no_group_and_does_not_throw()
    {
        var uninitialized = default(GroupKey);

        Assert.True(uninitialized.IsEmpty);
        Assert.Equal(string.Empty, uninitialized.Value);
        _ = uninitialized.GetHashCode();
    }

    /// <summary>
    /// The key is an opaque string, not a path: Core is portable and must not reason about
    /// Windows path semantics. Any normalization is the group resolver's (T1.4).
    /// </summary>
    [Fact]
    public void Does_not_normalize_the_key_it_is_given()
    {
        Assert.NotEqual(new GroupKey(@"C:\Projects\Dashboard"), new GroupKey(@"C:\projects\dashboard"));
        Assert.NotEqual(new GroupKey(@"C:\projects\dashboard\"), new GroupKey(@"C:\projects\dashboard"));
    }

    /// <summary>Phase 4 keys on a virtual-desktop id rather than a path (TS §IV.3).</summary>
    [Fact]
    public void Accepts_a_non_path_key()
    {
        var desktopId = new GroupKey("{6a3f2b1c-0000-4000-8000-abcdefabcdef}");

        Assert.Equal("{6a3f2b1c-0000-4000-8000-abcdefabcdef}", desktopId.Value);
    }
}
