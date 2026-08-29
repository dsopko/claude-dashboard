using System.Globalization;
using ClaudeDashboard.Core;
using CommunityToolkit.Mvvm.Input;

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
public sealed partial class SessionViewModel : DashboardRow
{
    /// <summary>How much of the prompt the collapsed row shows before eliding.</summary>
    public const int SnippetLength = 140;

    /// <summary>How much of the session id the expanded row shows (issue #15).</summary>
    /// <remarks>
    /// Eight is the operator's choice, made with all three lengths rendered in front of them. It
    /// is enough to tell two live sessions apart at a glance and short enough not to crowd the
    /// button row it sits in.
    /// </remarks>
    public const int IdPreviewLength = 8;

    private readonly MotionPolicy _motion;
    private readonly IAckPublisher? _ack;
    private readonly IClipboard? _clipboard;
    private Session _session;
    private DateTimeOffset _now;
    private bool _isExpanded;
    private bool _copyFailed;

    /// <summary>Wraps <paramref name="session"/>.</summary>
    /// <param name="session">The session this row shows.</param>
    /// <param name="motion">
    /// Whether motion is permitted; defaults to <see cref="MotionPolicy.System"/>, so a row that
    /// is handed no policy still honours the operator's reduced-motion setting.
    /// </param>
    /// <param name="ack">
    /// Where the row's acknowledge action sends its event. Null in tests that are not about
    /// acknowledging, which leaves the affordance visible and disabled rather than absent —
    /// whether a session <em>can</em> be acknowledged is a fact about the session, not about
    /// whether this row was wired up.
    /// </param>
    /// <param name="clipboard">
    /// Where the id goes when the operator clicks it. Null in tests that are not about copying,
    /// which leaves the id visible and readable — showing it and copying it are separate things,
    /// and a row nobody wired a clipboard to should still show which session it is.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public SessionViewModel(
        Session session,
        MotionPolicy? motion = null,
        IAckPublisher? ack = null,
        IClipboard? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _now = session.LastActivity;
        _motion = motion ?? MotionPolicy.System;
        _ack = ack;
        _clipboard = clipboard;
    }

    /// <summary>The session's id — stable for this view model's whole life.</summary>
    public SessionId Id => _session.Id;

    /// <summary>
    /// The first <see cref="IdPreviewLength"/> characters of the session id, or empty for a
    /// session that has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this lives here and not in the XAML, and why it is NOT what keeps
    /// <see cref="SessionId"/>'s doc comment honest.</strong> Truncation and tooltip wording are
    /// view-model work: putting them here is what makes them assertable without a window. That is
    /// the whole reason. It is emphatically <em>not</em> a device for keeping the old claim that a
    /// session id is "never a display string" — that claim stopped being true when this row
    /// started showing one, and it was rewritten rather than routed around. A property with a
    /// display-sounding name does not turn a displayed value into a hidden one.
    /// </para>
    /// <para>
    /// Truncation is by length rather than by format. A <see cref="SessionId"/> wraps any
    /// non-empty string and is not guaranteed to be a GUID, so an id shorter than the preview
    /// length is shown whole rather than throwing.
    /// </para>
    /// </remarks>
    public string ShortId =>
        Id.IsEmpty ? string.Empty : Id.Value[..Math.Min(IdPreviewLength, Id.Value.Length)];

    /// <summary>What hovering the id says: the label, the whole id, and what a click does.</summary>
    /// <remarks>
    /// The full value, because the preview is not enough to paste anywhere and the tooltip is the
    /// only place the operator can read the rest without copying it first.
    /// </remarks>
    public string IdTooltip =>
        Id.IsEmpty
            ? string.Empty
            : string.Create(
                CultureInfo.CurrentCulture,
                $"Claude Session ID:\n{Id.Value}\n\nClick to copy.");

    /// <summary>
    /// Whether the last attempt to copy the id failed. Shown on the row; cleared by a copy that
    /// works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Failure gets a surface and success does not, and that asymmetry was measured
    /// rather than assumed.</strong> The obvious design puts "Copied." in the tooltip, but a WPF
    /// tooltip closes on the click that would trigger it and does not re-open while the pointer
    /// stays where it is — and its default <c>InitialShowDelay</c> is a full second even after
    /// the pointer leaves and returns. So the tooltip cannot say anything at the moment the click
    /// happens, which is the only moment that matters.
    /// </para>
    /// <para>
    /// Success is the expected case and needs nothing: the absence of this marker <em>is</em> the
    /// success signal. A failure must not be silent, because a copy that failed invisibly sends
    /// the operator to paste whatever was on the clipboard before — a success that did not
    /// happen, which is the defect class T1.22 existed to remove.
    /// </para>
    /// <para>
    /// It appears and stays; it does not animate, fade, or time out. Design §9: "red blinks;
    /// working breathes; nothing else moves."
    /// </para>
    /// </remarks>
    public bool CopyFailed
    {
        get => _copyFailed;
        private set
        {
            if (_copyFailed == value)
            {
                return;
            }

            _copyFailed = value;
            OnPropertyChanged(nameof(CopyFailed));
        }
    }

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

    /// <summary>
    /// Whether the row offers an acknowledge affordance (Design Document §4, §9).
    /// </summary>
    /// <remarks>
    /// <strong>Asked, not restated.</strong> Which states an acknowledgment applies to is domain
    /// knowledge and lives in <see cref="Acknowledgment.Applies"/>, where the automatic tier
    /// reads it too. This row would have been the third copy of that list — and the one most
    /// likely to drift, because nothing in Core would fail when it did: the button would simply
    /// stop appearing on a state the next prompt still acknowledges.
    /// </remarks>
    public bool CanAcknowledge => Acknowledgment.Applies(_session.State);

    /// <summary>
    /// Acknowledges this session (Design Document §4 tier 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It publishes an event; it does not change anything.</strong> The row still reads
    /// the projection, so what appears on screen is what the Registry decided, arriving by the
    /// same route as every hook. Nothing here goes grey optimistically — see the remarks on the
    /// command's own summary.
    /// </para>
    /// <para>
    /// One command for both the row's Ack and the expanded row's, because they are the same act;
    /// two would be two things to keep enabled in step.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The body re-checks rather than trusting <c>CanExecute</c>. A binding will not invoke a
    /// disabled command, but <c>RelayCommand.Execute</c> does not gate on it, so anything holding
    /// the command object can raise an ack the Registry will only decline. The channel is bounded
    /// and drops its oldest entry when full (Impl §4) — publishing events already known to be
    /// no-ops is how a real one gets evicted.
    /// </remarks>
    /// <summary>Puts the <em>whole</em> session id on the clipboard (issue #15).</summary>
    /// <remarks>
    /// <para>
    /// <strong>The full value, never <see cref="ShortId"/>, and that is the one defect most
    /// likely to reach production here.</strong> Eight characters is a preview for the eye; it is
    /// useless in a command line or a search box, which is the entire reason the operator asked
    /// for this. Copying the preview would look correct on the row, produce a plausible string on
    /// the clipboard, and fail only later, somewhere else.
    /// </para>
    /// <para>
    /// The body re-checks the id rather than trusting <c>CanExecute</c>, for the reason the
    /// acknowledge command gives: a binding will not invoke a disabled command, but anything
    /// holding the command object can.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCopyId))]
    private void CopyId()
    {
        if (Id.IsEmpty)
        {
            return;
        }

        // A row with no clipboard wired cannot copy, and saying so is truthful: the operator
        // clicked and nothing reached the clipboard. Reporting success would be the false reading
        // this whole affordance is shaped to avoid.
        CopyFailed = _clipboard?.TrySet(Id.Value) is not true;
    }

    /// <summary>Whether there is an id to copy at all.</summary>
    /// <remarks>
    /// Deliberately not gated on the clipboard being wired. Whether a session <em>has</em> an id
    /// is a fact about the session; whether this row was handed a clipboard is a fact about the
    /// wiring, and hiding the affordance for the second would make a wiring fault look like a
    /// session without an id.
    /// </remarks>
    private bool CanCopyId() => !Id.IsEmpty;

    [RelayCommand(CanExecute = nameof(CanRaiseAck))]
    private void Acknowledge()
    {
        if (_ack is { } publisher && CanAcknowledge)
        {
            publisher.Acknowledge(_session);
        }
    }

    /// <summary>
    /// Whether the acknowledge action can be invoked: the session has something to acknowledge,
    /// and this row has somewhere to send it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CanAcknowledge"/>, which is the domain question and drives
    /// whether the affordance is shown at all. A row with nowhere to send an ack shows a
    /// disabled button rather than hiding one, because hiding it would misreport the session.
    /// </remarks>
    public bool CanRaiseAck => CanAcknowledge && _ack is not null;

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
        OnPropertyChanged(nameof(CanRaiseAck));
        AcknowledgeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Age));
    }
}
