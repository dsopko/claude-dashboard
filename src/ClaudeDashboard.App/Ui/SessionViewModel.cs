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

    private Session _session;
    private DateTimeOffset _now;

    /// <summary>Wraps <paramref name="session"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public SessionViewModel(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _now = session.LastActivity;
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

    /// <summary>Where this session sits in the attention model.</summary>
    public SessionState State => _session.State;

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
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Band));
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
