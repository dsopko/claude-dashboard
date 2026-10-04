using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.App.Storage;

/// <summary>
/// What kind of decision a row records (T1.37; issue #48 is the authority for this list).
/// </summary>
/// <remarks>
/// "Row removal scheduled / removed" from issue #48 is deliberately absent: nothing removes a
/// session yet, and a kind nothing produces is a statement more confident than the thing beneath
/// it (ruled 2026-09-25, deferred on #48 until removal exists).
/// </remarks>
public enum DecisionKind
{
    // ---- Registry -----------------------------------------------------------------------------

    /// <summary>First event for a session id the Registry had never seen.</summary>
    SessionAdded = 1,

    /// <summary>A <c>SessionStart</c> on a known id — resume, fork or compact.</summary>
    SessionRefreshed = 2,

    /// <summary>A state transition; <c>from_state</c> and <c>to_state</c> carry it.</summary>
    StateMoved = 3,

    /// <summary>The Registry declined the event; <c>reason</c> carries the <see cref="ApplyOutcome"/>.</summary>
    EventDeclined = 4,

    /// <summary>A <c>CwdChanged</c> moved the session between workspace groups.</summary>
    GroupRederived = 5,

    /// <summary>The silence sweep moved a Working session to Interrupted.</summary>
    SilenceSwept = 6,

    /// <summary>A <c>SessionEnd</c> ended the session.</summary>
    SessionEnded = 7,

    /// <summary>An acknowledgment was applied; <c>reason</c> carries its source.</summary>
    AckApplied = 8,

    /// <summary>An acknowledgment was declined — nothing to acknowledge, or stale.</summary>
    AckDeclined = 9,

    /// <summary>
    /// A <c>Stop</c> listed running background work of a type this build does not recognise, so it
    /// did not count toward Waiting (T1.41, issue #52). <c>reason</c> is
    /// <see cref="TaskTypeReason.UnrecognisedType"/>; <c>detail</c> is how many. Never the type
    /// text or the description: the causing <c>events</c> row, by <c>event_id</c>, holds the
    /// payload for whoever classifies the type later.
    /// </summary>
    TaskTypeUnrecognised = 10,

    // ---- Sound engine -------------------------------------------------------------------------

    /// <summary>A notice was emitted; <c>reason</c> carries the sound.</summary>
    NoticePlayed = 20,

    /// <summary>A nudge was emitted; <c>detail</c> carries the rung and minutes waiting.</summary>
    NudgePlayed = 21,

    /// <summary>A due sound was deliberately not emitted; <c>reason</c> says why.</summary>
    NoticeSuppressed = 22,

    /// <summary>A roster group finished as one; <c>detail</c> carries the member ids.</summary>
    GroupNoticePlayed = 23,

    /// <summary>A global or scoped mute was applied; <c>detail</c> carries the expiry.</summary>
    MuteApplied = 24,

    /// <summary>A timed mute lapsed, observed on the tick.</summary>
    MuteExpired = 25,

    /// <summary>
    /// A sound was emitted and the player dropped it (T1.55, issue #72). Recorded in place of
    /// <see cref="NoticePlayed"/>, <see cref="NudgePlayed"/> or <see cref="GroupNoticePlayed"/>;
    /// <c>reason</c> is <c>NoOutput</c> or <c>Failed</c>, and <c>detail</c> carries the identifiers
    /// the played row would have carried.
    /// </summary>
    SoundDropped = 26,

    // ---- Pipeline -----------------------------------------------------------------------------

    /// <summary>A channel was full and dropped its oldest; <c>reason</c> names the channel.</summary>
    EventDropped = 40,

    /// <summary>Applying an event threw; <c>reason</c> is the exception TYPE, never its message.</summary>
    ApplyFailed = 41,

    /// <summary>
    /// A <c>/hook</c> post was refused: its token did not match (T1.61, issue #74). <c>event_id</c>
    /// and <c>session_id</c> are NULL, and <c>reason</c> is empty: a refused post is not trusted, so
    /// nothing from its body or headers is recorded. At most one row a second; <c>detail</c> is the
    /// count of refusals it stands for (<c>refused=37</c>).
    /// </summary>
    HookRefused = 42,

    /// <summary>
    /// The hourly summary (T1.65, issue #76): the counts since the previous summary, at the first tick
    /// after each full UTC clock hour. <c>event_id</c> and <c>session_id</c> are NULL; <c>reason</c> is
    /// <c>partial</c> for the first one after a start; <c>detail</c> holds the counts as identifiers.
    /// </summary>
    HourlySummary = 43,

    // ---- UI -----------------------------------------------------------------------------------

