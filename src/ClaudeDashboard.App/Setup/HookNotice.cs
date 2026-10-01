using System.ComponentModel;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// What a start found about the hook route that the operator must see on screen, not only in the
/// log (the operator's ruling of 2026-10-01, R1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One case today: the dashboard's plugin is turned off.</strong> Claude Code has it
/// registered and set to <see langword="false"/> — the operator's own choice. A start honours it:
/// it does not run <c>claude plugin install</c>, which would turn it back on, and it does not write
/// the settings handler around it. So the dashboard receives nothing, and that must not look like a
/// quiet day. The window shows <see cref="Text"/>; the tray tooltip leads with <see cref="TrayText"/>,
/// the way it leads with a port that is taken.
/// </para>
/// <para>
/// <strong>The command in <see cref="Text"/> was run before it was written here</strong>, on Claude
/// Code 2.1.286: <c>claude plugin enable claude-dashboard@claude-dashboard</c> exits 0 and sets the
/// plugin to <see langword="true"/>. An open session was measured too: one started while the plugin
/// was off reported nothing after the enable, and a session started after it reported at once —
/// so the text says to restart open sessions.
/// </para>
/// <para>
/// <strong>It clears at the next start</strong>, and says so. Set once, on the start's thread,
/// before the window or the tray reads it; raised as a property change so a binding made earlier
/// still follows.
/// </para>
/// </remarks>
public sealed class HookNotice : INotifyPropertyChanged
{
    /// <summary>The tray's short form, in the voice of its other faults.</summary>
    public const string PluginDisabledShort = "plugin off · not receiving hooks";

    /// <summary>The window's text: what is wrong, the command that fixes it, and what then.</summary>
    public static readonly string PluginDisabledText =
        "Claude Code's plugin for this dashboard is turned off, so the dashboard receives nothing from " +
        $"Claude Code. To turn it on, run this in a terminal: claude plugin enable {HookPlugin.Id}. " +
        "Then restart each Claude Code session that is open: an open session does not see the change. " +
        "This notice clears the next time the dashboard starts.";

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The window's text, or null when there is nothing to say.</summary>
    public string? Text { get; private set; }

    /// <summary>The tray tooltip's short form, or null when there is nothing to say.</summary>
    public string? TrayText { get; private set; }

    /// <summary>Whether there is anything to show.</summary>
    public bool IsShown => Text is not null;

    /// <summary>Shows that the dashboard's plugin is turned off.</summary>
    public void ShowPluginDisabled()
    {
        Text = PluginDisabledText;
        TrayText = PluginDisabledShort;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
    }
}
