using Serilog;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// The message box a second start shows when it exits without bringing a window forward
/// (Impl §5.3, issue #150).
/// </summary>
/// <remarks>
/// <para>
/// <strong>You double-clicked, and nothing happened.</strong> Before this, a second start that could
/// not hand over wrote one line to the log and exited. The log line stays, and the box comes after
/// it, never instead of it: the line holds the detail, and the box says where it is.
/// </para>
/// <para>
/// <strong>The text is a pure function; the box is a seam.</strong> <see cref="TextFor"/> chooses
/// the words and is tested for every exit path. <see cref="Show"/> calls whatever
/// <c>Program</c> supplies (<c>System.Windows.MessageBox.Show</c>), and the tests supply a
/// recorder, so no test ever shows a box.
/// </para>
/// </remarks>
public static class StandDownNotice
{
    /// <summary>The box's title.</summary>
    public const string Title = "Claude Dashboard";

    /// <summary>The text when a dashboard on this data folder runs in another Windows session.</summary>
    public const string OtherSession =
        "Claude Dashboard is already running in another Windows session for this account. " +
        "Quit it there, or end its process, and start again.";

    /// <summary>The text when the running dashboard could not be asked to show its window.</summary>
    public const string CannotBeReached =
        "Claude Dashboard is already running, but it could not be asked to show its window. " +
        "Open it from its tray icon, or end its process and start again.";

    /// <summary>
    /// The text of the box for this exit, or null when no box is shown.
    /// </summary>
    /// <remarks>
    /// Null for <see cref="ShowSignalOutcome.Shown"/>, where the window came forward, and for
    /// <see cref="StartupAction.StartNormally"/>, which does not exit. Every other way out of a
    /// second start gets a text, and each text ends with a line that says where the log is.
    /// </remarks>
    /// <param name="action">What the start decided.</param>
    /// <param name="show">
    /// What <c>/show</c> answered, or null when it was not sent (no recorded port to send it to).
    /// Read only for <see cref="StartupAction.SignalAndExit"/>.
    /// </param>
    /// <param name="logFolder">The folder of the rolling log file.</param>
    public static string? TextFor(StartupAction action, ShowSignalOutcome? show, string logFolder)
    {
        var text = action switch
        {
            StartupAction.ExitForOtherSession => OtherSession,
            StartupAction.ReportAndExit => CannotBeReached,
            StartupAction.SignalAndExit when show == ShowSignalOutcome.Shown => null,

            // Rejected, Failed, a transport problem, or no port to send /show to.
            StartupAction.SignalAndExit => CannotBeReached,
            _ => null,
        };

        // The rolling file's name carries the date and, after a size roll, a suffix, so the
        // folder is named rather than a file name that can be wrong.
        return text is null
            ? null
            : $"{text}{Environment.NewLine}{Environment.NewLine}The details are in the newest log file in {logFolder}";
    }

    /// <summary>Shows <paramref name="text"/>, unless it is null. A box that throws is logged, never rethrown.</summary>
    /// <param name="show">Shows a box with a text and a title.</param>
    /// <param name="text">From <see cref="TextFor"/>.</param>
    /// <param name="logger">Where a failure to show the box is written.</param>
    public static void Show(Action<string, string> show, string? text, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(logger);

        if (text is null)
        {
            return;
        }

        try
        {
            show(text, Title);
        }
        catch (Exception ex)
        {
            // Degrade, never crash: the log line above this box already holds the reason.
            logger.Warning(ex, "Could not show the message box that says why this start exited.");
        }
    }
}
