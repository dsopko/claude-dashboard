using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The main window, as the Activity window reaches it (T1.71, issue #97): whether a line's row is there, and
/// showing it. The main window implements it; a test may fake it.
/// </summary>
public interface IActivityRows
{
    /// <summary>Whether the line's session, or for a group's line its group, is in the main window.</summary>
    bool Has(ActivityLine line);

    /// <summary>
    /// A click on the line: the main window comes to the front, and the line's row is unfolded, scrolled into view
    /// and opened, as a click on the row would open it. Nothing else: no Ack, no mute, no event.
    /// </summary>
    void Show(ActivityLine line);
}

/// <summary>"Show activity" on a row (T1.71): the session the Activity window lists, and its name for the bar.</summary>
/// <param name="Session">The session.</param>
/// <param name="Name">Its name as the row shows it, or its short id when it has none.</param>
public sealed record ActivityRequest(SessionId Session, string Name);

/// <summary>
/// The two links between the main window and the Activity window (T1.71, issue #97): from a line to its row, and
/// from a row to its lines.
/// </summary>
public static class ActivityLinks
{
    /// <summary>
    /// Connects them, once, on the UI thread, when both windows exist. A click on a line reaches the main window
    /// through <paramref name="rows"/>; the lines ask again whether their rows are there when the main window's
    /// sessions or groups change; and "Show activity" on a row filters the list and shows the Activity window.
    /// </summary>
    /// <param name="rows">The main window.</param>
    /// <param name="main">The main window's view model, which says when its sessions change and asks for a filter.</param>
    /// <param name="activity">The Activity window's view model and its one list.</param>
    /// <param name="showActivity">Brings the Activity window to the front.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void Connect(IActivityRows rows, MainViewModel main, ActivityViewModel activity, Action showActivity)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(showActivity);

        activity.Log.Rows = rows;
        activity.Log.Recheck();
        main.PresenceChanged += (_, _) => activity.Log.Recheck();
        main.ActivityRequested += (_, request) =>
        {
            activity.ShowOnly(request.Session, request.Name);
            showActivity();
        };
    }
}
