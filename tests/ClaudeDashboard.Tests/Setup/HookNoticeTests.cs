using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;

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

    /// <summary>The two notices an event clears only once Claude Code has the plugin enabled again.</summary>
    private static readonly HookNoticeKind[] ConfirmedByTheSettings = [HookNoticeKind.PluginDisabled, HookNoticeKind.PluginRemoved];

    /// <summary>
    /// <strong>An arriving event clears a notice that said nothing was reporting, and leaves the
    /// one that is about an old hook.</strong> With nothing to read Claude Code's settings, it also
    /// leaves the turned-off and removed notices: an event alone does not disprove those.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shows))]
    public void An_event_clears_exactly_the_notices_it_disproves(string how, HookNoticeKind kind, bool clearsOnEvent)
    {
        var notice = Shown(how);
        var raised = new List<string?>();
        notice.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        notice.EventArrived();

        var clears = clearsOnEvent && !ConfirmedByTheSettings.Contains(kind);

        Assert.Equal(!clears, notice.IsShown);
        Assert.Equal(ConfirmedByTheSettings.Contains(kind), notice.ClearsOncePluginEnabled && clearsOnEvent);

        if (clears)
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

    // ---- A plugin that is turned off or removed (PR #66 review, M1) ---------------------------------

    /// <summary>The two ways a notice says the plugin is off or gone.</summary>
    public static TheoryData<string> PluginOffOrGone => new()
    {
        nameof(HookNotice.ShowPluginDisabled),
        nameof(HookNotice.ShowPluginRemoved),
    };

    /// <summary>A notice whose re-read of Claude Code's settings is counted and answered by the test.</summary>
    private static (HookNotice Notice, FakeClock Clock, Func<int> Reads, Action<bool> SetEnabled) Confirmed(string how)
    {
        var notice = Shown(how);
        var clock = new FakeClock();
        var reads = 0;
        var enabled = false;
        notice.ConfirmPluginWith(() => { reads++; return enabled; }, clock);

        return (notice, clock, () => reads, value => enabled = value);
    }

    /// <summary>
    /// <strong>An event while the plugin is still off keeps the notice.</strong> A session opened
    /// before the plugin went off keeps reporting until it restarts; once it does, nothing reports,
    /// and a notice that cleared at its last event would leave the operator with no warning.
    /// </summary>
    [Theory]
    [MemberData(nameof(PluginOffOrGone))]
    public void An_event_while_the_plugin_is_still_off_keeps_the_notice(string how)
    {
        var (notice, _, reads, _) = Confirmed(how);
        var kind = notice.Kind;
        var raised = 0;
        notice.PropertyChanged += (_, _) => raised++;

        notice.EventArrived();

        Assert.Equal(1, reads());
        Assert.True(notice.IsShown);
        Assert.Equal(kind, notice.Kind);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// <strong>An event after the plugin is turned on clears the notice</strong>, at the first event
    /// once a read is due.
    /// </summary>
    [Theory]
    [MemberData(nameof(PluginOffOrGone))]
    public void An_event_after_the_plugin_is_enabled_clears_the_notice(string how)
    {
        var (notice, clock, reads, setEnabled) = Confirmed(how);

        notice.EventArrived();
        Assert.True(notice.IsShown);

        setEnabled(true);
        clock.Advance(HookNotice.RecheckInterval);
        notice.EventArrived();

        Assert.Equal(2, reads());
        Assert.False(notice.IsShown);
        Assert.Equal(HookNoticeKind.None, notice.Kind);
    }

    /// <summary>
    /// <strong>The first event reads at once; later ones at most once per interval.</strong> No
    /// event, no read: the notice never reads on a timer.
    /// </summary>
    [Fact]
    public void The_settings_are_read_at_the_first_event_and_then_at_most_once_per_interval()
    {
        var (notice, clock, reads, _) = Confirmed(nameof(HookNotice.ShowPluginDisabled));

        Assert.Equal(0, reads());

        notice.EventArrived();
        notice.EventArrived();
        clock.Advance(HookNotice.RecheckInterval - TimeSpan.FromSeconds(1));
        notice.EventArrived();

        Assert.Equal(1, reads());

        clock.Advance(TimeSpan.FromSeconds(1));
        notice.EventArrived();

        Assert.Equal(2, reads());
    }

    /// <summary>A notice shown afresh reads at its first event, whatever the last notice read.</summary>
    [Fact]
    public void A_notice_shown_again_reads_at_its_first_event()
    {
        var (notice, _, reads, _) = Confirmed(nameof(HookNotice.ShowPluginDisabled));

        notice.EventArrived();
        notice.ShowPluginRemoved();
        notice.EventArrived();

        Assert.Equal(2, reads());
    }

    /// <summary>
    /// <strong>The other notices do not read Claude Code's settings.</strong> An event proves the
    /// route works for them, so it clears them at once; the old-hook notice is not cleared even by
    /// a read that finds the plugin enabled, because the old hook is still there.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shows))]
    public void Only_the_turned_off_and_removed_notices_read_the_settings(string how, HookNoticeKind kind, bool clearsOnEvent)
    {
        var (notice, _, reads, setEnabled) = Confirmed(how);
        setEnabled(true);

        notice.EventArrived();

        Assert.Equal(ConfirmedByTheSettings.Contains(kind) ? 1 : 0, reads());
        Assert.Equal(!clearsOnEvent, notice.IsShown);
    }

    [Fact]
    public void Confirming_needs_a_reader_and_a_clock()
    {
        var notice = new HookNotice();

        Assert.Throws<ArgumentNullException>(() => notice.ConfirmPluginWith(null!, new FakeClock()));
        Assert.Throws<ArgumentNullException>(() => notice.ConfirmPluginWith(() => true, null!));
    }
}
