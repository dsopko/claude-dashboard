using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The dashboard body and header (Design Document §6, §7, §9; Impl §5.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It decides nothing about attention.</strong> Banding and ordering come from
/// <see cref="AttentionEngine"/>, grouping from <see cref="GroupResolver"/>, roll-up from
/// <see cref="Group"/>. This assembles what they return into a sequence of rows and keeps that
/// sequence in step with the projection; there is no comparison, no sort and no state test in
/// this file, and any that appeared would be the domain leaking into the host.
/// </para>
/// <para>
/// <strong>What it does decide is how much room each row gets.</strong> Design Document §6's
/// collapse rules are here rather than in XAML because they change which rows exist, not how a
/// row looks — and because a rule that can hide the thing the tool exists to surface must be
/// assertable without standing up a window.
/// </para>
/// <para>
/// <strong>It reads the projection, never the Registry.</strong> The projection's collection is
/// UI-thread-owned and already marshalled (Impl §4). <c>Registry.Sessions</c> is a live view
/// whose enumeration from another thread is unsafe, and the single-writer guard covers writes
/// rather than reads — so this is the one rule in the design that nothing enforces.
/// </para>
/// <para>
/// <strong>Rows are reused, never rebuilt.</strong> T1.3's ordering and T1.4's groups both
/// compare by value over their member sequences, so an unchanged projection produces a result
/// equal to the previous one. That property only reaches the screen if the binding preserves it:
/// a clear-and-refill on every notification would raise a reset for every row several times a
/// minute, discarding selection and scroll position. So a refresh reconciles in place and
/// touches only what actually moved.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IUiTickTarget, ISoundSignTarget, IDisposable
{
    /// <summary>
    /// How long a group must be entirely quiet before it collapses to one line
    /// (Design Document §6 rule 1: "N minutes (default 15)").
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(15);

    private readonly SessionProjection _projection;
    private readonly MotionPolicy _motion;
    private readonly IAckPublisher _ack;
    private readonly IClipboard _clipboard;
    private readonly RosterStore _rosters;
    private readonly Dictionary<SessionId, SessionViewModel> _sessionRows = [];
    private readonly IRosterPersistence _persist;
    private bool _isSelecting;
    private RosterPromptViewModel? _prompt;
    private string _promptFormedAs = string.Empty;
    private readonly Dictionary<GroupKey, GroupViewModel> _groupHeaders = [];
    private readonly Dictionary<AttentionBand, BandHeaderViewModel> _bandHeaders = [];
    private readonly Dictionary<string, QuietFooterViewModel> _footers = [];

    /// <summary>
    /// The sessions in the window, and in Grouped view the group that holds each (T1.71): what a line in the
    /// Activity window asks about. Kept by <see cref="Refresh"/>.
    /// </summary>
    private readonly HashSet<SessionId> _present = [];
    private readonly Dictionary<SessionId, GroupKey> _groupOf = [];

    /// <summary>
    /// The last sound that played for each session, kept here as well as on its row (T1.67): the
    /// sound can reach this thread before the session's first row exists, and a row built later
    /// takes it. Forgotten on the first refresh after its minute.
    /// </summary>
    private readonly Dictionary<SessionId, (SoundId Sound, DateTimeOffset At)> _sounds = [];

    private DateTimeOffset _now = DateTimeOffset.MinValue;
    private TimeSpan _staleAfter = DefaultStaleAfter;
    private bool _disposed;

    /// <summary>Grouped by default (Design Document §7).</summary>
    [ObservableProperty]
    private bool _isGrouped = true;

    /// <summary>
    /// How many sessions the dashboard knows about, for the caption's "11 sessions"
    /// (design option 2c).
    /// </summary>
    /// <remarks>
    /// Every session in the projection, the quiet and the ended included — the caption says how
    /// many there are, and the bands beside it say how many are worth looking at. It is therefore
    /// the sum of the five band counts below and is derived from the same list on the same pass,
    /// so it cannot disagree with them.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionsWord))]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _sessionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _needsYouCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _workingCount;

    [ObservableProperty]
    private int _quietCount;

    [ObservableProperty]
    private int _endedCount;

    /// <summary>
    /// Whether anything on the board is waiting on the operator — the Ack-all button's light
    /// and its gate, which are deliberately one fact (issue #43).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Computed from the projection's sessions, never from <see cref="Rows"/>.</strong>
    /// Rows is the view: it holds headers and footers, and what the collapse rules hide is a
    /// property of the renderer. Today no collapse can hide an eligible session — the Unread
    /// exemption and the never-summarised bands (Design Document §6 rule 3) see to that — but
    /// this flag answers "is anything waiting", which is a fact about the sessions, and reading
    /// it off the view would make it silently depend on those rules staying exactly as they are.
    /// </para>
    /// <para>
    /// Recomputed in <see cref="RecountBands"/>, on the same pass and the same dispatcher path
    /// as every band count — so it can never disagree with the numbers beside it.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AckAllCommand))]
    private bool _anythingToAcknowledge;

    /// <summary>The word after the caption's total: " sessions", or " session" for one.</summary>
    /// <remarks>
    /// <para>
    /// Singular and plural rather than "1 sessions". The counts beside it are not worded this way
    /// — "unread" and "working" are adjectives there and take no plural — so the rule is stated
    /// once here rather than guessed from a suffix, exactly as <see cref="TrayTooltip"/> states
    /// it for the tray.
    /// </para>
    /// <para>
    /// The word alone, not "11 sessions", because the caption shows it in its own run: it is the
    /// first thing <see cref="FittingStrip"/> takes away when the slot runs short, and the number
    /// beside it stays. A single string could not be half-hidden.
    /// </para>
    /// </remarks>
    public string SessionsWord => SessionCount == 1 ? " session" : " sessions";

    /// <summary>
    /// The caption's counts in full words, for the tooltip on the strip (T1.39, issue #53):
    /// "11 sessions · 3 need you · 5 unread · 8 working".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The words the strip takes away, one hover away.</strong> At its narrowest tier
    /// before counts drop, the strip shows numbers only and lets the colours name them. That
    /// depends on the reader knowing the colours; this does not.
    /// </para>
    /// <para>
    /// Exactly the strip's long form, zeros included in the same way: the total always, a band
    /// only when it is not zero. The words are the markup's, stated again here because the markup
    /// splits them across runs a tooltip cannot reach. Raised with every count it reads.
    /// </para>
    /// </remarks>
    public string CountsText
    {
        get
        {
            var parts = new List<string>(4)
            {
                string.Create(CultureInfo.CurrentCulture, $"{SessionCount}{SessionsWord}"),
            };

            if (NeedsYouCount > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{NeedsYouCount} need you"));
            }

            if (UnreadCount > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{UnreadCount} unread"));
            }

            if (WorkingCount > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{WorkingCount} working"));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Binds to <paramref name="projection"/>.</summary>
    /// <remarks>
    /// <strong>Every collaborator here is required, and that is the point.</strong> This is the
    /// type the container resolves, and Microsoft DI honours a constructor default for a service
    /// it cannot resolve rather than throwing — so an optional parameter quietly turns a lost
    /// registration into a program that still starts and still renders and does less. That is not
    /// hypothetical: with <c>ack</c> optional, deleting one line from <c>AppHost</c> disabled every
    /// Ack button in the shipped app and left the whole suite green, because every test supplies
    /// the publisher the container was supposed to. Required parameters make the same deletion
    /// throw at startup instead — loud, immediate, and unshippable.
    /// <see cref="SessionViewModel"/> keeps its optional forms, because rows really are built
    /// standalone in tests and nothing resolves one from a container.
    /// </remarks>
    /// <param name="projection">The UI-thread mirror of the Registry.</param>
    /// <param name="motion">
    /// Whether rows may animate. <see cref="MotionPolicy.System"/> is the operator's own setting.
    /// </param>
    /// <param name="ack">
    /// Where a row sends a manual acknowledgment (Design Document §4 tier 2). Passed to every row
    /// this builds.
    /// </param>
    /// <param name="persist">
    /// Where an accepted roster is written so it survives a restart. <strong>Required, not
    /// defaulted.</strong> It is a registered service, and an optional parameter that resolves to
    /// nothing would leave every accepted roster silently unremembered — a feature that works
    /// perfectly until the operator restarts.
    /// <para>
    /// <strong>What actually protects it, measured rather than assumed:</strong> removing the
    /// registration fails <c>AppHostTests</c> twice and <c>PhaseOneAcceptanceTests</c> twice,
    /// because a required parameter makes the container throw when the service is missing.
    /// <c>ServiceCompositionTests</c> is <em>not</em> among them — it refuses an <em>optional</em>
    /// parameter that is also registered, which is the shape this deliberately is not. Knowing which
    /// test protects what is the point of the exercise, and the first version of this remark named
    /// the wrong one from memory.
    /// </para>
    /// </param>
    /// <param name="clipboard">
    /// Where a row sends the session id when it is clicked (issue #15). Required for the same
    /// reason as the others, and the composition guard is what enforced it: it is a registered
    /// service, so an optional form here would turn a deleted registration into a copy affordance
    /// that silently does nothing — the exact failure the remark above describes for <c>ack</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public MainViewModel(
        SessionProjection projection,
        MotionPolicy motion,
        IAckPublisher ack,
        IClipboard clipboard,
        RosterStore rosters,
        IRosterPersistence persist)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(ack);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(rosters);
        ArgumentNullException.ThrowIfNull(persist);

        _projection = projection;
        _motion = motion;
        _ack = ack;
        _clipboard = clipboard;
        _rosters = rosters;
        _persist = persist;
        _projection.Sessions.CollectionChanged += OnSessionsChanged;
        _motion.PropertyChanged += OnMotionChanged;

        Refresh();
    }

    /// <summary>
    /// The body: band or group headings interleaved with session rows, in display order.
    /// </summary>
    public ObservableCollection<DashboardRow> Rows { get; } = [];

    /// <summary>
    /// How long a group must be entirely quiet before it collapses (Design Document §6 rule 1).
    /// </summary>
    public TimeSpan StaleAfter
    {
        get => _staleAfter;
        set
        {
            if (_staleAfter == value)
            {
                return;
            }

            _staleAfter = value;
            OnPropertyChanged(nameof(StaleAfter));
            Refresh();
        }
    }

    /// <summary>Stops following the projection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _projection.Sessions.CollectionChanged -= OnSessionsChanged;
        _motion.PropertyChanged -= OnMotionChanged;

        foreach (var header in _groupHeaders.Values)
        {
            header.PropertyChanged -= OnHeaderChanged;
        }

        foreach (var header in _bandHeaders.Values)
        {
            header.PropertyChanged -= OnHeaderChanged;
        }

        _disposed = true;
    }

    /// <summary>
    /// Advances the display to <paramref name="now"/>: every row's age, and every group's idle
    /// time — which is what decides whether it has gone stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An age has to move without an event arriving — a session blocked for nine minutes must
    /// read nine, then ten — and so does staleness, which is the same clock asking a different
    /// question. Nothing here starts a timer to make that happen: the event consumer owns the
    /// only periodic loop in the process (T1.9), deliberately, and a second one is exactly what
    /// that arrangement exists to prevent. <see cref="UiTick"/> is what carries that loop's tick
    /// across to this method on the UI thread.
    /// </para>
    /// <para>
    /// Every cached row is aged, not merely the visible ones: a row hidden behind a "+ 3 quiet"
    /// footer keeps its instance, and would otherwise reappear showing the age it had when it
    /// was collapsed.
    /// </para>
    /// </remarks>
    public void Tick(DateTimeOffset now)
    {
        _now = now;

        foreach (var row in _sessionRows.Values)
        {
            row.RefreshAge(now);
        }

        foreach (var gone in _sounds.Where(pair => now - pair.Value.At >= SessionViewModel.SoundSignFor).Select(pair => pair.Key).ToList())
        {
            _sounds.Remove(gone);
        }

        Refresh();
    }

    /// <summary>
    /// A sound played for <paramref name="session"/>: its row shows the speaker sign for a minute
    /// (T1.67, issue #99). Called on the UI thread, by <see cref="SoundSigns"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The session is the engine's answer, already decided.</strong> For a group's sound it is
    /// the member whose finish settled the group, so the sign is on that member's row and never on
    /// the group heading: a heading is not a session and is never marked here.
    /// </para>
    /// <para>
    /// A sound that plays again replaces the last one, so a reminder starts the minute again. The row
    /// order does not change, and nothing is written anywhere.
    /// </para>
    /// </remarks>
    public void SoundPlayed(SessionId session, SoundId sound, DateTimeOffset at)
    {
        _sounds[session] = (sound, at);

        if (_sessionRows.TryGetValue(session, out var row))
        {
            row.ShowSound(sound, at);
        }
    }

    // ---- Forming and editing a roster (T1.26, issue #16) --------------------------------------

    /// <summary>
    /// The smallest group the operator can form.
    /// </summary>
    /// <remarks>
    /// <strong>A group of one is not a group.</strong> It would gain the settle window and the done
    /// suppression, so a single session's finished chime would be delayed for no benefit — and that
    /// chime is the thing this product exists to deliver. Rules 4 and 6 can still <em>reduce</em> a
    /// roster to one member, and such a group renders normally; this is only about forming one.
    /// </remarks>
    public const int SmallestGroup = 2;

    /// <summary>Whether the window is in selection mode (Design Document §9).</summary>
    /// <remarks>
    /// The mode announces itself in the header, which is the whole answer to "a state can be
    /// wrong": a mode that can be on unseen is a mode nobody knows is on. It ends on grouping, on
    /// cancelling and when the window is hidden, and it is never persisted.
    /// </remarks>
    public bool IsSelecting
    {
        get => _isSelecting;
        set
        {
            if (_isSelecting == value)
            {
                return;
            }

            _isSelecting = value;

            foreach (var row in _sessionRows.Values)
            {
                row.IsSelecting = value;
            }

            OnPropertyChanged(nameof(IsSelecting));
            RaiseSelection();
        }
    }

    /// <summary>How many rows the operator has ticked.</summary>
    public int SelectedCount => _sessionRows.Values.Count(row => row.IsSelected);

    /// <summary>What the header says while selecting.</summary>
    public string SelectionText => $"Selecting · {SelectedCount} chosen";

    /// <summary>
    /// Acknowledges every session that is waiting on the operator (issue #43).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>N clicks of Ack is N acks; so is one click of this.</strong> One
    /// <see cref="IAckPublisher.Acknowledge"/> per eligible session, through the same publisher
    /// every row uses — no new event type, and the Registry stays the one writer, applying each
    /// ack as the idempotent, timestamp-guarded transition it already is.
    /// </para>
    /// <para>
    /// <strong>The body re-checks each session at the moment of the click</strong> rather than
    /// trusting <c>CanExecute</c>, for the reason <see cref="SessionViewModel"/>'s own command
    /// gives: a binding will not invoke a disabled command, but anything holding the command
    /// object can. And the re-check is per session, not per click — the flag says <em>some</em>
    /// session is eligible, and publishing for the rest would be publishing acks the Registry
    /// will decline. That is not free: the channel is bounded. An ack is never noise, so a full
    /// queue does not shed it (T1.58), but every ack is queued work, and at the hard limit
    /// (<see cref="Pipeline.EventPipeline.HardLimitFactor"/> times
    /// <see cref="Pipeline.EventPipeline.DefaultCapacity"/>, 16,384) the channel drops its
    /// <em>oldest</em> entry (Impl §4). Eligible-only publishing keeps one click at exactly one ack
    /// per waiting session; only more than 16,384 sessions waiting at once could make the click
    /// itself reach that limit, a scale four orders past the dashboard's world, accepted rather
    /// than defended against.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanAckAll))]
    private void AckAll()
    {
        foreach (var session in _projection.Sessions.ToList())
        {
            if (Acknowledgment.Applies(session.State))
            {
                _ack.Acknowledge(session);
            }
        }
    }

    private bool CanAckAll() => AnythingToAcknowledge;

    /// <summary>Enters selection mode.</summary>
    [RelayCommand]
    private void BeginSelection() => IsSelecting = true;

    /// <summary>Leaves selection mode, dropping every tick.</summary>
    [RelayCommand]
    private void CancelSelection() => IsSelecting = false;

    /// <summary>
    /// Forms a group from the ticked rows, and asks whether to remember it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The group exists immediately (rule 1) because the store is updated now; remembering it is a
    /// separate question (rule 2) because only the settings file makes it survive a restart.
    /// </para>
    /// <para>
    /// <strong>Rules 4 and 6 are the store's and are not repeated here.</strong> A ticked name that
    /// already belongs to another roster leaves it, and a roster emptied by that ceases to exist —
    /// this method calls <see cref="RosterBook.With"/> and renders whatever comes back, so the other
    /// group losing a member is visible on the very next refresh rather than being hidden.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanGroupSelected))]
    private void GroupSelected()
    {
        var members = _sessionRows.Values
            .Where(row => row.IsSelected && row.CanSelect)
            .Select(row => row.Session.Title!)
            .ToList();

        if (members.Count < SmallestGroup)
        {
            return;
        }

        var name = FreeRosterName();

        _rosters.Replace(_rosters.Book.With(name, members));

        IsSelecting = false;
        _promptFormedAs = name;
        _prompt = new RosterPromptViewModel(name, RememberRoster, DeclineRoster);

        Refresh();
    }

    private bool CanGroupSelected() => SelectedCount >= SmallestGroup;

    /// <summary>
    /// Removes a session's name from its roster (issue #16 rule 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Removal is by NAME, so one click can move two rows.</strong> Two live sessions can
    /// share a rostered name and both join; removing the name removes both. That is #16's accepted
    /// consequence and it is deliberately not special-cased away — the second row moving is correct,
    /// and hiding it would be the UI lying about what the store did.
    /// </para>
    /// <para>
    /// The session is not removed from the dashboard. It returns to its workspace group, which is
    /// why the menu item says "Remove from group".
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveFromGroup))]
    private void RemoveFromGroup(SessionViewModel? row)
    {
        if (row?.Session.Title is not { } title || _rosters.Book.RosterFor(title) is null)
        {
            return;
        }

        _rosters.Replace(_rosters.Book.Without(title));
        Refresh();
    }

    private bool CanRemoveFromGroup(SessionViewModel? row) =>
        row?.Session.Title is { } title && _rosters.Book.RosterFor(title) is not null;

    /// <summary>Writes the roster to settings, so it re-forms on the next start.</summary>
    private void RememberRoster(string name)
    {
        // Looked up by the name the group was FORMED under, not by the one being asked for: the
        // operator may have renamed it in the prompt, and looking it up by the new name would find
        // nothing and silently remember the group under its default label.
        var formed = _rosters.Book.Rosters
            .FirstOrDefault(roster => string.Equals(roster.Name, _promptFormedAs, StringComparison.Ordinal));

        if (formed is not null && !string.Equals(formed.Name, name, StringComparison.Ordinal))
        {
            _rosters.Replace(_rosters.Book.With(name, formed.Members));
        }

        _persist.Remember(_rosters.Book);
        _prompt = null;
        Refresh();
    }

    /// <summary>
    /// Leaves the group formed and unpersisted, which is also what happens if the prompt is ignored.
    /// </summary>
    private void DeclineRoster()
    {
        _prompt = null;
        Refresh();
    }

    /// <summary>The first unused default label. The operator may rename it in the prompt.</summary>
    /// <remarks>
    /// Deliberately not derived from the members' titles. A title can be a model-written summary of
    /// the operator's prompt (T1.24), and a group heading is one more place it would then appear.
    /// </remarks>
    private string FreeRosterName()
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? "Group" : $"Group {n}";

            if (!_rosters.Book.Rosters.Any(roster => string.Equals(roster.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
    }

    /// <summary>Keeps the header's count and the group action in step with the ticks.</summary>
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsSelected))
        {
            RaiseSelection();
        }
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        GroupSelectedCommand.NotifyCanExecuteChanged();
    }
    /// <summary>Rebuilds the row sequence from the projection, reusing every row it can.</summary>
    public void Refresh()
    {
        var sessions = _projection.Sessions.ToList();

        Restate(sessions);

        var groups = IsGrouped ? GroupResolver.Resolve(sessions, _rosters.Book) : null;

        var rows = groups is null ? FlatRows(sessions) : GroupedRows(groups);

        if (_prompt is not null)
        {
            // Above everything, because it is about the group that was just made and the operator
            // has to be able to find it. It is not modal: they may ignore it, and ignoring it is
            // declining.
            rows.Insert(0, _prompt);
        }

        Reconcile(rows);
        Forget(sessions, groups?.Select(group => group.Key).ToHashSet());
        RecountBands(sessions);
        Remember(sessions, groups);
    }

    /// <summary>
    /// The window's sessions, or its groups, changed (T1.71): the Activity window's lines ask again whether
    /// their rows are here. Raised by a refresh that changed them, never by one that did not.
    /// </summary>
    public event EventHandler? PresenceChanged;

    /// <summary>"Show activity" on a row (T1.71): the Activity window lists only that session's lines.</summary>
    public event EventHandler<ActivityRequest>? ActivityRequested;

    /// <summary>Whether a line's session, or for a group's line its group, is in the window (T1.71).</summary>
    /// <remarks>
    /// A group is in the window only in Grouped view, where it has a heading. A session is in the window whether
    /// or not its row is folded away, because a click unfolds it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    public bool Has(ActivityLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.SessionId is { } id)
        {
            return _present.Contains(new SessionId(id));
        }

        return line.Group is { } group && IsGrouped && _groupHeaders.ContainsKey(group);
    }

    /// <summary>
    /// Makes a line's row a row on screen, and opens it (T1.71): what a click on an Activity line asks of the
    /// window. Returns the row to scroll to, or null when the line's session or group is not here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A folded row is unfolded the way a click unfolds it today:</strong> its group's heading, or in
    /// Flat view its band's (the Ended line, the Quiet line), is set expanded, which is what a click on the stale
    /// line, on "+ 3 quiet" or on the band's summary does. The refresh that follows puts the row in place.
    /// </para>
    /// <para>
    /// <strong>Opened as a click on the row opens it, and nothing more:</strong> no Ack, no mute, no event.
    /// <strong>Not in selection mode:</strong> there a click on a row selects it, and setting
    /// <see cref="SessionViewModel.IsExpanded"/> would toggle the selection, so the row is only brought into view.
    /// </para>
    /// <para>
    /// A group's line returns its heading, in Grouped view, unopened: a heading has nothing to open.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    public DashboardRow? Reveal(ActivityLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.SessionId is null)
        {
            return line.Group is { } group && IsGrouped && _groupHeaders.TryGetValue(group, out var heading)
                ? heading
                : null;
        }

        var id = new SessionId(line.SessionId);

        if (!_present.Contains(id))
        {
            return null;
        }

        if (!_sessionRows.TryGetValue(id, out var row) || !Rows.Contains(row))
        {
            Unfold(id);
        }

        if (!_sessionRows.TryGetValue(id, out row) || !Rows.Contains(row))
        {
            return null;
        }

        if (!IsSelecting)
        {
            row.IsExpanded = true;
        }

        return row;
    }

    /// <summary>Opens what holds a session's row: its group's heading, or in Flat view its band's.</summary>
    private void Unfold(SessionId id)
    {
        if (IsGrouped)
        {
            if (_groupOf.TryGetValue(id, out var key) && _groupHeaders.TryGetValue(key, out var heading))
            {
                heading.IsExpanded = true;
            }

            return;
        }

        var session = _projection.Sessions.FirstOrDefault(candidate => candidate.Id == id);

        if (session is not null
            && _bandHeaders.TryGetValue(AttentionOrder.BandOf(session.State), out var band)
            && band.IsCollapsible)
        {
            band.IsExpanded = true;
        }
    }

    /// <summary>Keeps the sessions in the window and their groups, and says when they changed.</summary>
    private void Remember(List<Session> sessions, IReadOnlyList<Group>? groups)
    {
        var present = sessions.Select(session => session.Id).ToHashSet();
        var groupOf = groups?
            .SelectMany(group => group.Members.Select(member => (member.Id, group.Key)))
            .ToDictionary(pair => pair.Id, pair => pair.Key)
            ?? [];

        if (present.SetEquals(_present)
            && groupOf.Count == _groupOf.Count
            && groupOf.All(pair => _groupOf.TryGetValue(pair.Key, out var key) && key == pair.Value))
        {
            return;
        }

        _present.Clear();
        _present.UnionWith(present);
        _groupOf.Clear();

        foreach (var (id, key) in groupOf)
        {
            _groupOf[id] = key;
        }

        PresenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnActivityRequested(object? sender, EventArgs e)
    {
        if (sender is SessionViewModel row && !row.Id.IsEmpty)
        {
            ActivityRequested?.Invoke(this, new ActivityRequest(row.Id, row.ActivityName));
        }
    }

    /// <summary>
    /// Gives every row this view model still holds the session it stands for — collapsed rows
    /// included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Collapsing hides a row; it must not stop updating it.</strong> Assembling the
    /// sequence only touches the rows that end up in it, so before this existed a row behind a
    /// "+ 3 quiet" footer kept the record it had when it was collapsed — and since the act of
    /// being acknowledged is what collapses a row, that meant the ack itself was the update the
    /// row missed. It corrected itself when the footer was opened, which is exactly the kind of
    /// bug that hides: invisible while invisible.
    /// </para>
    /// <para>
    /// Idempotent against the assembly that follows, which sets the same records again;
    /// <see cref="SessionViewModel.Session"/> compares by value, so the second write raises
    /// nothing.
    /// </para>
    /// </remarks>
    private void Restate(List<Session> sessions)
    {
        foreach (var session in sessions)
        {
            if (_sessionRows.TryGetValue(session.Id, out var row))
            {
                row.Session = session;
                row.IsSelecting = _isSelecting;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="session"/> has been dealt with, and so may be collapsed
    /// (Design Document §6 rules 2 and 3).
    /// </summary>
    /// <remarks>
    /// <strong>The Unread exemption is this one line.</strong> "Quiet" is the Quiet and Ended
    /// bands and nothing else, so an Unread session can never be summarised away — §6 rule 3
    /// replaced the earlier "show only the first green per group" idea precisely because that
    /// rule would have hidden the thing the tool exists to surface. The bands come from Core;
    /// this asks a question of them rather than restating what they contain.
    /// </remarks>
    private static bool IsQuiet(Session session) =>
        AttentionOrder.BandOf(session.State) is AttentionBand.Quiet or AttentionBand.Ended;

    /// <summary>Grouped view: a heading per group, its members in attention order beneath it.</summary>
    private List<DashboardRow> GroupedRows(IReadOnlyList<Group> resolved)
    {
        var rows = new List<DashboardRow>();

        foreach (var group in AttentionEngine.OrderGroups(resolved, group => RosterSettle.StateOf(group, _now)))
        {
            var header = HeaderFor(group);

            header.WorstState = RosterSettle.StateOf(group, _now);
            header.IdleAge = _now - group.LastActivity;
            header.IsStale = group.Members.All(IsQuiet) && header.IdleAge >= StaleAfter;

            rows.Add(header);

            // Placement is where a row learns it sits in a roster (T1.36) — the one pass that
            // knows where every row is going on every rebuild, so a session moving into or out
            // of a roster is retold in the same breath.
            var isRoster = GroupKeys.KindOf(group.Key) == GroupKeyKind.Roster;

            if (header.IsExpanded)
            {
                // Everything, quiet included: the operator asked.
                foreach (var member in group.Members)
                {
                    var row = RowFor(member);
                    row.IsRosterMember = isRoster;
                    rows.Add(row);
                }

                continue;
            }

            if (header.IsStale)
            {
                // Rule 1: the whole group is one line. It cannot push active work down, because
                // Core sorts a group by its worst member and every member here is quiet.
                continue;
            }

            // Rule 2: the live rows in full, the dealt-with ones behind a footer.
            var quiet = 0;
            foreach (var member in group.Members)
            {
                if (IsQuiet(member))
                {
                    quiet++;
                }
                else
                {
                    var row = RowFor(member);
                    row.IsRosterMember = isRoster;
                    rows.Add(row);
                }
            }

            if (quiet > 0)
            {
                rows.Add(FooterFor(header, group.Key.Value, quiet, isBandSummary: false));
            }
        }

        return rows;
    }

    /// <summary>Flat view: global bands, visibly labelled (Design Document §7).</summary>
    private List<DashboardRow> FlatRows(List<Session> sessions)
    {
        var rows = new List<DashboardRow>();

        foreach (var band in AttentionEngine.Order(sessions))
        {
            var header = HeaderFor(band.Band, band.Sessions.Count);
            rows.Add(header);

            if (header.IsCollapsible && !header.IsExpanded)
            {
                // The mockups' flat view: the Quiet band is one line, never a list of grey rows.
                rows.Add(FooterFor(header, band.Band.ToString(), band.Sessions.Count, isBandSummary: true));
                continue;
            }

            // Flat view has no rosters, so every row keeps its own Ack (T1.36) — retold here for
            // the session that was a roster member the last time Grouped assembled it.
            foreach (var session in band.Sessions)
            {
                var row = RowFor(session);
                row.IsRosterMember = false;
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Brings <see cref="Rows"/> into line with <paramref name="desired"/>, changing only what
    /// differs.
    /// </summary>
    /// <remarks>
    /// Rows are reused by identity, so an unchanged projection yields the same instances in the
    /// same order and this raises nothing at all — which is the churn-free property reaching the
    /// screen rather than stopping at Core's doorstep.
    /// </remarks>
    private void Reconcile(List<DashboardRow> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= Rows.Count)
            {
                Rows.Add(desired[i]);
            }
            else if (!ReferenceEquals(Rows[i], desired[i]))
            {
                if (Rows[i].GetType() == desired[i].GetType())
                {
                    Rows[i] = desired[i];
                }
                else
                {
                    // A row of another kind takes the place out and in again, not by a replace (T1.71): a
                    // replace reuses the container, and the old kind's template is bound once to the new row
                    // before WPF changes the template, which writes a binding error for each binding (issue #23
                    // names the mechanism). Unfolding a group from the Activity window puts a session row where
                    // its "+ 1 quiet" line was.
                    Rows.RemoveAt(i);
                    Rows.Insert(i, desired[i]);
                }
            }
        }

        while (Rows.Count > desired.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    /// <summary>
    /// Drops cached rows for sessions and groups that no longer exist.
    /// </summary>
    /// <remarks>
    /// Keyed on what the projection holds rather than on what is currently on screen: a row
    /// hidden behind a quiet footer is still that session's row, and forgetting it would rebuild
    /// it — losing its expansion — the moment the footer was opened.
    /// </remarks>
    private void Forget(List<Session> sessions, HashSet<GroupKey>? liveGroups)
    {
        var liveSessions = sessions.Select(session => session.Id).ToHashSet();
        foreach (var gone in _sessionRows.Keys.Where(id => !liveSessions.Contains(id)).ToList())
        {
            _sessionRows[gone].PropertyChanged -= OnRowChanged;
            _sessionRows[gone].ActivityRequested -= OnActivityRequested;
            _sessionRows.Remove(gone);
        }

        if (liveGroups is null)
        {
            // Flat view resolves no groups. Leaving the headers cached is what makes toggling
            // back to grouped view keep whatever the operator had expanded.
            return;
        }

        foreach (var gone in _groupHeaders.Keys.Where(key => !liveGroups.Contains(key)).ToList())
        {
            _groupHeaders[gone].PropertyChanged -= OnHeaderChanged;
            _groupHeaders.Remove(gone);
            _footers.Remove(gone.Value);
        }
    }

    /// <summary>The counts strip (Design Document §9).</summary>
    /// <remarks>
    /// Derived on every refresh rather than maintained incrementally. Incremental counts are a
    /// cache of something already in hand, and a cache that can disagree with the collection
    /// beside it is worse than a loop over fifteen items. They count sessions, not rows, so a
    /// collapsed group still reports what is inside it.
    /// </remarks>
    private void RecountBands(List<Session> sessions)
    {
        var byBand = sessions.CountBy(session => AttentionOrder.BandOf(session.State))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        SessionCount = sessions.Count;
        NeedsYouCount = byBand.GetValueOrDefault(AttentionBand.NeedsYou);
        UnreadCount = byBand.GetValueOrDefault(AttentionBand.Unread);
        WorkingCount = byBand.GetValueOrDefault(AttentionBand.Working);
        QuietCount = byBand.GetValueOrDefault(AttentionBand.Quiet);
        EndedCount = byBand.GetValueOrDefault(AttentionBand.Ended);

        // Asked of the domain, not derived from the band counts above: which states an ack
        // applies to is Acknowledgment.Applies's knowledge, and a copy phrased in bands would be
        // the drift SessionViewModel.CanAcknowledge's remark warns about.
        AnythingToAcknowledge = sessions.Any(session => Acknowledgment.Applies(session.State));
    }

    private SessionViewModel RowFor(Session session)
    {
        if (_sessionRows.TryGetValue(session.Id, out var existing))
        {
            existing.Session = session;
            return existing;
        }

        var created = new SessionViewModel(session, _motion, _ack, _clipboard) { IsSelecting = _isSelecting };
        created.RefreshAge(_now > session.LastActivity ? _now : session.LastActivity);

        if (_sounds.TryGetValue(session.Id, out var played))
        {
            created.ShowSound(played.Sound, played.At);
        }

        created.PropertyChanged += OnRowChanged;
        created.ActivityRequested += OnActivityRequested;
        _sessionRows[session.Id] = created;
        return created;
    }

    private GroupViewModel HeaderFor(Group group)
    {
        if (_groupHeaders.TryGetValue(group.Key, out var existing))
        {
            existing.Group = group;
            return existing;
        }

        var created = new GroupViewModel(group, _ack);
        created.PropertyChanged += OnHeaderChanged;
        _groupHeaders[group.Key] = created;
        return created;
    }

    private BandHeaderViewModel HeaderFor(AttentionBand band, int count)
    {
        if (!_bandHeaders.TryGetValue(band, out var existing))
        {
            existing = new BandHeaderViewModel(band);
            existing.PropertyChanged += OnHeaderChanged;
            _bandHeaders[band] = existing;
        }

        existing.Count = count;
        return existing;
    }

    private QuietFooterViewModel FooterFor(DashboardRow owner, string key, int count, bool isBandSummary)
    {
        if (!_footers.TryGetValue(key, out var existing) || !ReferenceEquals(existing.Owner, owner))
        {
            existing = new QuietFooterViewModel(owner, key, isBandSummary);
            _footers[key] = existing;
        }

        existing.Count = count;
        return existing;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    /// <summary>
    /// A heading was expanded or collapsed, which changes which rows exist.
    /// </summary>
    /// <remarks>
    /// The toggle is bound straight to the heading, so the row sequence has to follow it. Only
    /// <c>IsExpanded</c> re-runs the assembly: the other properties on a heading are things this
    /// method just finished setting, and reacting to those would recurse.
    /// </remarks>
    private void OnHeaderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GroupViewModel.IsExpanded))
        {
            Refresh();
        }
    }

    /// <summary>The reduced-motion setting changed; every row has to re-read it.</summary>
    private void OnMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(MotionPolicy.IsMotionAllowed)))
        {
            return;
        }

        foreach (var row in _sessionRows.Values)
        {
            row.RefreshMotion();
        }
    }

    /// <summary>
    /// Re-projects the same data (Impl §5.5). There is no second collection: the toggle changes
    /// which headings are built, and the rows themselves are the same instances.
    /// </summary>
    partial void OnIsGroupedChanged(bool value) => Refresh();
}
