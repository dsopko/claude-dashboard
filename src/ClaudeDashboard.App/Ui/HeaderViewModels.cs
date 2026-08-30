using System.Globalization;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// A labelled band heading in flat view (Design Document §7; mockups, flat view).
/// </summary>
/// <remarks>
/// Only appears where the band has sessions. <c>AttentionEngine.Order</c> omits an empty band
/// rather than returning it empty, so "absent" and "empty" are one fact rather than two, and the
/// mockups show headers only above rows — there is no persistent heading for a band with nothing
/// in it.
/// </remarks>
public sealed class BandHeaderViewModel(AttentionBand band) : DashboardRow
{
    private int _count;
    private bool _isExpanded;

    /// <summary>Which band this heads.</summary>
    public AttentionBand Band { get; } = band;

    /// <summary>The heading, as the mockups letter it: "NEEDS YOU", "UNREAD", …</summary>
    public string Label => Band switch
    {
        AttentionBand.NeedsYou => "NEEDS YOU",
        AttentionBand.Unread => "UNREAD",
        AttentionBand.Working => "WORKING",
        AttentionBand.Quiet => "QUIET",
        AttentionBand.Ended => "ENDED",
        _ => Band.ToString().ToUpperInvariant(),
    };

    /// <summary>The colour the heading reads as.</summary>
    public Accent Accent => Band switch
    {
        AttentionBand.NeedsYou => Accent.Red,
        AttentionBand.Unread => Accent.Green,
        AttentionBand.Working => Accent.Blue,
        _ => Accent.Grey,
    };

    /// <summary>
    /// Whether this band may be summarised to a single line rather than listed row by row.
    /// </summary>
    /// <remarks>
    /// Only the two bands that hold work already dealt with. Design Document §6 rule 3 is
    /// explicit that Unread is never summarised away — it is the thing the tool exists to
    /// surface — and Needs You and Working are what the operator opened the window for.
    /// </remarks>
    public bool IsCollapsible => Band is AttentionBand.Quiet or AttentionBand.Ended;

    /// <summary>Whether a collapsible band is currently listed in full.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    /// <summary>How many sessions are under it.</summary>
    public int Count
    {
        get => _count;
        internal set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            OnPropertyChanged(nameof(Count));
        }
    }
}

/// <summary>
/// The single line that stands in for rows already dealt with — "+ 3 quiet" under a group, or
/// "4 quiet sessions" under a band (Design Document §6 rule 2; mockups).
/// </summary>
/// <remarks>
/// It is a row like any other so that expanding and collapsing is an ordinary reconcile rather
/// than a special case in the binding, and so the footer can be clicked to expand what it hides.
/// </remarks>
public sealed class QuietFooterViewModel(DashboardRow owner, string key, bool isBandSummary) : DashboardRow
{
    private int _count;

    /// <summary>
    /// The heading whose <c>IsExpanded</c> this footer flips — its group, or its band.
    /// </summary>
    /// <remarks>
    /// Held rather than duplicated so that the footer and the heading above it cannot disagree
    /// about whether the quiet rows are showing. The binding reaches through it.
    /// </remarks>
    public DashboardRow Owner { get; } = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>What this footer belongs to — a group key, or a band. Not for display.</summary>
    public string Key { get; } = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>Whether this stands under a band heading rather than inside a group.</summary>
    public bool IsBandSummary { get; } = isBandSummary;

    /// <summary>How many rows it stands for.</summary>
    public int Count
    {
        get => _count;
        internal set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(Text));
        }
    }

    /// <summary>The line itself.</summary>
    public string Text => IsBandSummary
        ? string.Create(CultureInfo.CurrentCulture, $"{Count} quiet {(Count == 1 ? "session" : "sessions")}")
        : string.Create(CultureInfo.CurrentCulture, $"+ {Count} quiet");
}

/// <summary>
/// A group heading in grouped view (Design Document §7, §9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The label never comes from the key.</strong> A <see cref="GroupKey"/> is an identity:
/// T1.4's canonical form folds casing and prefixes the kind, so binding it would put
/// <c>WORKSPACE:C:\PROJECTS\DASHBOARD</c> on screen. The heading is built from a member's
/// <see cref="Session.Cwd"/> instead, and <see cref="GroupKeys.KindOf"/> decides whether a
/// directory name is even the right thing to show — a session with no workspace is keyed on
/// itself and has no path to display.
/// </para>
/// <para>
/// The mockups show both halves: the folder name, and the full path beside it.
/// </para>
/// </remarks>
public sealed class GroupViewModel : DashboardRow
{
    private Group _group;
    private bool _isExpanded;
    private bool _isStale;
    private TimeSpan _idleAge;
    private SessionState? _displayState;

