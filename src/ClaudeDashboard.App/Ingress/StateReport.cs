using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// What <c>/state</c> answers (T1.46): what the Registry believes now, as the consumer thread
/// last published it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable, built once per publication, and never changed after.</strong> The request
/// thread serializes whatever instance it reads; nothing on the consumer thread can reach into it
/// afterwards. See <see cref="StateBoard"/> for how it crosses threads.
/// </para>
/// <para>
/// <strong>No prompt and no answer, anywhere.</strong> Nothing here is built from an
/// <c>Exchange</c>. The only prose is the title and the task descriptions, and both travel as
/// <see cref="OperatorText"/>.
/// </para>
/// </remarks>
/// <param name="PublishedAt">When the consumer thread built this, by the dashboard's clock.</param>
/// <param name="SessionCount">Every session the Registry holds, Ended included — as the window counts them.</param>
/// <param name="Bands">Sessions per attention band, every band present, zeros included.</param>
/// <param name="Tray">The tray light's roll-up.</param>
/// <param name="Sessions">One entry per session, most urgent first.</param>
/// <param name="Health">
/// The path from Claude Code (T1.61, issue #74): when a message last arrived, and the last
/// self-test. Added when a request is served, not when the consumer publishes, because it is
/// written on request threads. #76 adds to it.
/// </param>
public sealed record StateReport(
    DateTimeOffset PublishedAt,
    int SessionCount,
    IReadOnlyDictionary<AttentionBand, int> Bands,
    TrayRollUp Tray,
    IReadOnlyList<SessionStateEntry> Sessions,
    HealthEntry? Health = null)
{
    /// <summary>What <c>/state</c> answers before the consumer has published anything.</summary>
    public static StateReport Empty(DateTimeOffset at) => Of([], _ => null, at);

    /// <summary>
    /// Builds the report from session snapshots and the nudge schedule.
    /// </summary>
    /// <remarks>
    /// <strong>The band rule is asked, never restated.</strong> Each band comes from
    /// <see cref="AttentionOrder.BandOf"/>, the tray from <see cref="StatusSummary.Of"/> and
    /// <see cref="TrayVisuals.ColourOf"/>, and the order from <see cref="AttentionEngine.Order"/> —
    /// the same calls the window and the tray make. The bands and the tray light are taken over
    /// <see cref="CountedStates.Of"/>, so a roster group counts once, at its roll-up, as in the window and the tray
    /// (T1.83, issue #130); the session count and each session's entry are the sessions themselves.
    /// </remarks>
    /// <param name="sessions">Immutable session snapshots.</param>
    /// <param name="nextNudgeAt">The sound engine's schedule, read on the consumer thread.</param>
    /// <param name="at">When this is published.</param>
    /// <param name="rosters">The rosters; none when null, so every session counts on its own.</param>
    public static StateReport Of(
        IReadOnlyCollection<Session> sessions,
        Func<SessionId, DateTimeOffset?> nextNudgeAt,
        DateTimeOffset at,
        RosterBook? rosters = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(nextNudgeAt);

        var bands = Enum.GetValues<AttentionBand>()
            .OrderByDescending(band => band)
            .ToDictionary(band => band, _ => 0);

        var counted = CountedStates.Of(sessions, rosters ?? RosterBook.Empty, at);

        foreach (var entry in counted)
        {
            bands[AttentionOrder.BandOf(entry.State)]++;
        }

        var worst = StatusSummary.OfCounted(counted).Worst;

        return new StateReport(
            at,
            sessions.Count,
            bands,
            new TrayRollUp(worst, TrayVisuals.ColourOf(worst)),
            [.. AttentionEngine.Order(sessions).SelectMany(band => band.Sessions).Select(session => SessionStateEntry.Of(session, nextNudgeAt(session.Id)))]);
    }
}

/// <summary>The tray light: the most urgent state any session is in, and the colour it shows.</summary>
/// <param name="Worst">The state that sets the light.</param>
/// <param name="Light">The colour the tray icon shows for it.</param>
public sealed record TrayRollUp(SessionState Worst, TrayColour Light);

/// <summary>One session in <c>/state</c>.</summary>
/// <param name="Id">Claude Code's session id.</param>
/// <param name="State">The session's state.</param>
/// <param name="Band">The attention band it displays in.</param>
/// <param name="Group">The workspace group key.</param>
/// <param name="Cwd">The working directory.</param>
/// <param name="Title">The session's title, wrapped. Null when it has none.</param>
/// <param name="EnteredAt">When it entered <paramref name="State"/>.</param>
/// <param name="LastActivity">When anything last happened to it.</param>
/// <param name="LastHeardAt">When Claude Code last sent an event for it.</param>
/// <param name="ErrorKind">The error matcher, in Error only.</param>
/// <param name="NextNudgeAt">When the sound engine next nudges it, or null if nothing is scheduled.</param>
/// <param name="WaitingOn">The background tasks it is waiting on, in Waiting.</param>
public sealed record SessionStateEntry(
    string Id,
    SessionState State,
    AttentionBand Band,
    string Group,
    string Cwd,
    OperatorText? Title,
    DateTimeOffset EnteredAt,
    DateTimeOffset LastActivity,
    DateTimeOffset LastHeardAt,
    string? ErrorKind,
    DateTimeOffset? NextNudgeAt,
    IReadOnlyList<WaitingTaskEntry> WaitingOn)
{
    /// <summary>Copies what the report shows out of an immutable session.</summary>
    public static SessionStateEntry Of(Session session, DateTimeOffset? nextNudgeAt)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionStateEntry(
            session.Id.Value,
            session.State,
            AttentionOrder.BandOf(session.State),
            session.WorkspaceGroup.Value,
            session.Cwd,
            session.Title is { } title ? new OperatorText(title) : null,
            session.EnteredAt,
            session.LastActivity,
            session.LastHeardAt,
            session.ErrorKind,
            nextNudgeAt,
            [.. session.WaitingOn.Select(WaitingTaskEntry.Of)]);
    }
}

/// <summary>
/// One background task a session waits on. Never its command: that is not read off the wire
/// (T1.41), so it is not here to leak.
/// </summary>
/// <param name="Id">Claude Code's task id.</param>
/// <param name="Kind">Shell or subagent.</param>
/// <param name="Description">What the agent said the task is, wrapped.</param>
/// <param name="FirstSeenAt">When the dashboard first saw it running.</param>
public sealed record WaitingTaskEntry(
    string Id,
    BackgroundTaskKind Kind,
    OperatorText Description,
    DateTimeOffset FirstSeenAt)
{
    /// <summary>Copies a waiting task, wrapping its description.</summary>
    public static WaitingTaskEntry Of(WaitingTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new WaitingTaskEntry(task.Id, task.Kind, new OperatorText(task.Description), task.FirstSeenAt);
    }
}
