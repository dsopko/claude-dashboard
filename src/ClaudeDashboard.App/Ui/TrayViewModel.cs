using System.Collections.Specialized;
using System.ComponentModel;

using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The always-ambient status glyph and its menu (Design §9; Impl §5.1, §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the glyph says and what it does not.</strong> The colour is a roll-up of every
/// session, coarsened from <see cref="AttentionOrder.Rank"/> — five colours for eight states,
/// order preserved (see <see cref="TrayVisuals"/>). It carries no digits: 16px cannot render
/// legible ones, so the counts live in the tooltip, where the Error-and-Question merge is also
/// undone. It never animates; TS reserves motion for needs-you rows inside the window.
/// </para>
/// <para>
/// <strong>Why it ticks.</strong> A global mute lapses by predicate and raises no event, so
/// nothing would tell the tray that "muted 24 min" has become "muted 23 min", or that a mute is
/// over. <see cref="Tick"/> recomputes on the clock, the same mechanism that advances a row's
/// age (T1.11), and it is the reason the tooltip cannot sit there claiming a mute that has
/// expired.
/// </para>
/// <para>
/// <strong>It cannot mutate the domain.</strong> Menu clicks arrive on the Dispatcher, so every
/// global mode change is published as a <see cref="SoundCommand"/> and applied on the consumer
/// thread. This type is handed <see cref="ISoundModeReader"/> — a surface with no setters — so
/// there is nothing here to call by mistake.
/// </para>
/// </remarks>
public sealed partial class TrayViewModel : ObservableObject, IUiTickTarget, IDisposable
{
    /// <summary>How long "Mute all for 30 min" mutes for (Impl §5.2).</summary>
    public static readonly TimeSpan TimedMuteDuration = TimeSpan.FromMinutes(30);

    private readonly SessionProjection _projection;
    private readonly ISoundModeReader _modes;
    private readonly IEventSink _sink;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Pipeline.IDecisionLog? _decisions;
    private readonly NoticeBoard _notices;
    private readonly Ingress.HookHealth? _health;
    private readonly bool _ownsNotices;

    private DateTimeOffset _now;
    private bool _disposed;

    /// <summary>The glyph.</summary>
    [ObservableProperty]
    private System.Drawing.Icon _icon = TrayIcons.For(TrayColour.Grey);

    /// <summary>The sentence under the glyph.</summary>
    [ObservableProperty]
    private string _tooltip = TrayTooltip.AllQuiet;

    /// <summary>The roll-up colour, before pause is applied. Exposed for assertions.</summary>
    [ObservableProperty]
    private TrayColour _colour = TrayColour.Grey;

    /// <summary>Whether monitoring is off duty.</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Whether a global mute is in force.</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>Creates the tray.</summary>
    /// <param name="projection">The UI-thread mirror of the Registry — never the Registry.</param>
    /// <param name="modes">Read-only access to the global sound modes.</param>
    /// <param name="sink">Where mode changes are published, to travel the Channel.</param>
    /// <param name="clock">The clock a timed mute's expiry is computed from.</param>
    /// <param name="ingress">
    /// Whether hooks can reach this process at all (T1.15). Required rather than optional: a
    /// dashboard that cannot hear anything looks exactly like a quiet afternoon, so the one
    /// place that says otherwise must not be able to go missing.
    /// </param>
    /// <param name="logger">Where a refused publish is recorded.</param>
    /// <param name="notices">
    /// What the operator must see on screen, not only in the log: the hook route (the ruling of
    /// 2026-10-01) and the history (T1.54, issue #71). Their short forms lead the tooltip, after the
    /// ingress fault, and the window shows their texts through <see cref="NoticeTexts"/>. The host
    /// passes it; a test that is not about it may omit it.
    /// </param>
    /// <param name="health">
    /// When the dashboard last heard from Claude Code (T1.61, issue #74): the tooltip's last item,
    /// always, in the product. The host passes it; a test that is not about it may omit it, and its
    /// tooltip then has no such item.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public TrayViewModel(
        SessionProjection projection,
        ISoundModeReader modes,
        IEventSink sink,
        IClock clock,
        IngressStatus ingress,
        ILogger logger,
        Pipeline.IDecisionLog? decisions = null,
        NoticeBoard? notices = null,
        Ingress.HookHealth? health = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(logger);

        _projection = projection;
        _modes = modes;
        _sink = sink;
        _clock = clock;
        _logger = logger;
        _decisions = decisions;
        _health = health;
        // The ingress fault reaches the tooltip through the board and nowhere else (T1.57): the host's
        // board has it as its first source. A tray given no board makes one that holds it, so a fault
        // cannot go missing for want of a board.
        _notices = notices ?? new NoticeBoard(ingress);
        _ownsNotices = notices is null;
        _notices.PropertyChanged += OnNoticeChanged;
        _now = clock.Now;

        _projection.Sessions.CollectionChanged += OnSessionsChanged;

        Refresh();
    }

    /// <summary>Raised when the operator asks for the window. The host shows or hides it.</summary>
    /// <remarks>
    /// An event rather than a window reference: a view model that could call <c>Show</c> would
    /// be holding a <see cref="System.Windows.Window"/>, and the tray is constructed before the
    /// window is shown.
    /// </remarks>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when the operator chooses Quit. The host shuts the application down.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>
    /// Raised when the operator chooses Settings (issue #36). The host opens the Settings window, or
    /// brings the open one forward. An event for the reason <see cref="OpenRequested"/> is one.
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Raised when the operator asks for the Activity window (T1.70, issue #97), from the tray menu or
    /// the main window's toolbar, which share this command. The host opens the window, or brings the
    /// open one to the front.
    /// </summary>
    public event EventHandler? ActivityRequested;

    /// <summary>What the mute menu item reads (Impl §5.2: the item toggles).</summary>
    public string MuteAllLabel => IsMuted ? "Unmute all" : "Mute all";

    /// <summary>
    /// The texts the window's notice row shows, one line each, in the board's fixed order (T1.54).
    /// Empty when there is nothing to say. The window binds its notice row to this.
    /// </summary>
    public IReadOnlyList<string> NoticeTexts => _notices.Texts;

    /// <summary>
    /// The same texts as one string, a line each, or null when there is nothing to say: the
    /// notice row as it reads, for a reader that is not the window.
    /// </summary>
    public string? NoticeText => NoticeTexts.Count == 0 ? null : string.Join(Environment.NewLine, NoticeTexts);

    /// <summary>Whether the window shows its notice row.</summary>
    public bool HasNotice => _notices.HasAny;

    /// <summary>What the pause menu item reads. It toggles rather than adding a second item.</summary>
    public string PauseLabel => IsPaused ? "Resume monitoring" : "Pause monitoring";

    /// <summary>How many mode changes have been published. Diagnostic only; UI thread only.</summary>
    internal long PublishedCount { get; private set; }

    /// <summary>Toggles the dashboard window.</summary>
    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Silences everything, or lets it be heard again.</summary>
    [RelayCommand]
    private void MuteAll() =>
        Publish(IsMuted ? SoundCommandKind.UnmuteAll : SoundCommandKind.MuteAll);

    /// <summary>Silences everything for half an hour.</summary>
    /// <remarks>
    /// The expiry is computed here and carried on the command, rather than the engine adding
    /// thirty minutes when it arrives. One clock stamps everything, so the tooltip's countdown
    /// and the predicate that ends the mute are measured against the same instant.
    /// </remarks>
    [RelayCommand]
    private void MuteAllForThirtyMinutes() =>
        Publish(SoundCommandKind.MuteAll, _clock.Now + TimedMuteDuration);

    /// <summary>Goes off duty, or comes back on.</summary>
    [RelayCommand]
    private void TogglePause() =>
        Publish(IsPaused ? SoundCommandKind.ResumeMonitoring : SoundCommandKind.PauseMonitoring);

    /// <summary>Opens the Settings window (issue #36).</summary>
    /// <remarks>
    /// Enabled since issue #36 gave the window its first setting: start with Windows. Phase 6's
    /// settings (T6.1) grow the same window.
    /// </remarks>
    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks for the Activity window (T1.70).</summary>
    [RelayCommand]
    private void OpenActivity() => ActivityRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Ends the process.</summary>
    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    /// <remarks>
    /// The clock moving is a reason to re-render even though nothing happened: a mute's
    /// remaining minutes change, and a mute that has lapsed stops being one.
    /// </remarks>
    public void Tick(DateTimeOffset now)
    {
        _now = now;

        // The notices first: a source that reads the world on the clock (the history) changes
        // here, and the tooltip below then leads with it.
        _notices.Tick(now);

        Refresh();
    }

    /// <summary>Stops following the projection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _projection.Sessions.CollectionChanged -= OnSessionsChanged;

        _notices.PropertyChanged -= OnNoticeChanged;

        if (_ownsNotices)
        {
            _notices.Dispose();
        }
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnNoticeChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(NoticeTexts));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
        Refresh();
    }

    /// <summary>Recomputes the glyph and the tooltip from the sessions and the modes.</summary>
    private void Refresh()
    {
        var summary = StatusSummary.Of(_projection.Sessions);
        var paused = _modes.IsMonitoringPaused;
        var mutedUntil = _modes.AllMutedUntil;

        // A mute that has lapsed is not a mute. Asked as a predicate against this tick's instant,
        // because nothing raised an event when it expired.
        var muted = mutedUntil is { } until && (until == DateTimeOffset.MaxValue || _now < until);

        var next = TrayVisuals.ColourOf(summary.Worst);

        // The decisions record (T1.37, issue #48): the tray light changing is a decision, and it
        // is made here, on the dispatcher — so it rides the recorder's cross-thread queue and
        // lands with event_id NULL. The driving session is the first at the worst state, an id
        // and never a title.
        if (next != Colour)
        {
            _decisions?.External(new Storage.Decision(
                _now,
                _projection.Sessions.FirstOrDefault(session => session.State == summary.Worst)?.Id.Value,
                Storage.DecisionKind.TrayLightChanged,
                FromState: Colour.ToString(),
                ToState: next.ToString(),
                Reason: summary.Worst.ToString()));
        }

        Colour = next;
        IsPaused = paused;
        IsMuted = muted;
        Icon = TrayIcons.For(Colour, paused);
        // Every fault leads the tooltip through the board, in its order: the port first (T1.57), then
        // each notice (T1.54). One path, so the port fault shows once.
        // "Last heard" is the last item, always (the operator's ruling of 2026-10-03), and the first
        // to go whole when Windows' length limit is reached. Read on this tick: no poller.
        Tooltip = TrayTooltip.For(
            summary,
            paused,
            muted ? mutedUntil : null,
            _now,
            _notices.TrayText,
            _health is null ? null : TrayTooltip.LastHeard(_health.LastHeardAt, _now));

        OnPropertyChanged(nameof(MuteAllLabel));
        OnPropertyChanged(nameof(PauseLabel));
    }

    /// <summary>Sends a mode change down the Channel.</summary>
    private void Publish(SoundCommandKind kind, DateTimeOffset? until = null)
    {
        var command = new SoundCommand
        {
            // Global: no session, and SessionId.IsEmpty says so. The Registry never sees one.
            SessionId = default,
            Timestamp = _clock.Now,
            Cwd = string.Empty,
            Kind = kind,
            Until = until,
        };

        if (!_sink.TryPublish(command))
        {
            // The channel is bounded and drops oldest under load. Nothing optimistic happens
            // here: the mode changes when the command lands, and if it never lands the tooltip
            // goes on telling the truth about a mute that was never applied.
            _logger.Warning("The tray's {Kind} command was refused by the pipeline.", kind);

            return;
        }

        PublishedCount++;
    }
}