    /// <summary>The tray light changed; <c>from_state</c>/<c>to_state</c> carry the colours.</summary>
    TrayLightChanged = 60,

    /// <summary>A <c>/show</c> surfaced the window.</summary>
    WindowSurfaced = 61,

    /// <summary>The operator edited a roster.</summary>
    RosterEdited = 62,
}

/// <summary>
/// The <c>reason</c> on a <see cref="DecisionKind.StateMoved"/> row a prompt caused (T1.41,
/// issue #52).
/// </summary>
/// <remarks>
/// Tier-1 acknowledgment — "the operator cannot have typed a new prompt without having seen the
/// previous result" — has no effect beyond the state change it makes, measured for T1.41. So the
/// distinction the operator ruled on, that a prompt nobody typed is not an acknowledgment, is
/// recorded here and in the transition log, and nowhere is it invented.
/// </remarks>
public enum PromptMeaning
{
    /// <summary>The operator's own prompt, on a session with something to acknowledge.</summary>
    AutoAcknowledgment = 1,

    /// <summary>
    /// A prompt nobody typed — a task notification, a peer's message, an idle notice, an agent
    /// message. It moves the session to Working and acknowledges nothing.
    /// </summary>
    MachinePrompt = 2,

    /// <summary>
    /// The session's own scheduled job firing: a prompt exactly equal to a cron its previous Stop
    /// listed (T1.44, issue #56). A machine prompt too — nobody typed it — and it acknowledges
    /// nothing.
    /// </summary>
    ScheduledPrompt = 3,
}

/// <summary>
/// The <c>reason</c> on the <see cref="DecisionKind.StateMoved"/> row of a Stop that ended a tick
/// quietly (T1.44, issue #56).
/// </summary>
/// <remarks>
/// The row moves the session back to what it showed before the tick. The sound engine's own row
/// beside it, <c>NoticeSuppressed</c> with reason <c>AlreadyAnnounced</c>, says why no finished
/// sound played. The reply is never in either: it was compared, not recorded.
/// </remarks>
public enum TickOutcome
{
    /// <summary>The tick's reply was exactly the sentinel: the row went back as it was.</summary>
    QuietTick = 1,
}

/// <summary>The <c>reason</c> on a <see cref="DecisionKind.TaskTypeUnrecognised"/> row.</summary>
public enum TaskTypeReason
{
    /// <summary>The <c>type</c> is on neither the allow-list nor the known-excluded list.</summary>
    UnrecognisedType = 1,
}

/// <summary>
/// One decision the dashboard made, or deliberately did not make (T1.37, issue #48).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identifiers and enums only — never a title, prompt, payload or message body
/// (T1.24).</strong> <see cref="Reason"/> is an enum name or an identifier;
/// <see cref="Detail"/> is <c>key=value</c> identifiers — a device id, a group key, a rung, a
/// count of minutes. The inventory guard covers both.
/// </para>
/// <para>
/// The row's <c>event_id</c> is not here: the decision cannot know it, because the archive
/// thread assigns it at insert. The decisions travel WITH their event in an
/// <see cref="ArchiveRecord"/> and take the id from the same transaction.
/// </para>
/// </remarks>
/// <param name="Ts">When the decision was made.</param>
/// <param name="SessionId">The session it concerns, or null for a global decision.</param>
/// <param name="Kind">What kind of decision.</param>
/// <param name="FromState">The state left, where the kind moves one.</param>
/// <param name="ToState">The state entered, where the kind moves one.</param>
/// <param name="Reason">An enum name or identifier saying why. Never operator text.</param>
/// <param name="Detail">Identifier pairs. Never operator text.</param>
public sealed record Decision(
    DateTimeOffset Ts,
    string? SessionId,
    DecisionKind Kind,
    string? FromState = null,
    string? ToState = null,
    string? Reason = null,
    string? Detail = null);

/// <summary>
/// What the consumer hands the archive: one event — or none, for a tick — and every decision it
/// produced (T1.37).
/// </summary>
/// <remarks>
/// The hand-off moved from before <c>Registry.Apply</c> to after the sound engine has decided,
/// so the decisions exist to travel. The archive inserts the event row and its decision rows in
/// one transaction, and the decisions take the event's id from that insert — no thread ever
/// waits on the disk for an id.
/// </remarks>
/// <param name="Event">The causing event, or null for a tick's decisions.</param>
/// <param name="Decisions">Every decision made while handling it, in order.</param>
public sealed record ArchiveRecord(InboundEvent? Event, IReadOnlyList<Decision> Decisions)
{
    /// <summary>Whether there is anything at all to write.</summary>
    public bool IsEmpty => Event is null && Decisions.Count == 0;
}
