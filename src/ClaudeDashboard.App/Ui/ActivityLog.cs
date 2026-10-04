using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
/// <strong>Between <see cref="KeepAfterTrim"/> and <see cref="Limit"/> of the newest lines are kept.</strong>
/// When the list passes the limit, the oldest lines go in one step, down to <see cref="KeepAfterTrim"/>, and
/// <see cref="HasDropped"/> says so at the bottom of the list. In one step because removing a line from a list
/// that has been laid out costs a layout: one line at a time, every new line at the limit cost several
/// milliseconds of the UI thread (the T1.70 review). A safety net for a dashboard that runs for weeks: a normal
/// three weeks is a few thousand lines.
/// </para>
/// <para>
/// <strong>A record's sound lines go on top of its other lines</strong> (the director's ruling on the T1.70
/// review): a <c>♪</c> line, and a "no sound" line too, sits directly above the state change that caused it,
/// which answers "what made that sound?". The order between records does not change.
/// </para>
/// <para>
/// <strong>The day name follows the day.</strong> A line's time is read again when the local date changes, on
/// the consumer's tick (<see cref="Tick"/>), so yesterday's line reads "Sun 23:59". No timer.
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

    /// <summary>How many of the newest lines are left after a trim.</summary>
    public const int KeepAfterTrim = 19_000;

    /// <summary>What the bottom of the list says once the oldest lines have gone.</summary>
    public const string DroppedText = "Older lines are not kept: the window keeps between 19,000 and 20,000 of the newest.";

    private readonly IUiDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly int _limit;
    private readonly int _keep;
    private DateTime _today;
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
        : this(dispatcher, clock, Limit, KeepAfterTrim)
    {
    }

    /// <summary>For tests: a smaller limit, and what a trim leaves.</summary>
    internal ActivityLog(IUiDispatcher dispatcher, IClock clock, int limit, int keep)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keep, limit);

        _dispatcher = dispatcher;
        _clock = clock;
        _limit = limit;
        _keep = keep;
        _today = clock.Now.ToLocalTime().Date;
    }

    /// <summary>The lines, newest first. UI thread only.</summary>
    public ActivityLines Lines { get; } = [];

    /// <summary>The text at the bottom of the list, or empty while every line is kept.</summary>
    public string DroppedLine => HasDropped ? DroppedText : string.Empty;

    /// <summary>How many times the oldest lines were trimmed. Diagnostic only.</summary>
    public int TrimCount { get; private set; }

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

        List<Decision>? picked = null;

        foreach (var decision in decisions)
        {
            if (ActivityWords.IsShown(decision.Kind))
            {
                (picked ??= []).Add(decision);
            }
        }

        if (picked is null)
        {
            return;
        }

        // The record's sound lines last, so that they go on top: each sits above the change that caused it.
        // Numbered in that order, so the ids still run from the top line down.
        var shown = picked
            .Where(decision => !ActivityWords.IsSound(decision.Kind))
            .Concat(picked.Where(decision => ActivityWords.IsSound(decision.Kind)))
            .Select(decision => (++_next, decision))
            .ToList();

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

        // In one step, down to what a trim leaves, with one notification: the costly part comes once in many
        // lines, and costs one rebuild of the rows in view rather than one change for each line removed.
        Lines.TrimTo(_keep);

        TrimCount++;

        if (!HasDropped)
        {
            HasDropped = true;
            OnPropertyChanged(nameof(DroppedLine));
        }
    }
    /// <summary>
    /// The clock moved (the consumer's tick, through the window's view model). When the local date has changed,
    /// every line reads its time again, so a line from yesterday reads "Sun 23:59". UI thread only.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;

        if (today == _today)
        {
            return;
        }

        _today = today;

        foreach (var line in Lines)
        {
            line.Reread(now);
        }
    }
}

/// <summary>
/// The Activity window's lines (T1.70): an observable list that can drop its oldest lines in one step.
/// </summary>
/// <remarks>
/// Removing the oldest lines one by one raised one change for each, and a shown list handled each one: a trim of
/// 1,000 lines then cost seconds of the UI thread (measured in the T1.70 fix cycle). <see cref="TrimTo"/> drops
/// them from the end and raises a single reset, after which the list rebuilds the rows in view only.
/// </remarks>
public sealed class ActivityLines : ObservableCollection<ActivityLineViewModel>
{
    /// <summary>Drops the oldest lines, at the end, until <paramref name="keep"/> are left, with one reset.</summary>
    internal void TrimTo(int keep)
    {
        if (Count <= keep)
        {
            return;
        }

        CheckReentrancy();

        while (Items.Count > keep)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
