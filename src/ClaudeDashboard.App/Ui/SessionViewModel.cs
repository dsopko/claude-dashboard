using System.Globalization;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// One session row (Design Document §9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed by <see cref="SessionId"/> and long-lived.</strong> The Registry replaces the
/// immutable <see cref="Session"/> record on every change, so a view model that <em>was</em> the
/// record would be a different object after each event — and anything bound to it, selection
/// most of all, would be lost several times a minute. This wraps instead: one view model per
/// session id for the life of the session, with the record swapped underneath it.
/// </para>
/// <para>
/// <strong>The text is data.</strong> <see cref="Prompt"/> and <see cref="Answer"/> hand back
/// exactly what the hook carried, unparsed and uninterpreted (Impl §3.4; TS §II.5). WPF binding
/// renders a string as text, and nothing here builds markup, evaluates, or formats it into
/// anything that could be interpreted — a snippet is a substring and nothing more.
/// </para>
/// </remarks>
public sealed class SessionViewModel : DashboardRow
{
    /// <summary>How much of the prompt the collapsed row shows before eliding.</summary>
    public const int SnippetLength = 140;

    private readonly MotionPolicy _motion;
    private Session _session;
    private DateTimeOffset _now;
    private bool _isExpanded;

    /// <summary>Wraps <paramref name="session"/>.</summary>
    /// <param name="session">The session this row shows.</param>
    /// <param name="motion">
    /// Whether motion is permitted; defaults to <see cref="MotionPolicy.System"/>, so a row that
    /// is handed no policy still honours the operator's reduced-motion setting.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public SessionViewModel(Session session, MotionPolicy? motion = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _now = session.LastActivity;
        _motion = motion ?? MotionPolicy.System;
    }

    /// <summary>The session's id — stable for this view model's whole life.</summary>
    public SessionId Id => _session.Id;

    /// <summary>The session as it currently stands.</summary>
    public Session Session
    {
        get => _session;
        internal set
        {
            ArgumentNullException.ThrowIfNull(value);

            // Session is a record, so an equal-but-different instance is not a change. Comparing
            // by value rather than by reference is what keeps a redelivered event from raising a
            // property change that would repaint a row nothing happened to.
            if (_session == value)
            {
                return;
            }

            _session = value;
            RaiseAll();
        }
    }

    /// <summary>Whether the row is showing the full exchange (Design Document §9).</summary>
    /// <remarks>
    /// Lives on the row rather than on the view model so that expanding one row survives every
    /// refresh around it — the row instance outlives the record, which is the whole reason it is
    /// keyed by <see cref="SessionId"/>.
    /// </remarks>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    /// <summary>Where this session sits in the attention model.</summary>
    public SessionState State => _session.State;

    /// <summary>The colour this row reads as (mockups' legend).</summary>
    public Accent Accent => RowVisuals.AccentOf(_session.State);

    /// <summary>
    /// What the row is allowed to do visually: red blinks, working breathes, nothing else moves
    /// (Design Document §9) — and nothing at all when the operator has asked for reduced motion.
    /// </summary>
    public MotionKind Motion => _motion.Allow(_session.State);

    /// <summary>The state badge — "PERMISSION", "FINISHED", … (mockups).</summary>
    public string BadgeText => RowVisuals.BadgeOf(_session.State);

    /// <summary>The age, phrased for the state: "waiting 4 min", "2 min ago", "6 min".</summary>
    public string AgeText => RowVisuals.Age(_session.State, Age);

    /// <summary>
    /// The extra detail the meta line carries — currently the error kind, or null.
    /// </summary>
    /// <remarks>
    /// Verbatim from the hook, like every other piece of text here: the matcher list in Impl
    /// §9.1 is open-ended, so a kind this build has never heard of must still reach the operator
    /// intact rather than being prettified into something else or dropped.
    /// </remarks>
    public string? Detail => _session.ErrorKind;

    /// <summary>Whether there is a <see cref="Detail"/> to show.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(_session.ErrorKind);

    /// <summary>
    /// The workspace's short name, for the small group tag flat view puts on each row
    /// (Design Document §7). Empty when the session has no workspace.
    /// </summary>
    public string GroupTag => string.IsNullOrWhiteSpace(_session.Cwd)
        ? string.Empty
        : RowVisuals.WorkspaceLabel(_session.Cwd);

    /// <summary>When the prompt was submitted, for the expanded row's "YOU ASKED · 14:32".</summary>
    public string AskedAtText => _session.Latest.StartedAt.ToLocalTime()
        .ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>The band it displays in — from Core, never decided here.</summary>
    public AttentionBand Band => AttentionOrder.BandOf(_session.State);

    /// <summary>The submitted prompt, verbatim.</summary>
    public string Prompt => _session.Latest.Prompt;

    /// <summary>The prompt shortened to a row's worth, verbatim as far as it goes.</summary>
    public string PromptSnippet =>
        Prompt.Length <= SnippetLength ? Prompt : Prompt[..SnippetLength] + "…";

    /// <summary>Claude's answer once known, verbatim, or null (Design Document §9, expanded row).</summary>
    public string? Answer => _session.Latest.Answer;

    /// <summary>Whether there is an answer to show in an expanded row.</summary>
    public bool HasAnswer => _session.Latest.IsAnswered;

    /// <summary>The workspace this session is running in.</summary>
    public string Cwd => _session.Cwd;

    /// <summary>The failure that stopped it, or null.</summary>
    public string? ErrorKind => _session.ErrorKind;

    /// <summary>Whether the row offers an acknowledge affordance (Design Document §9).</summary>
    /// <remarks>The command itself is T1.12's; this is only whether the row has one.</remarks>
    public bool CanAcknowledge => _session.State is SessionState.Unread
        or SessionState.NeedsPermission
        or SessionState.NeedsQuestion
        or SessionState.Error;

    /// <summary>
    /// How long the session has been in its current state, as of the last <see cref="RefreshAge"/>.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Session.EnteredAt"/> and a supplied instant rather than read from
    /// a clock, so that it advances only when something drives it. This type starts no timer —
    /// the event consumer owns the only periodic loop in the process, deliberately (T1.9).
    /// </remarks>
    public TimeSpan Age => _now - _session.EnteredAt;

    /// <summary>Recomputes <see cref="Age"/> against <paramref name="now"/>.</summary>
    /// <remarks>Call on the UI thread; it raises a property change.</remarks>
    public void RefreshAge(DateTimeOffset now)
    {
        if (_now == now)
        {
            return;
        }

        _now = now;
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(AgeText));
    }

    /// <summary>
    /// Re-reads <see cref="Motion"/>, because the reduced-motion setting changed underneath it.
    /// </summary>
    /// <remarks>
    /// Driven from <see cref="MainViewModel"/> rather than by each row subscribing to the policy:
    /// fifteen rows subscribing to one process-wide event is fifteen handlers to unsubscribe, and
    /// a row that forgot to would keep the policy alive after the session ended.
    /// </remarks>
    public void RefreshMotion() => OnPropertyChanged(nameof(Motion));

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Band));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(Motion));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(GroupTag));
        OnPropertyChanged(nameof(AskedAtText));
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(PromptSnippet));
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(Cwd));
        OnPropertyChanged(nameof(ErrorKind));
        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(Age));
    }
}
