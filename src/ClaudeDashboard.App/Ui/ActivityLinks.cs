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

/// <summary>"Show activity" on a row (T1.71): the session the Activity window lists, and its words for the bar.</summary>
/// <param name="Session">The session.</param>
/// <param name="Name">Its name as the row shows it, or its short id when it has none.</param>
/// <param name="Project">Its project, the last folder of its path, or empty (issue #157).</param>
public sealed record ActivityRequest(SessionId Session, string Name, string Project);

/// <summary>
/// The links between the main window and the Activity window (T1.71, issue #97): from a line to its row, from a row
/// to its lines, and from the toolbar and the tray to every line (issue #157).
/// </summary>
public static class ActivityLinks
{
    /// <summary>
    /// Connects them, once, on the UI thread, when both windows exist. A click on a line reaches the main window
    /// through <paramref name="rows"/>; the lines ask again whether their rows are there when the main window's
    /// sessions or groups change; "Show activity" on a row filters the list and shows the Activity window; and the
    /// Activity command of the toolbar and the tray menu clears the filter and shows it.
    /// </summary>
    /// <param name="rows">The main window.</param>
    /// <param name="main">The main window's view model, which says when its sessions change and asks for a filter.</param>
    /// <param name="tray">The tray's view model, whose Activity command the toolbar shares.</param>
    /// <param name="activity">The Activity window's view model and its one list.</param>
    /// <param name="showActivity">Brings the Activity window to the front.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void Connect(
        IActivityRows rows,
        MainViewModel main,
        TrayViewModel tray,
        ActivityViewModel activity,
        Action showActivity)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(showActivity);

        activity.Log.Rows = rows;
        activity.Log.Recheck();
        main.PresenceChanged += (_, _) => activity.Log.Recheck();
        main.ActivityRequested += (_, request) =>
        {
            activity.ShowOnly(request.Session, request.Name, request.Project);
            showActivity();
        };

        // The window is only hidden when it closes, so a filter outlives it. The toolbar and the tray ask for the
        // Activity window as a whole, and a filter left from a "Show activity" days before hid every other
        // session's lines (issue #157).
        tray.ActivityRequested += (_, _) =>
        {
            activity.ShowAll();
            showActivity();
        };
    }
}
