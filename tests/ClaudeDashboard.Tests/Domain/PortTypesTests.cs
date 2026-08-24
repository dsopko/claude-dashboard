using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Domain;

public sealed class WindowHandleTests
{
    [Fact]
    public void Wraps_a_handle_value()
    {
        var handle = new WindowHandle(0x1234);

        Assert.Equal(0x1234, handle.Value);
        Assert.False(handle.IsNone);
    }

    [Fact]
    public void Default_and_None_name_no_window()
    {
        Assert.True(default(WindowHandle).IsNone);
        Assert.True(WindowHandle.None.IsNone);
        Assert.Equal(default, WindowHandle.None);
        Assert.True(new WindowHandle(0).IsNone);
    }

    [Fact]
    public void Has_value_equality()
    {
        Assert.Equal(new WindowHandle(7), new WindowHandle(7));
        Assert.True(new WindowHandle(7) == new WindowHandle(7));
        Assert.NotEqual(new WindowHandle(7), new WindowHandle(8));
        Assert.Equal(new WindowHandle(7).GetHashCode(), new WindowHandle(7).GetHashCode());
    }
}

public sealed class DesktopIdTests
{
    private static readonly Guid Value = new("6a3f2b1c-0000-4000-8000-abcdefabcdef");

    [Fact]
    public void Wraps_a_desktop_guid()
    {
        Assert.Equal(Value, new DesktopId(Value).Value);
        Assert.False(new DesktopId(Value).IsNone);
    }

    [Fact]
    public void Default_and_None_name_no_desktop()
    {
        Assert.True(default(DesktopId).IsNone);
        Assert.True(DesktopId.None.IsNone);
        Assert.True(new DesktopId(Guid.Empty).IsNone);
    }

    [Fact]
    public void Has_value_equality()
    {
        Assert.Equal(new DesktopId(Value), new DesktopId(Value));
        Assert.NotEqual(new DesktopId(Value), new DesktopId(Guid.NewGuid()));
    }
}

public sealed class SoundIdTests
{
    [Fact]
    public void Names_the_sound_language_of_TS_IV_5()
    {
        Assert.Equal("finished", SoundId.Finished.Name);
        Assert.Equal("permission", SoundId.Permission.Name);
        Assert.Equal("question", SoundId.Question.Name);
        Assert.Equal("error", SoundId.Error.Name);
    }

    [Fact]
    public void Well_known_ids_are_distinct()
    {
        SoundId[] all = [SoundId.Finished, SoundId.Permission, SoundId.Question, SoundId.Error];

        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_id_that_names_no_sound(string? name)
    {
        var thrown = Assert.Throws<ArgumentException>(() => new SoundId(name!));

        Assert.Equal("name", thrown.ParamName);
    }

    [Fact]
    public void Default_names_no_sound_and_does_not_throw()
    {
        Assert.True(default(SoundId).IsEmpty);
        Assert.Equal(string.Empty, default(SoundId).Name);
        _ = default(SoundId).GetHashCode();
    }

    [Fact]
    public void Has_value_equality()
    {
        Assert.Equal(SoundId.Finished, new SoundId("finished"));
        Assert.True(SoundId.Finished == new SoundId("finished"));
        Assert.NotEqual(SoundId.Finished, SoundId.Error);
    }

    /// <summary>The type stays open so a later phase can add a sound without changing Core.</summary>
    [Fact]
    public void Accepts_an_id_outside_the_well_known_set()
    {
        Assert.Equal("subagent_finished", new SoundId("subagent_finished").Name);
    }
}

public sealed class TabRefTests
{
    private static readonly WindowHandle Window = new(0x1234);

    [Fact]
    public void References_a_tab_within_a_window()
    {
        var tab = new TabRef(Window, 3);

        Assert.Equal(Window, tab.Window);
        Assert.Equal(3, tab.TabIndex);
        Assert.True(tab.IsTabResolved);
    }

    /// <summary>
    /// TS §IV.7's degradation ladder made expressible: the window resolved, the tab did not,
    /// and callers navigate at window granularity.
    /// </summary>
    [Fact]
    public void References_a_window_when_the_tab_could_not_be_resolved()
    {
        var windowOnly = new TabRef(Window);

        Assert.Equal(Window, windowOnly.Window);
        Assert.Null(windowOnly.TabIndex);
        Assert.False(windowOnly.IsTabResolved);
    }

    [Fact]
    public void Requires_a_window()
    {
        Assert.Equal("window", Assert.Throws<ArgumentException>(() => new TabRef(WindowHandle.None)).ParamName);
        Assert.Equal("window", Assert.Throws<ArgumentException>(() => new TabRef(WindowHandle.None, 0)).ParamName);
    }

    [Fact]
    public void Rejects_a_negative_tab_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TabRef(Window, -1));
    }

    [Fact]
    public void Accepts_the_first_tab()
    {
        Assert.Equal(0, new TabRef(Window, 0).TabIndex);
    }

    [Fact]
    public void Has_value_equality()
    {
        Assert.Equal(new TabRef(Window, 3), new TabRef(Window, 3));
        Assert.Equal(new TabRef(Window), new TabRef(Window));
        Assert.Equal(new TabRef(Window, 3).GetHashCode(), new TabRef(Window, 3).GetHashCode());
    }

    /// <summary>A window-level reference is a weaker claim than a tab-level one, not the same claim.</summary>
    [Fact]
    public void Window_level_and_tab_level_references_are_different()
    {
        Assert.NotEqual(new TabRef(Window), new TabRef(Window, 0));
        Assert.NotEqual(new TabRef(Window, 3), new TabRef(Window, 4));
        Assert.NotEqual(new TabRef(Window, 3), new TabRef(new WindowHandle(0x9999), 3));
    }
}
