using System.Globalization;
using System.Text;
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

    /// <summary>
    /// How much of the session's title the row shows, counted in <strong>grapheme clusters</strong>
    /// (issue #18).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Forty is the operator's number: it shows in full every title in the archive — the longest is
    /// 29 — and roughly 85% of the names across their live sessions, while bounding the tail.
    /// </para>
    /// <para>
    /// <strong>Clusters, not characters, and the difference is visible.</strong> A title is
    /// arbitrary text. Cutting at 40 UTF-16 code units splits a surrogate pair whenever the 41st
    /// position is an astral character, which leaves a lone surrogate — measured on .NET 10 for
    /// 👍, for a ZWJ family, and for a regional-indicator flag: the result does not round-trip
    /// through UTF-8 and renders as the replacement glyph. Cutting by cluster keeps each of those
    /// whole, and keeps a combining accent attached to its letter.
    /// </para>
    /// <para>
    /// This is <em>not</em> what <see cref="SnippetLength"/> does to the prompt, which still cuts
    /// by character. That is a known defect in the older property, filed separately, and it is
    /// deliberately not copied here.
    /// </para>
    /// </remarks>
    public const int TitleClusters = 40;

    /// <summary>
    /// The hard ceiling, in characters, on the title text a row will lay out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A cluster budget bounds what is shown and not what is laid out, which is why there
    /// are two numbers here rather than one.</strong> Measured: forty clusters of a letter plus
    /// two hundred combining marks each is <strong>8,040 characters</strong> and passes a
    /// forty-cluster cut completely untouched. The title comes from outside the process, so the
    /// row needs a bound on the string as well as on the glyph count.
    /// </para>
    /// <para>
    /// A hundred and sixty is four times the cluster budget. The widest legitimate case measured —
    /// forty clusters of ZWJ family emoji — lands at 48 characters, so this clears real text by a
    /// wide margin and bites only on the degenerate case. The second cut still lands on a cluster
    /// boundary, so the ceiling cannot reintroduce the split glyph the first cut exists to avoid.
    /// </para>
    /// </remarks>
    public const int TitleCharacterCeiling = 160;

    /// <summary>What separates the title from the prompt on the row: <c>Director — run the …</c>.</summary>
    private const string TitleSeparator = " — ";

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

    /// <summary>Whether this session has a title to show before its prompt.</summary>
    public bool HasTitle => TitleOfRow.Shown.Length > 0;

    /// <summary>
    /// The session's title as the row shows it: folded to one line, cut to
    /// <see cref="TitleClusters"/> clusters with an ellipsis, or empty when there is none.
    /// </summary>
    /// <remarks>
    /// A session with no title gives an empty string here and an empty
    /// <see cref="TitlePrefix"/>, so the row renders exactly as it did before issue #18 — no
    /// separator, no empty prefix, and the prompt keeps every one of its
    /// <see cref="SnippetLength"/> characters, because the title sits outside that budget.
    /// </remarks>
    public string TitleDisplay => TitleOfRow.Shown;

    /// <summary>
    /// What the row prints ahead of the prompt: the title and its separator, or empty.
    /// </summary>
    public string TitlePrefix => HasTitle ? TitleDisplay + TitleSeparator : string.Empty;

    /// <summary>
    /// The whole title, for hovering — <strong>only when it was cut</strong>. Null otherwise, so
    /// no tooltip appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tooltip repeating text already on screen is noise, so an untruncated title gets none.
    /// Null rather than empty because that is what WPF reads as "no tooltip"; an empty string
    /// would open an empty popup.
    /// </para>
    /// <para>
    /// <strong>This is not length-capped, deliberately.</strong> Issue #18 rules that a truncated
    /// title is shown here in full, and a cap would make the tooltip a second truncation with no
    /// way to read past it. The folding still applies, so a title full of line breaks cannot grow
    /// the popup vertically without limit — but a pathological title is long here, and that is a
    /// recorded residual rather than an oversight.
    /// </para>
    /// </remarks>
    public string? TitleTooltip => TitleOfRow.Truncated ? TitleOfRow.Full : null;

    /// <summary>
    /// What a screen reader hears for the row: the title and the prompt, exactly as they render.
    /// </summary>
    /// <remarks>
    /// <c>AutomationProperties.Name</c> used to bind <see cref="PromptSnippet"/> alone, so a
    /// screen reader would not have heard the title at all. It is built by the same concatenation
    /// the row draws — <see cref="TitlePrefix"/> then <see cref="PromptSnippet"/> — so the
    /// accessible name cannot drift from the visible one, which is the failure this property is
    /// most likely to have.
    /// </remarks>
    public string RowName => TitlePrefix + PromptSnippet;

    /// <summary>The title, folded and measured once for the four properties above.</summary>
    private TitleText TitleOfRow => TitleText.From(_session.Title);

    /// <summary>
    /// A latched title prepared for a row: folded to one line, and cut to fit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Rendering, not interpreting.</strong> The domain keeps the title exactly as it
    /// arrived (TS §II.5); everything here happens on the way to the screen and nothing here
    /// parses, evaluates or formats the text into something that could be interpreted. Folding and
    /// cutting are the two things a row needs and neither changes what the title says.
    /// </para>
    /// </remarks>
    /// <param name="Full">The folded title, whole.</param>
    /// <param name="Shown">The folded title cut to a row's worth.</param>
    /// <param name="Truncated">Whether <paramref name="Shown"/> lost anything.</param>
    private readonly record struct TitleText(string Full, string Shown, bool Truncated)
    {
        /// <summary>A session with no title.</summary>
        public static readonly TitleText None = new(string.Empty, string.Empty, false);

        /// <summary>Prepares <paramref name="raw"/>, the value the Registry latched.</summary>
        public static TitleText From(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return None;
            }

            var folded = Fold(raw);
            if (folded.Length == 0)
            {
                return None;
            }

            var (shown, truncated) = Shorten(folded);
            return new TitleText(folded, shown, truncated);
        }

        /// <summary>
        /// Collapses every run of whitespace — line breaks and control characters included — to a
        /// single space, and trims the ends.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A newline is a bound that forty of anything does not cover.</strong> A
        /// <c>TextBlock</c> breaks on one whatever the wrapping mode, so a two-line title would
        /// double the row's height however few characters it held. Folding is what makes the row a
        /// row.
        /// </para>
        /// <para>
        /// <strong>Format characters are deliberately kept.</strong> U+200D, the zero-width joiner,
        /// is what holds a family emoji together as one cluster; stripping the Format category
        /// along with the control characters would shatter exactly the graphemes
        /// <see cref="Shorten"/> exists to keep whole.
        /// </para>
        /// </remarks>
        private static string Fold(string text)
        {
            var builder = new StringBuilder(text.Length);
            var pending = false;
            Span<char> utf16 = stackalloc char[2];

            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune) ||
                    Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                        or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator)
                {
                    // Leading whitespace is dropped rather than remembered, so the result is
                    // trimmed at the front without a second pass.
                    pending = builder.Length > 0;
                    continue;
                }

                if (pending)
                {
                    builder.Append(' ');
                    pending = false;
                }

                builder.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Cuts <paramref name="folded"/> to <see cref="TitleClusters"/> clusters and
        /// <see cref="TitleCharacterCeiling"/> characters, whichever bites first.
        /// </summary>
        /// <remarks>
        /// Both bounds land on a cluster boundary, so neither can produce the split glyph the
        /// cluster count exists to avoid. The two constants carry the argument for why there are
        /// two of them.
        /// </remarks>
        private static (string Shown, bool Truncated) Shorten(string folded)
        {
            var elements = StringInfo.GetTextElementEnumerator(folded);
            var clusters = 0;
            var taken = 0;

            while (elements.MoveNext())
            {
                var element = (string)elements.Current;

                if (clusters == TitleClusters || taken + element.Length > TitleCharacterCeiling)
                {
                    return (folded[..taken] + "…", true);
                }

                clusters++;
                taken += element.Length;
            }

            return (folded, false);
        }
    }

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
        OnPropertyChanged(nameof(HasTitle));
        OnPropertyChanged(nameof(TitleDisplay));
        OnPropertyChanged(nameof(TitlePrefix));
        OnPropertyChanged(nameof(TitleTooltip));
        OnPropertyChanged(nameof(RowName));
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
