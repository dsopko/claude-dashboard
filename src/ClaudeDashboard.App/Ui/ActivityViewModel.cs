using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Ports;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>How much of a line the Activity window has room for (T1.70).</summary>
public enum ActivityLayout
{
    /// <summary>One line, in columns.</summary>
    Wide = 1,

    /// <summary>Two lines: the second, small and grey, holds the project and the detail.</summary>
    TwoLines = 2,

    /// <summary>Two lines, without the detail: the detail goes first.</summary>
    NoDetail = 3,

    /// <summary>One line, without the project either. The time, the sign, what occurred and the name never go.</summary>
    NoProject = 4,
}

/// <summary>
/// One line on screen (T1.70): the words, and the time as it reads today. When the local date changes the log
/// asks it to read the time again (<see cref="Reread"/>), so yesterday's line gains its day name.
/// </summary>
public sealed class ActivityLineViewModel : ObservableObject
{
    /// <summary>Wraps <paramref name="line"/>, with its time read against <paramref name="now"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    public ActivityLineViewModel(ActivityLine line, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(line);

        Line = line;
        Time = line.TimeText(now);
        Sentence = line.Sentence(now);
    }

    /// <summary>The words.</summary>
    public ActivityLine Line { get; }

    /// <summary>The line's order: the higher, the newer.</summary>
    public long Id => Line.Id;

    /// <summary>"14:32", or "Mon 23:58" for a line not from today.</summary>
    public string Time { get; private set; }

    /// <summary>"♪" for a sound that played; empty otherwise.</summary>
    public string Sign => Line.Played ? "♪" : string.Empty;

    /// <summary>What occurred.</summary>
    public string What => Line.What;

    /// <summary>The session's name, its short id, or a group's name.</summary>
    public string Name => Line.Name;

    /// <summary>The project, the last folder of the path.</summary>
    public string Project => Line.Project;

    /// <summary>The full path, for the hover on the project; null when there is none.</summary>
    public string? ProjectPath => Line.ProjectPath;

    /// <summary>The detail.</summary>
    public string Detail => Line.Detail;

    /// <summary>Whether there is a project to show.</summary>
    public bool HasProject => Line.Project.Length > 0;

    /// <summary>Whether there is a detail to show.</summary>
    public bool HasDetail => Line.Detail.Length > 0;

    /// <summary>The line as one sentence: the screen reader's name, and the hover, which holds everything.</summary>
    public string Sentence { get; private set; }

    /// <summary>Reads the time again against <paramref name="now"/>, and says so if it changed. UI thread only.</summary>
    public void Reread(DateTimeOffset now)
    {
        var time = Line.TimeText(now);

        if (time == Time)
        {
            return;
        }

        Time = time;
        Sentence = Line.Sentence(now);
        OnPropertyChanged(nameof(Time));
        OnPropertyChanged(nameof(Sentence));
    }
}

/// <summary>
/// <summary>
/// The Activity window (T1.70, issue #97): what the dashboard did since it started, newest first, in
/// plain words.
/// </summary>
/// <remarks>
/// <para>
/// The lines are <see cref="ActivityLog"/>'s one list, which the window binds to directly: made at start,
/// fed from the consumer's decisions, and never read from <c>dashboard.db</c>. This type adds what is the
/// window's own: the layout its width allows, and at the top "last heard from Claude Code …", the tray
/// tooltip's own words (T1.61).
/// </para>
/// </remarks>
public sealed partial class ActivityViewModel : ObservableObject, IUiTickTarget
{
    /// <summary>The width from which a line is one line in columns.</summary>
    public const double WideFrom = 620;

    /// <summary>The width from which the second line keeps the detail.</summary>
    public const double DetailFrom = 420;

    /// <summary>The width from which the second line keeps the project.</summary>
    public const double ProjectFrom = 320;

    private readonly HookHealth? _health;
    private DateTimeOffset _now;

    /// <summary>The layout the window's width allows.</summary>
    [ObservableProperty]
    private ActivityLayout _layout = ActivityLayout.Wide;

    /// <summary>Creates the view model over the one list.</summary>
    /// <param name="log">The lines.</param>
    /// <param name="clock">What time it is, for "last heard".</param>
    /// <param name="health">When Claude Code was last heard from; null in a test that is not about it.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ActivityViewModel(ActivityLog log, IClock clock, HookHealth? health)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        Log = log;
        _health = health;
        _now = clock.Now;
    }

    /// <summary>The one list, and whether its oldest lines have gone.</summary>
    public ActivityLog Log { get; }

    /// <summary>"last heard from Claude Code 2 min ago": the tooltip's own words (T1.61).</summary>
    public string LastHeardText => TrayTooltip.LastHeard(_health?.LastHeardAt, _now);

    /// <summary>The window's width changed: the layout follows it.</summary>
    public void SetWidth(double width) => Layout = LayoutFor(width);

    /// <summary>The layout for a width: the detail goes first, then the project.</summary>
    public static ActivityLayout LayoutFor(double width) => width switch
    {
        >= WideFrom => ActivityLayout.Wide,
        >= DetailFrom => ActivityLayout.TwoLines,
        >= ProjectFrom => ActivityLayout.NoDetail,
        _ => ActivityLayout.NoProject,
    };

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        _now = now;
        OnPropertyChanged(nameof(LastHeardText));

        // The day name follows the day: the log reads the lines' times again when the date changes.
        Log.Tick(now);
    }
}
