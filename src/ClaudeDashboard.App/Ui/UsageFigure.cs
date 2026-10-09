using System.Globalization;
using ClaudeDashboard.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// One pair of the usage strip, "Week 64%": what the window shows for one slot (MOD.7, issue #133), the share
/// remaining (MOD.8, issue #144).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One instance for each slot, kept for the life of the window and changed in place.</strong> The strip
/// is three pieces of markup, one for each slot, because <see cref="FittingStrip"/> reads its words once and
/// cannot follow items that a template makes later. So a slot with no reading is not removed: it says so in
/// <see cref="IsShown"/>, and its markup collapses.
/// </para>
/// <para>
/// The label ("Week") is in the markup, as the counts' words are. The words of the hover text are here, because
/// they are put together with the figure and the reset time.
/// </para>
/// </remarks>
public sealed partial class UsageFigure : ObservableObject
{
    /// <summary>Creates the pair for <paramref name="slot"/>, not shown.</summary>
    public UsageFigure(UsageSlot slot) => Slot = slot;

    /// <summary>The slot this pair shows.</summary>
    public UsageSlot Slot { get; }

    /// <summary>Whether the slot has a reading, live or fresh. False before the first post.</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>Whether a pair to the left of this one is shown, so this one starts with " · ".</summary>
    /// <remarks>
    /// The counts need no such flag, because their first count, the total, is always there. Any of the three
    /// slots can be absent, so the separator goes with the slots before it.
    /// </remarks>
    [ObservableProperty]
    private bool _hasSeparator;

    /// <summary>The share remaining with its percent sign, "64%" (ruling R13).</summary>
    [ObservableProperty]
    private string _figure = string.Empty;

    /// <summary>The figure's colour, from Core, by the share used (rulings R10 and R13).</summary>
    [ObservableProperty]
    private UsageLevel _level;

    /// <summary>
    /// The hover text: "This week · 64% remaining · Resets Wednesday 9:00 AM" (ruling R11), or for a fresh slot
    /// "Current session · 100% remaining · Reset at 6:20 AM; the next reset time comes with the next reading"
    /// (ruling R14).
    /// </summary>
    [ObservableProperty]
    private string _hoverText = string.Empty;

    /// <summary>The name of the window in the hover text: longer than the label, because there is room.</summary>
    public string WindowName => Slot switch
    {
        UsageSlot.Current => "Current session",
        UsageSlot.Week => "This week",
        _ => "Fable this week",
    };

    /// <summary>The words after a fresh slot's reset time: its percentage comes back with the next post.</summary>
    public const string NextReadingText = "the next reset time comes with the next reading";

    /// <summary>Shows <paramref name="slot"/> in this pair, as it stands at <paramref name="now"/>.</summary>
    /// <param name="slot">The slot's reading at <paramref name="now"/>, live or fresh, from Core.</param>
    /// <param name="now">The UI tick's instant, which says whether the reset is today.</param>
    /// <param name="hasSeparator">Whether a pair to the left of this one is shown.</param>
    public void Show(UsageSlotReading slot, DateTimeOffset now, bool hasSeparator)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var figure = string.Create(CultureInfo.CurrentCulture, $"{slot.Remaining:0}%");
        var reset = slot.Window.ResetsAt is { } at ? ResetsText(at, now, passed: slot.IsFresh) : null;

        Figure = figure;
        Level = slot.Level;
        HoverText = (slot.IsFresh, reset) switch
        {
            (true, { } passed) => $"{WindowName} · {figure} remaining · {passed}; {NextReadingText}",
            (true, null) => $"{WindowName} · {figure} remaining; {NextReadingText}",
            (false, { } resets) => $"{WindowName} · {figure} remaining · {resets}",
            (false, null) => $"{WindowName} · {figure} remaining",
        };
        HasSeparator = hasSeparator;
        IsShown = true;
    }

    /// <summary>Hides this pair: the slot's kind was never reported.</summary>
    public void Hide()
    {
        IsShown = false;
        HasSeparator = false;
    }

    /// <summary>
    /// "Resets at 6:20 AM" when <paramref name="reset"/> is today in local time, "Resets Wednesday 9:00 AM"
    /// otherwise, in the current culture's short time pattern; "Reset at …" and "Reset Wednesday …" when
    /// <paramref name="passed"/>; null for an instant that has no local time.
    /// </summary>
    /// <remarks>
    /// <strong>Degrade, never crash.</strong> A post can carry a reset time near the end of the calendar, and the
    /// conversion to a local zone then throws. The pair then shows no reset part, and the figure stays.
    /// </remarks>
    internal static string? ResetsText(DateTimeOffset reset, DateTimeOffset now, bool passed = false)
    {
        DateTimeOffset local;
        DateTimeOffset today;

        try
        {
            local = TimeZoneInfo.ConvertTime(reset, TimeZoneInfo.Local);
            today = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        var verb = passed ? "Reset" : "Resets";

        return local.Date == today.Date
            ? string.Create(CultureInfo.CurrentCulture, $"{verb} at {local:t}")
            : string.Create(CultureInfo.CurrentCulture, $"{verb} {local:dddd} {local:t}");
    }
}
