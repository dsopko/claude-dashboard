using System.Collections.ObjectModel;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core.Ports;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// What the dashboard did since it started, in plain words: the Activity window's one list, in memory
/// (T1.70, issue #97).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The source is the consumer's decisions</strong>, the same ones it hands to the archive: the
/// recorder tells this log with each record's decisions (<c>DecisionRecorder.Decided</c>), on the consumer
/// thread. A record that holds a shown line becomes one dispatcher post, and a record without one becomes
/// none. The post adds the lines on the UI thread. <strong>Nothing is read from <c>dashboard.db</c></strong>
/// (the operator's ruling of 2026-10-04), so the store's state changes nothing here: a line the store could
/// not write still shows, because the window is a log of what the dashboard did, not of what it recorded.
/// </para>
/// <para>
/// <strong>One list from the start.</strong> It is made with the host, before the first event, and the window
/// binds to it directly; lines are added while the window is hidden, and nothing is built again when it opens.
/// Newest first.
/// </para>
/// <para>
/// <strong>The newest <see cref="Limit"/> lines are kept.</strong> When a new line would pass the limit, the
/// oldest goes, and <see cref="HasDropped"/> says so at the bottom of the list. A safety net for a dashboard
/// that runs for weeks: a normal three weeks is a few thousand lines.
/// </para>
/// <para>
/// On the consumer thread it only picks the shown decisions, numbers them and posts: no lock, no wait, no log
/// line, and no word is built there. The consumer never waits on the UI thread.
/// </para>
/// </remarks>
public sealed partial class ActivityLog : ObservableObject
{
    /// <summary>The most lines kept.</summary>
    public const int Limit = 20_000;

    /// <summary>What the bottom of the list says once the oldest lines have gone.</summary>
    public const string DroppedText = "Older lines are not kept: the window keeps the newest 20,000.";

    private readonly IUiDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly int _limit;
    private long _next;
    private long _postedCount;

    /// <summary>Whether lines have gone because the list passed its limit.</summary>
    [ObservableProperty]
    private bool _hasDropped;

    /// <summary>Creates the log.</summary>
    /// <param name="dispatcher">The UI thread, which owns the list.</param>
    /// <param name="clock">What time it is, for the day name before a line's time.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ActivityLog(IUiDispatcher dispatcher, IClock clock)
        : this(dispatcher, clock, Limit)
    {
    }

    /// <summary>For tests: a smaller limit.</summary>
    internal ActivityLog(IUiDispatcher dispatcher, IClock clock, int limit)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        _dispatcher = dispatcher;
        _clock = clock;
        _limit = limit;
    }

    /// <summary>The lines, newest first. UI thread only.</summary>
    public ObservableCollection<ActivityLineViewModel> Lines { get; } = [];

    /// <summary>The text at the bottom of the list, or empty while every line is kept.</summary>
    public string DroppedLine => HasDropped ? DroppedText : string.Empty;

    /// <summary>How many posts the log made. Diagnostic only.</summary>
    public long PostedCount => Interlocked.Read(ref _postedCount);

    /// <summary>
    /// One record's decisions, as the consumer hands them to the archive. The consumer's thread: picks the
    /// shown ones, numbers them, and posts once; a record with no shown decision posts nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="decisions"/> is null.</exception>
    public void Decided(IReadOnlyList<Decision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        List<(long Id, Decision Decision)>? shown = null;

        foreach (var decision in decisions)
        {
            if (ActivityWords.IsShown(decision.Kind))
            {
                (shown ??= []).Add((++_next, decision));
            }
        }

        if (shown is null)
        {
            return;
        }

        Interlocked.Increment(ref _postedCount);
        _dispatcher.Post(() => Add(shown));
    }

    /// <summary>Adds the lines at the top, and lets the oldest go past the limit. UI thread only.</summary>
    private void Add(List<(long Id, Decision Decision)> shown)
    {
        var now = _clock.Now;

        foreach (var (id, decision) in shown)
        {
            if (ActivityWords.LineOf(id, decision) is not { } line)
            {
                continue;
            }

            Lines.Insert(0, new ActivityLineViewModel(line, now));
        }

        if (Lines.Count <= _limit)
        {
            return;
        }

        while (Lines.Count > _limit)
        {
            Lines.RemoveAt(Lines.Count - 1);
        }

        if (!HasDropped)
        {
            HasDropped = true;
            OnPropertyChanged(nameof(DroppedLine));
        }
    }
}
