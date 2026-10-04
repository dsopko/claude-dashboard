using System.ComponentModel;
using System.Windows.Data;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    /// <summary>The hover's reason for a line whose session is not in the main window (T1.71).</summary>
    public const string SessionGoneText = "This session is no longer in the window.";

    /// <summary>The hover's reason for a group's line whose group is not in the main window (T1.71).</summary>
    public const string GroupGoneText = "This group is not in the window.";

    private readonly Action<ActivityLine>? _show;
    private bool _isGone;

    /// <summary>Wraps <paramref name="line"/>, with its time read against <paramref name="now"/>.</summary>
    /// <param name="line">The words.</param>
    /// <param name="now">What time it is.</param>
    /// <param name="show">What a click on the line does: show it in the main window; null does nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    public ActivityLineViewModel(ActivityLine line, DateTimeOffset now, Action<ActivityLine>? show = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        Line = line;
        Time = line.TimeText(now);
        Sentence = line.Sentence(now);
        _show = show;
        ShowCommand = new RelayCommand(() => _show?.Invoke(Line));
    }

    /// <summary>
    /// A click on the line, and Enter on a selected line (T1.71): brings the main window to the front and shows
    /// the line's row there. A command, so a test reaches it the way the click does.
    /// </summary>
    public IRelayCommand ShowCommand { get; }

    /// <summary>
    /// Whether the line's session, or its group, is not in the main window, so a click only brings the window
    /// to the front (T1.71). Set by the log. A line about neither is never gone.
    /// </summary>
    public bool IsGone
    {
        get => _isGone;
        internal set
        {
            if (_isGone == value)
            {
                return;
            }

            _isGone = value;
            OnPropertyChanged(nameof(IsGone));
            OnPropertyChanged(nameof(GoneText));
            OnPropertyChanged(nameof(Hover));
        }
    }

    /// <summary>Why a click does nothing more than bring the main window to the front; or empty.</summary>
    public string GoneText => !IsGone
        ? string.Empty
        : Line.SessionId is not null ? SessionGoneText : GroupGoneText;

    /// <summary>The hover: the sentence, and under it why a click does no more, when it does not.</summary>
    public string Hover => IsGone ? Sentence + "\n" + GoneText : Sentence;

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
        OnPropertyChanged(nameof(Hover));
    }
}

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
/// <para>
/// <strong>"Show activity" is a filter on the one list, not a copy</strong> (T1.71): <see cref="Shown"/> is a
/// view over the log's lines, so a new line for that session arrives in it and a line for another does not. A
/// bar at the top says "Only Director" with <b>Show all</b>, which clears it.
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

        // A view over the one list, made here on the UI thread, where the log's changes arrive.
        Shown = new ListCollectionView(log.Lines);
        ShowAllCommand = new RelayCommand(ShowAll);
    }

    /// <summary>
    /// What the list shows: the log's lines, through the filter of "Show activity" when there is one (T1.71).
    /// </summary>
    public ICollectionView Shown { get; }

    /// <summary>The full id of the session the list is filtered to; null when it shows every line.</summary>
    public string? OnlySession { get; private set; }

    /// <summary>Whether the list shows one session's lines only.</summary>
    public bool IsFiltered => OnlySession is not null;

    /// <summary>The bar's words: "Only Director", or the short id when the session has no name; or empty.</summary>
    public string OnlyText { get; private set; } = string.Empty;

    /// <summary>The bar's <b>Show all</b>: every line again.</summary>
    public IRelayCommand ShowAllCommand { get; }

    /// <summary>
    /// "Show activity" on a row: only <paramref name="session"/>'s lines, under a bar that reads
    /// "Only <paramref name="name"/>". On another row it changes the filter to that row. UI thread only.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="session"/> is empty.</exception>
    public void ShowOnly(SessionId session, string name)
    {
        if (session.IsEmpty)
        {
            throw new ArgumentException("A filter needs a session.", nameof(session));
        }

        OnlySession = session.Value;
        OnlyText = "Only " + name;

        // Set each time, so a second "Show activity" filters again by the new session.
        Shown.Filter = Passes;
        RaiseFilter();
    }

    private void ShowAll()
    {
        OnlySession = null;
        OnlyText = string.Empty;
        Shown.Filter = null;
        RaiseFilter();
    }

    /// <summary>A line about the session the list is filtered to; a line about no session never is.</summary>
    private bool Passes(object item) =>
        item is ActivityLineViewModel line && line.Line.SessionId is { } id && id == OnlySession;

    private void RaiseFilter()
    {
        OnPropertyChanged(nameof(OnlySession));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(OnlyText));
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
