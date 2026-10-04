using System.Globalization;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.App.Ui;

/// <summary>One line of the Activity window, in plain words (T1.70, issue #97).</summary>
/// <param name="Id">The line's order: the higher, the newer.</param>
/// <param name="Ts">When it was decided.</param>
/// <param name="Played">Whether a sound played: the line shows <c>♪</c>.</param>
/// <param name="What">What occurred, in plain words.</param>
/// <param name="Name">The session's name, its short id, or a group's name; empty for a decision about no one.</param>
/// <param name="Project">The project, the last folder of the path, by the main window's rule; or empty.</param>
/// <param name="ProjectPath">The full path, for the hover; or null.</param>
/// <param name="Detail">The detail, in plain words; or empty.</param>
public sealed record ActivityLine(
    long Id,
    DateTimeOffset Ts,
    bool Played,
    string What,
    string Name,
    string Project,
    string? ProjectPath,
    string Detail)
{
    /// <summary>
    /// The time, <c>14:32</c>, with a day name before it when the line is not from today:
    /// <c>Mon 23:58</c>. Local time.
    /// </summary>
    public string TimeText(DateTimeOffset now)
    {
        var local = Ts.ToLocalTime();

        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The line as one sentence, for a screen reader and for the hover: "14:32, sound played, finished,
    /// Director, project claude-dashboard, reminder, waiting 7 min."
    /// </summary>
    public string Sentence(DateTimeOffset now)
    {
        var parts = new List<string> { TimeText(now) };

        if (Played)
        {
            parts.Add("sound played");
        }

        parts.Add(What);

        if (Name.Length > 0)
        {
            parts.Add(Name);
        }

        if (Project.Length > 0)
        {
            parts.Add("project " + Project);
        }

        if (Detail.Length > 0)
        {
            parts.Add(Detail);
        }

        return string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// The Activity window's table: which decisions it shows, and the plain words for each kind and reason
/// (T1.70, issue #97). Impl §5.7 holds the same table.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plain words, never code names.</strong> Every kind, state, reason and sound that a shown line
/// can carry has words here. A value this build does not know gives no words rather than its own name:
/// an unknown reason shows no detail, an unknown state shows "changed", an unknown sound "a sound". So no
/// enum name reaches the screen. <c>ActivityWordsTests</c> holds every value of every enum involved.
/// </para>
/// <para>
/// <strong>What shows:</strong> what happened to the operator's sessions and to sound. The dashboard's
/// own records (a declined event, a refreshed session, the tray light, the hourly summary) are for a
/// developer with SQL, and stay out of this window.
/// </para>
/// <para>
/// The name, the project and the detail come from the decision only: its <c>SessionTitle</c> (T1.69), the
/// short id when there is none, and the last folder of its <c>Cwd</c> by <see cref="RowVisuals.WorkspaceLabel"/>.
/// No prompt, no answer: a decision holds none.
/// </para>
/// </remarks>
public static class ActivityWords
{
    /// <summary>The kinds the window shows.</summary>
    public static readonly IReadOnlySet<DecisionKind> Shown = new HashSet<DecisionKind>
    {
        DecisionKind.SessionAdded,
        DecisionKind.StateMoved,
        DecisionKind.SilenceSwept,
        DecisionKind.SessionEnded,
        DecisionKind.AckApplied,
        DecisionKind.NoticePlayed,
        DecisionKind.NudgePlayed,
        DecisionKind.GroupNoticePlayed,
        DecisionKind.NoticeSuppressed,
        DecisionKind.SoundDropped,
        DecisionKind.MuteApplied,
        DecisionKind.MuteExpired,
    };

    /// <summary>The first characters of a session id, for a row with no name: as the main window's id.</summary>
    public const int ShortIdLength = SessionViewModel.IdPreviewLength;

    /// <summary>Whether the window shows this kind.</summary>
    public static bool IsShown(DecisionKind kind) => Shown.Contains(kind);

    /// <summary>
    /// The line for a decision, or null when the window does not show its kind. <paramref name="id"/> orders
    /// the lines: the higher, the newer.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    public static ActivityLine? LineOf(long id, Decision row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var kind = row.Kind;

        if (!IsShown(kind))
        {
            return null;
        }

        var (played, what, detail) = Words(kind, row);
        var name = NameOf(kind, row);
        var path = string.IsNullOrWhiteSpace(row.Cwd) ? null : row.Cwd;

        return new ActivityLine(
            id,
            row.Ts,
            played,
            what,
            name,
            path is null ? string.Empty : RowVisuals.WorkspaceLabel(path),
            path,
            detail);
    }

    /// <summary>The words for a state the session entered.</summary>
    public static string StateWords(string? state) => Parse<SessionState>(state) switch
    {
        SessionState.NeedsPermission => "needs permission",
        SessionState.NeedsQuestion => "asks a question",
        SessionState.Error => "stopped on an error",
        SessionState.Unread => "finished",
        SessionState.Working => "working",
        SessionState.Waiting => "waiting on background work",
        SessionState.Acked => "seen",
        SessionState.Interrupted => "went quiet",
        SessionState.Ended => "ended",
        _ => "changed",
    };

    /// <summary>The words for a sound, by its id: the name of its file (Impl Part 7).</summary>
    public static string SoundWords(string? sound) => sound switch
    {
        "finished" => "finished",
        "permission" => "permission",
        "question" => "question",
        "error" => "error",
        _ => "a sound",
    };

    /// <summary>Why a sound that was due did not play.</summary>
    public static string WhyNoSound(string? reason) => Parse<SuppressionReason>(reason) switch
    {
        SuppressionReason.MonitoringPaused => "monitoring is paused",
        SuppressionReason.AllMuted => "all sound is muted",
        SuppressionReason.SessionMuted => "this session is muted",
        SuppressionReason.GroupMuted => "its group is muted",
        SuppressionReason.GroupDone => "its group owns the sound",
        SuppressionReason.AlreadyAnnounced => "announced before",
        _ => string.Empty,
    };

    /// <summary>Why a sound that was played was not heard.</summary>
    public static string WhyNotHeard(string? reason) => Parse<SoundOutcome>(reason) switch
    {
        SoundOutcome.NoOutput => "no sound device",
        SoundOutcome.Failed => "the sound could not play",
        _ => string.Empty,
    };

    /// <summary>Why a session changed state, when the record says.</summary>
    public static string WhyMoved(string? reason) => reason switch
    {
        nameof(PromptMeaning.ScheduledPrompt) => "its scheduled job ran",
        nameof(PromptMeaning.MachinePrompt) => "a prompt that nobody typed",
        nameof(PromptMeaning.AutoAcknowledgment) => "a new prompt, so the last result counts as seen",
        nameof(TickOutcome.QuietTick) => "its scheduled job ran and changed nothing",
        _ => string.Empty,
    };

    /// <summary>Who acknowledged a session.</summary>
    public static string WhoAcknowledged(string? reason) => Parse<AckSource>(reason) switch
    {
        AckSource.Manual => "you acknowledged it",
        AckSource.InferredFocus => "you looked at it",
        _ => string.Empty,
    };

    /// <summary>What a mute or pause command did.</summary>
    public static string MuteWords(string? reason) => Parse<SoundCommandKind>(reason) switch
    {
        SoundCommandKind.MuteAll => "all sound muted",
        SoundCommandKind.UnmuteAll => "sound on",
        SoundCommandKind.PauseMonitoring => "monitoring paused",
        SoundCommandKind.ResumeMonitoring => "monitoring resumed",
        _ => "sound changed",
    };

    private static (bool Played, string What, string Detail) Words(DecisionKind kind, Decision row) => kind switch
    {
        DecisionKind.SessionAdded => (false, "new session", StateWords(row.ToState)),
        DecisionKind.StateMoved => (false, MovedWords(row), WhyMoved(row.Reason)),
        DecisionKind.SilenceSwept => (false, "went quiet", Number(row.Detail, "silentMinutes") is { } minutes
            ? $"no event for {minutes} minutes"
            : "no event for a while"),
        DecisionKind.SessionEnded => (false, "ended", string.Empty),
        DecisionKind.AckApplied => (false, "seen", WhoAcknowledged(row.Reason)),
        DecisionKind.NoticePlayed => (true, SoundWords(row.Reason), string.Empty),
        DecisionKind.NudgePlayed => (true, SoundWords(row.Reason), Number(row.Detail, "waitedMinutes") is { } waited
            ? $"reminder, waiting {waited} min"
            : "reminder"),
        DecisionKind.GroupNoticePlayed => (true, "finished", row.Reason == "nudge" ? "reminder for the group" : "the whole group finished"),
        DecisionKind.NoticeSuppressed => (false, "no sound", Joined(SoundWords(Value(row.Detail, "sound")), WhyNoSound(row.Reason))),
        DecisionKind.SoundDropped => (false, "no sound", Joined(SoundWords(Value(row.Detail, "sound")), WhyNotHeard(row.Reason))),
        DecisionKind.MuteApplied => (false, MuteWords(row.Reason), Until(row.Detail)),
        DecisionKind.MuteExpired => (false, "sound on again", "the timed mute ended"),
        _ => (false, "changed", string.Empty),
    };

    /// <summary>"working again" for a session that goes back to work; the state's words otherwise.</summary>
    private static string MovedWords(Decision row) =>
        Parse<SessionState>(row.ToState) == SessionState.Working && row.FromState is not null
            ? "working again"
            : StateWords(row.ToState);

    /// <summary>
    /// The session's name; its short id when it has none; a group's name for a group's sound; or empty.
    /// </summary>
    private static string NameOf(DecisionKind kind, Decision row)
    {
        if (!string.IsNullOrWhiteSpace(row.SessionTitle))
        {
            return row.SessionTitle;
        }

        if (!string.IsNullOrEmpty(row.SessionId))
        {
            return row.SessionId.Length <= ShortIdLength ? row.SessionId : row.SessionId[..ShortIdLength];
        }

        return kind == DecisionKind.GroupNoticePlayed && GroupOf(row.Detail) is { } group ? GroupName(group) : string.Empty;
    }

    /// <summary>A group's name, by the main window's heading rule: the roster's name, or the folder's.</summary>
    internal static string GroupName(GroupKey key) => GroupKeys.KindOf(key) switch
    {
        GroupKeyKind.Roster => GroupKeys.RosterNameOf(key) ?? string.Empty,
        GroupKeyKind.Workspace => RowVisuals.WorkspaceLabel(key.Value[(key.Value.IndexOf(':', StringComparison.Ordinal) + 1)..]),
        _ => string.Empty,
    };

    /// <summary>The group key in a group sound's detail: <c>group=roster:name members=…</c>.</summary>
    private static GroupKey? GroupOf(string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var start = detail.IndexOf("group=", StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        start += "group=".Length;
        var end = detail.IndexOf(" members=", start, StringComparison.Ordinal);
        var value = end < 0 ? detail[start..] : detail[start..end];

        return string.IsNullOrWhiteSpace(value) ? null : new GroupKey(value);
    }

    /// <summary>A timed mute's end, "until 15:02", in local time; or empty.</summary>
    private static string Until(string? detail) =>
        Value(detail, "until") is { } until
            && DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? "until " + at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;

    /// <summary>The raw value of a <c>key=value</c> detail, or null. Never shown as it is.</summary>
    private static string? Value(string? detail, string key)
    {
        if (detail is null)
        {
            return null;
        }

        foreach (var pair in detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return pair[(key.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>A whole number from a <c>key=value</c> detail, or null: never a raw value on screen.</summary>
    private static int? Number(string? detail, string key) =>
        int.TryParse(Value(detail, key), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Joined(string first, string second) =>
        second.Length == 0 ? first : first + ", " + second;

    private static T? Parse<T>(string? value)
        where T : struct, Enum =>
        value is not null && Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
