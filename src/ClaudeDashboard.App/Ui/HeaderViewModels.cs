using System.IO;
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

    /// <summary>Which band this heads.</summary>
    public AttentionBand Band { get; } = band;

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
            if (Workspace is not { } workspace)
            {
                // Keyed on the session itself, so there is no directory — name it after the
                // session rather than rendering a key that only looks like a path.
                return _group.Members[0].Id.Value;
            }

            var name = Path.GetFileName(workspace.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? workspace : name;
        }
    }

    /// <summary>How many sessions are in it.</summary>
    public int SessionCount => _group.Members.Count;

    /// <summary>The group's roll-up state — from <see cref="Group.WorstState"/>, never computed here.</summary>
    public SessionState WorstState => _group.WorstState;

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
    }
}
