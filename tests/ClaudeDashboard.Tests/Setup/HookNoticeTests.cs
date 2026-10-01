using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The notice the operator sees when the dashboard is not connected to Claude Code (the operator's
/// rulings of 2026-10-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What a notice says is the remedy, so the words are asserted.</strong> A notice that
/// names the wrong command, or no command, leaves the operator with an empty window and nothing
/// to do about it.
/// </para>
/// <para>
/// <strong>And when it goes away is asserted too.</strong> A notice that says "receives nothing"
/// above rows that are updating is contradicted by the screen it is on; a notice about an old hook
/// that vanished at the first event would vanish at once, because events arrive through that hook.
/// </para>
/// </remarks>
public sealed class HookNoticeTests
{
    private const string Folder = @"C:\Users\Some One\AppData\Local\ClaudeDashboard\plugin";

    /// <summary>Every way of showing a notice, by name, with whether an event clears it.</summary>
    public static TheoryData<string, HookNoticeKind, bool> Shows => new()
    {
        { nameof(HookNotice.ShowPluginDisabled), HookNoticeKind.PluginDisabled, true },
        { nameof(HookNotice.ShowPluginRemoved), HookNoticeKind.PluginRemoved, true },
        { nameof(HookNotice.ShowClaudeCodeNotInstalled), HookNoticeKind.ClaudeCodeNotInstalled, true },
        { nameof(HookNotice.ShowClaudeNotFound), HookNoticeKind.ClaudeNotFound, true },
        { nameof(HookNotice.ShowClaudeRefused), HookNoticeKind.ClaudeRefused, true },
        { nameof(HookNotice.ShowSettingsUnreadable), HookNoticeKind.SettingsUnreadable, true },
        { nameof(HookNotice.ShowOptOutUnknown), HookNoticeKind.OptOutUnknown, true },
        { nameof(HookNotice.ShowOtherDataFolder), HookNoticeKind.OtherDataFolder, true },
        { nameof(HookNotice.ShowOldHooks), HookNoticeKind.OldHooks, false },
        { nameof(HookNotice.ShowJustRegistered), HookNoticeKind.JustRegistered, true },
    };

    private static HookNotice Shown(string how)
    {
        var notice = new HookNotice();

        switch (how)
        {
            case nameof(HookNotice.ShowPluginDisabled): notice.ShowPluginDisabled(); break;
            case nameof(HookNotice.ShowPluginRemoved): notice.ShowPluginRemoved(); break;
            case nameof(HookNotice.ShowClaudeCodeNotInstalled): notice.ShowClaudeCodeNotInstalled(); break;
            case nameof(HookNotice.ShowClaudeNotFound): notice.ShowClaudeNotFound(Folder); break;
            case nameof(HookNotice.ShowClaudeRefused): notice.ShowClaudeRefused(Folder, "exit 1: refused"); break;
            case nameof(HookNotice.ShowSettingsUnreadable): notice.ShowSettingsUnreadable("unexpected end of data"); break;
            case nameof(HookNotice.ShowOptOutUnknown): notice.ShowOptOutUnknown(); break;
            case nameof(HookNotice.ShowOtherDataFolder): notice.ShowOtherDataFolder(@"C:\Elsewhere\plugin"); break;
            case nameof(HookNotice.ShowOldHooks): notice.ShowOldHooks(pluginEnabled: false); break;
            case nameof(HookNotice.ShowJustRegistered): notice.ShowJustRegistered(); break;
            default: throw new ArgumentOutOfRangeException(nameof(how), how, "Not a way of showing a notice.");
        }

        return notice;
    }

    [Fact]
    public void A_new_notice_shows_nothing()
    {
        var notice = new HookNotice();

        Assert.False(notice.IsShown);
        Assert.Null(notice.Text);
        Assert.Null(notice.TrayText);
        Assert.Equal(HookNoticeKind.None, notice.Kind);
    }

    [Theory]
    [MemberData(nameof(Shows))]
    public void Each_finding_is_shown_with_a_sentence(string how, HookNoticeKind kind, bool clearsOnEvent)
    {
        var notice = Shown(how);

        Assert.True(notice.IsShown);
        Assert.Equal(kind, notice.Kind);
        Assert.Equal(clearsOnEvent, notice.ClearsOnEvent);
        Assert.False(string.IsNullOrWhiteSpace(notice.Text));
        Assert.EndsWith(".", notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The dashboard edits no settings file, and no notice says or implies that it
    /// does.</strong> The one notice that is about a settings file tells the operator to ask
    /// Claude, or to use Claude Code's own command.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shows))]
    public void No_notice_names_a_settings_file_to_edit(string how, HookNoticeKind kind, bool clearsOnEvent)
    {
        _ = kind;
        _ = clearsOnEvent;

        Assert.DoesNotContain("settings.json", Shown(how).Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <strong>An arriving event clears a notice that said nothing was reporting, and leaves the
    /// one that is about an old hook.</strong>
    /// </summary>
    [Theory]
    [MemberData(nameof(Shows))]
    public void An_event_clears_exactly_the_notices_it_disproves(string how, HookNoticeKind kind, bool clearsOnEvent)
    {
        var notice = Shown(how);
        var raised = new List<string?>();
        notice.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        notice.EventArrived();

        Assert.Equal(!clearsOnEvent, notice.IsShown);

        if (clearsOnEvent)
        {
            Assert.Null(notice.Text);
            Assert.Null(notice.TrayText);
            Assert.Equal(HookNoticeKind.None, notice.Kind);
            Assert.Contains(nameof(HookNotice.Text), raised);
            Assert.Contains(nameof(HookNotice.TrayText), raised);
            Assert.Contains(nameof(HookNotice.IsShown), raised);
        }
        else
        {
            Assert.Equal(kind, notice.Kind);
            Assert.Empty(raised);
        }
    }

    [Fact]
    public void An_event_with_nothing_shown_changes_nothing()
    {
        var notice = new HookNotice();
        var raised = 0;
        notice.PropertyChanged += (_, _) => raised++;

        notice.EventArrived();
        notice.EventArrived();

        Assert.Equal(0, raised);
        Assert.False(notice.IsShown);
    }

    [Fact]
    public void Showing_raises_the_changes_a_binding_follows()
    {
        var notice = new HookNotice();
        var raised = new List<string?>();
        notice.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        notice.ShowPluginDisabled();

        Assert.Contains(nameof(HookNotice.Text), raised);
        Assert.Contains(nameof(HookNotice.TrayText), raised);
        Assert.Contains(nameof(HookNotice.IsShown), raised);
    }

    // ---- What each one tells the operator to do -------------------------------------------------

    [Fact]
    public void A_turned_off_plugin_names_the_command_that_turns_it_on()
    {
        var notice = Shown(nameof(HookNotice.ShowPluginDisabled));

        Assert.Contains("claude plugin enable claude-dashboard@claude-dashboard", notice.Text, StringComparison.Ordinal);
        Assert.Contains("restart each Claude Code session", notice.Text, StringComparison.Ordinal);
        Assert.Equal(HookNotice.PluginDisabledShort, notice.TrayText);
    }

    /// <summary>
    /// <strong>With no <c>claude</c> program there is no other door, so the notice is the
    /// remedy</strong>: both commands, with the folder in quotes because a user name can hold a
    /// space.
    /// </summary>
    [Theory]
    [InlineData(nameof(HookNotice.ShowClaudeNotFound))]
    [InlineData(nameof(HookNotice.ShowClaudeRefused))]
    public void A_plugin_that_could_not_be_registered_gives_the_two_commands_to_run(string how)
    {
        var notice = Shown(how);

        Assert.Contains($"claude plugin marketplace add \"{Folder}\"", notice.Text, StringComparison.Ordinal);
        Assert.Contains("claude plugin install claude-dashboard@claude-dashboard", notice.Text, StringComparison.Ordinal);
        Assert.Equal(HookNotice.NotConnectedShort, notice.TrayText);
    }

    [Fact]
    public void A_refusal_says_what_Claude_Code_said_in_one_short_line()
    {
        var notice = new HookNotice();

        notice.ShowClaudeRefused(Folder, "first line\r\n   second line\n" + new string('x', 500));

        Assert.Contains("first line second line", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 201), notice.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_refusal_with_no_reason_says_so(string? problem)
    {
        var notice = new HookNotice();

        notice.ShowClaudeRefused(Folder, problem);

        Assert.Contains("no reason was given", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_Claude_Code_says_none_was_detected()
    {
        var notice = Shown(nameof(HookNotice.ShowClaudeCodeNotInstalled));

        Assert.StartsWith("No Claude Code install was detected", notice.Text, StringComparison.Ordinal);
        Assert.Equal(HookNotice.NoClaudeCodeShort, notice.TrayText);
    }

    [Fact]
    public void A_removed_plugin_names_the_switch_that_puts_it_back()
    {
        var notice = Shown(nameof(HookNotice.ShowPluginRemoved));

        Assert.Contains("--remove-hooks", notice.Text, StringComparison.Ordinal);
        Assert.Contains("--install-hooks", notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The old hook is the operator's to remove, and the notice says how without asking
    /// them to edit a file</strong>: ask Claude, or use the <c>/hooks</c> command.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_old_hook_says_how_to_remove_it(bool pluginEnabled)
    {
        var notice = new HookNotice();

        notice.ShowOldHooks(pluginEnabled);

        Assert.Contains("remove all hooks for Claude Dashboard from my settings", notice.Text, StringComparison.Ordinal);
        Assert.Contains("/hooks", notice.Text, StringComparison.Ordinal);
        Assert.Contains("restart the dashboard", notice.Text, StringComparison.Ordinal);
        Assert.Equal(pluginEnabled, notice.Text!.Contains("every event arrives twice", StringComparison.Ordinal));
        Assert.Equal(HookNotice.OldHooksShort, notice.TrayText);
    }

    [Fact]
    public void Another_data_folders_plugin_is_named()
    {
        var notice = Shown(nameof(HookNotice.ShowOtherDataFolder));

        Assert.Contains(@"C:\Elsewhere\plugin", notice.Text, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_DASHBOARD_HOME", notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A plugin that was just registered is not a fault</strong>, so the tray tooltip has
    /// nothing to lead with. The window still says that open sessions need a restart.
    /// </summary>
    [Fact]
    public void A_just_registered_plugin_is_news_and_not_a_fault()
    {
        var notice = Shown(nameof(HookNotice.ShowJustRegistered));

        Assert.Null(notice.TrayText);
        Assert.Contains("already open", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_later_finding_replaces_an_earlier_one()
    {
        var notice = new HookNotice();

        notice.ShowJustRegistered();
        notice.ShowOldHooks(pluginEnabled: true);

        Assert.Equal(HookNoticeKind.OldHooks, notice.Kind);
        Assert.False(notice.ClearsOnEvent);
        Assert.Equal(HookNotice.OldHooksTwiceText, notice.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void A_notice_that_names_a_folder_needs_one(string? folder)
    {
        var notice = new HookNotice();

        Assert.ThrowsAny<ArgumentException>(() => notice.ShowClaudeNotFound(folder!));
        Assert.ThrowsAny<ArgumentException>(() => notice.ShowClaudeRefused(folder!, "x"));
        Assert.ThrowsAny<ArgumentException>(() => notice.ShowOtherDataFolder(folder!));
        Assert.False(notice.IsShown);
    }
}