    /// <summary>Heads <paramref name="group"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    public GroupViewModel(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _group = group;
    }

    /// <summary>The group's identity. Not for display — see the remarks on this type.</summary>
    public GroupKey Key => _group.Key;

    /// <summary>The group as it currently stands.</summary>
    public Group Group
    {
        get => _group;
        internal set
        {
            ArgumentNullException.ThrowIfNull(value);

            // Group compares by key and member sequence (T1.4), so an unchanged group is equal to
            // the one it replaces and raises nothing.
            if (_group == value)
            {
                return;
            }

            _group = value;
            RaiseAll();
        }
    }

    /// <summary>What this group is keyed on — a workspace, or a session that has none.</summary>
    public GroupKeyKind Kind => GroupKeys.KindOf(_group.Key);

    /// <summary>The workspace every member shares, or null when there is none to show.</summary>
    public string? Workspace =>
        Kind == GroupKeyKind.Workspace && !string.IsNullOrWhiteSpace(_group.Members[0].Cwd)
            ? _group.Members[0].Cwd
            : null;

    /// <summary>
    /// The heading: the workspace's folder name, or the session's id when it has no workspace.
    /// </summary>
    public string Label
    {
        get
        {
            if (Kind == GroupKeyKind.Roster)
            {
                // The roster's own name, which is what the operator called this group — never a
                // member's directory, and never a member's title. The key carries the name because
                // GroupKeys.ForRoster built it from the name, so this needs no lookup and cannot
                // disagree with the store.
                return GroupKeys.RosterNameOf(_group.Key) ?? _group.Key.Value;
            }

            if (Workspace is not { } workspace)
            {
                // Keyed on the session itself, so there is no directory — name it after the
                // session rather than rendering a key that only looks like a path.
                return _group.Members[0].Id.Value;
            }

            return RowVisuals.WorkspaceLabel(workspace);
        }
    }

    /// <summary>How many sessions are in it.</summary>
    public int SessionCount => _group.Members.Count;

    /// <summary>
    /// Whether the operator has asked to see the members already dealt with.
    /// </summary>
    /// <remarks>
    /// <strong>One toggle, two presentations.</strong> A group where everything is quiet collapses
    /// to a single stale line; a group with live work keeps its rows and hides only the quiet ones
    /// behind a "+ k quiet" footer. Both are the same question — "show me what has been dealt
    /// with" — so both are this flag, and clicking either the stale line or the footer flips it.
    /// </remarks>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    /// <summary>
    /// Whether every member is quiet and has been for long enough to collapse
    /// (Design Document §6 rule 1). Decided by <see cref="MainViewModel"/>, which holds the clock.
    /// </summary>
    public bool IsStale
    {
        get => _isStale;
        internal set
        {
            if (_isStale == value)
            {
                return;
            }

            _isStale = value;
            OnPropertyChanged(nameof(IsStale));
        }
    }

    /// <summary>How long since anything in the group happened.</summary>
    public TimeSpan IdleAge
    {
        get => _idleAge;
        internal set
        {
            if (_idleAge == value)
            {
                return;
            }

            _idleAge = value;
            OnPropertyChanged(nameof(IdleAge));
            OnPropertyChanged(nameof(IdleText));
        }
    }

    /// <summary>
    /// The stale line's age — "idle 38 min" (Design Document §6).
    /// </summary>
    /// <remarks>
    /// §6 writes it as "quiet 38 min", which is not quite true of a group whose members have all
    /// ended: they are not being quiet, they are gone. "Idle" covers both without needing the line
    /// to know which it is.
    /// </remarks>
    public string IdleText => string.Create(
        CultureInfo.CurrentCulture,
        $"idle {RowVisuals.Duration(IdleAge)}");

    /// <summary>The colour the heading reads as: the worst member's, from Core.</summary>
    public Accent Accent => RowVisuals.AccentOf(WorstState);

    /// <summary>One dot per member, in the group's own order — the mockups' <c>.gdots</c>.</summary>
    public IReadOnlyList<Accent> MemberAccents =>
        [.. _group.Members.Select(member => RowVisuals.AccentOf(member.State))];

    /// <summary>
    /// The group's roll-up state as the row reads it — the settle window included for a roster
    /// group (issue #16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Still never computed here. <see cref="Group.WorstState"/> supplies the raw roll-up and knows
    /// which severity order applies; <see cref="RosterSettle.StateOf"/> holds a roster group at
    /// working while a hand-off may still be in flight. Both are Core's, and this row is handed
    /// the answer rather than deriving one.
    /// </para>
    /// <para>
    /// <strong>Set by the caller because the settle needs a clock, and a row has none.</strong>
    /// Defaulting to the raw roll-up means a row nobody has ticked reads what it would have read
    /// before this feature existed, which is the right answer for every group that is not a roster.
    /// </para>
    /// </remarks>
    public SessionState WorstState
    {
        get => _displayState ?? _group.WorstState;

        set
        {
            if (_displayState == value)
            {
                return;
            }

            _displayState = value;
            OnPropertyChanged(nameof(WorstState));
            OnPropertyChanged(nameof(Accent));
        }
    }

    /// <summary>The most recent activity across its members.</summary>
    public DateTimeOffset LastActivity => _group.LastActivity;

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(SessionCount));
        OnPropertyChanged(nameof(WorstState));
        OnPropertyChanged(nameof(LastActivity));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(MemberAccents));
    }
}
