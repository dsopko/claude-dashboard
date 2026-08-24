using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class GroupKeysTests
{
    private static readonly SessionId Id = new("s-1");

    [Fact]
    public void A_workspace_key_groups_sessions_in_the_same_directory()
    {
        Assert.Equal(
            GroupKeys.ForSession(@"C:\projects\dashboard", Id),
            GroupKeys.ForSession(@"C:\projects\dashboard", new SessionId("s-2")));
    }

    [Fact]
    public void Different_directories_get_different_keys()
    {
        Assert.NotEqual(
            GroupKeys.ForWorkspace(@"C:\projects\dashboard"),
            GroupKeys.ForWorkspace(@"C:\projects\elsewhere"));
    }

    /// <summary>
    /// Not hypothetical: this repository's own build emitted both <c>C:\Projects\Claude\…</c>
    /// and <c>C:\projects\Claude\…</c> for one directory within a single run. Ordinal keys
    /// would have made that two groups and split the workspace on screen.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Projects\Claude\dashboard", @"C:\projects\Claude\dashboard")]
    [InlineData(@"C:\PROJECTS\DASHBOARD", @"c:\projects\dashboard")]
    [InlineData(@"D:\Work\App", @"D:\work\app")]
    public void Casing_does_not_split_a_workspace(string one, string other)
    {
        Assert.Equal(GroupKeys.ForWorkspace(one), GroupKeys.ForWorkspace(other));
    }

    [Theory]
    [InlineData(@"C:\projects\dashboard\", @"C:\projects\dashboard")]
    [InlineData(@"C:\projects\dashboard\\", @"C:\projects\dashboard")]
    [InlineData("C:/projects/dashboard/", "C:/projects/dashboard")]
    public void A_trailing_separator_does_not_split_a_workspace(string one, string other)
    {
        Assert.Equal(GroupKeys.ForWorkspace(one), GroupKeys.ForWorkspace(other));
    }

    /// <summary>
    /// Trimming <c>/</c> as well as <c>\</c> concedes that both spellings reach this rule, so
    /// leaving interior separators distinct would be half a rule — <c>C:\Projects\x</c> and
    /// <c>C:/Projects/x</c> name one directory, and two keys for it split a workspace into two
    /// groups that look entirely legitimate on screen.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Projects\x", "C:/Projects/x")]
    [InlineData(@"C:\Projects\x", "c:/projects/x/")]
    [InlineData(@"C:\a\b\c", "C:/a/b/c")]
    public void Separator_spelling_does_not_split_a_workspace(string one, string other)
    {
        Assert.Equal(GroupKeys.ForWorkspace(one), GroupKeys.ForWorkspace(other));
    }

    [Fact]
    public void Casing_and_a_trailing_separator_together_do_not_split_a_workspace()
    {
        Assert.Equal(
            GroupKeys.ForWorkspace(@"C:\Projects\Dashboard\"),
            GroupKeys.ForWorkspace(@"c:\projects\dashboard"));
    }

    /// <summary>A path that is nothing but separators is the root, not nothing.</summary>
    [Fact]
    public void A_root_directory_is_still_a_workspace()
    {
        var root = GroupKeys.ForWorkspace(@"\");

        Assert.False(root.IsEmpty);
        Assert.Equal(GroupKeyKind.Workspace, GroupKeys.KindOf(root));
    }

    [Fact]
    public void Normalization_does_not_merge_genuinely_different_directories()
    {
        Assert.NotEqual(
            GroupKeys.ForWorkspace(@"C:\projects\dashboard"),
            GroupKeys.ForWorkspace(@"C:\projects\dashboard2"));
        Assert.NotEqual(
            GroupKeys.ForWorkspace(@"C:\projects\a\b"),
            GroupKeys.ForWorkspace(@"C:\projects\a"));
    }

    // ---- No workspace ---------------------------------------------------------------------

    /// <summary>
    /// T1.1 made <c>Cwd</c> required-but-possibly-empty so ingress never drops a real event for
    /// want of a directory, which guarantees this case occurs.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_session_with_no_workspace_is_keyed_on_itself(string cwd)
    {
        var key = GroupKeys.ForSession(cwd, Id);

        Assert.Equal(GroupKeys.ForUngrouped(Id), key);
        Assert.Equal(GroupKeyKind.Session, GroupKeys.KindOf(key));
    }

    /// <summary>
    /// Pooling every directory-less session into one shared group would assert they belong
    /// together, which is the one thing grouping must never invent (TS §IV.3).
    /// </summary>
    [Fact]
    public void Sessions_with_no_workspace_do_not_group_with_each_other()
    {
        Assert.NotEqual(
            GroupKeys.ForSession(string.Empty, new SessionId("s-1")),
            GroupKeys.ForSession(string.Empty, new SessionId("s-2")));
    }

    // ---- Kinds ----------------------------------------------------------------------------

    /// <summary>
    /// A key is an identity, not a display string. Without a kind, an ungrouped session's key
    /// is an indistinguishable string and a caller would render a session id as a directory.
    /// </summary>
    [Fact]
    public void A_workspace_key_and_a_session_key_are_distinguishable()
    {
        var workspace = GroupKeys.ForWorkspace(@"C:\projects\dashboard");
        var ungrouped = GroupKeys.ForUngrouped(Id);

        Assert.Equal(GroupKeyKind.Workspace, GroupKeys.KindOf(workspace));
        Assert.Equal(GroupKeyKind.Session, GroupKeys.KindOf(ungrouped));
        Assert.NotEqual(workspace, ungrouped);
    }

    /// <summary>
    /// A session id that happens to look like a path must not be mistaken for a workspace, and
    /// a directory named after a session must not be mistaken for an ungrouped session.
    /// </summary>
    [Fact]
    public void The_two_kinds_cannot_collide()
    {
        Assert.NotEqual(
            GroupKeys.ForUngrouped(new SessionId(@"C:\projects\dashboard")),
            GroupKeys.ForWorkspace(@"C:\projects\dashboard"));
    }

    [Fact]
    public void An_unrecognized_key_reports_an_unknown_kind()
    {
        Assert.Equal(GroupKeyKind.Unknown, GroupKeys.KindOf(new GroupKey("hand-written")));
        Assert.Equal(GroupKeyKind.Unknown, GroupKeys.KindOf(default));
    }

    // ---- Validation -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_workspace_key_needs_a_directory(string? cwd)
    {
        Assert.Equal("cwd", Assert.Throws<ArgumentException>(() => GroupKeys.ForWorkspace(cwd!)).ParamName);
    }

    [Fact]
    public void An_ungrouped_key_needs_a_session()
    {
        Assert.Equal("session", Assert.Throws<ArgumentException>(() => GroupKeys.ForUngrouped(default)).ParamName);
    }

    [Fact]
    public void Keys_are_never_empty()
    {
        Assert.False(GroupKeys.ForWorkspace(@"C:\x").IsEmpty);
        Assert.False(GroupKeys.ForUngrouped(Id).IsEmpty);
        Assert.False(GroupKeys.ForSession(string.Empty, Id).IsEmpty);
    }
}
