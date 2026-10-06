using System.Globalization;
using System.Text;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// The values an event carries from a fixed list, as Claude Code sent them, and the text the log file and the
/// decisions record write for one (T1.79, issue #9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An event's type field</strong> is the one value that says which kind of that event it is: a
/// <c>Notification</c>'s type, a <c>SessionStart</c>'s source, a <c>StopFailure</c>'s error kind and a
/// <c>SessionEnd</c>'s reason. Each is read into a fixed list in <c>Matchers.cs</c>, with <c>Unknown</c> for any
/// other value.
/// </para>
/// <para>
/// <strong>An unknown value</strong> is a type field that parses to <c>Unknown</c>, or the <c>type</c> of a running
/// background task on a <c>Stop</c> that is not <c>shell</c>, <c>subagent</c> or <c>monitor</c>. A known value
/// that the dashboard ignores on purpose (<c>idle_prompt</c>, <c>agent_completed</c>) is not unknown.
/// </para>
/// <para>
/// <strong>The text is the value as Claude Code sent it</strong> (T1.76, issue #118), with two limits, so that a
/// broken or hostile payload cannot write a huge line or a false one: a control character is written as an escape
/// (<c>\n</c>, <c>\u0007</c>), so one value stays on one line; and the text is cut after
/// <see cref="MaxLength"/> characters, with an ellipsis.
/// </para>
/// </remarks>
public static class EventValues
{
    /// <summary>The most characters of one value that a line or a decision row writes, before the ellipsis.</summary>
    public const int MaxLength = 200;

    /// <summary>What a missing or empty value is written as.</summary>
    public const string None = "(none)";

    /// <summary>The event's type field as Claude Code sent it, or null for an event that has none.</summary>
    public static string? TypeOf(InboundEvent inboundEvent) => inboundEvent switch
    {
        Notification notification => notification.NotificationType,
        SessionStart start => start.Source ?? string.Empty,
        StopFailure failure => failure.ErrorKind,
        SessionEnd end => end.Reason,
        _ => null,
    };

    /// <summary>Each value of the event that this build does not know, in the order the event holds them.</summary>
    public static IEnumerable<UnknownValue> UnknownsOf(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        switch (inboundEvent)
        {
            case Notification notification when notification.Kind == NotificationKind.Unknown:
                yield return new UnknownValue(notification.HookEventName, UnknownField.NotificationType, notification.NotificationType);
                break;

            case SessionStart start when start.ParsedSource == SessionStartSource.Unknown:
                yield return new UnknownValue(start.HookEventName, UnknownField.Source, start.Source ?? string.Empty);
                break;

            case StopFailure failure when failure.Kind == StopFailureKind.Unknown:
                yield return new UnknownValue(failure.HookEventName, UnknownField.Error, failure.ErrorKind);
                break;

            case SessionEnd end when end.ParsedReason == SessionEndReason.Unknown:
                yield return new UnknownValue(end.HookEventName, UnknownField.Reason, end.Reason);
                break;

            case Stop stop:
                foreach (var type in stop.UnrecognisedBackgroundTaskTypes)
                {
                    yield return new UnknownValue(stop.HookEventName, UnknownField.BackgroundTaskType, type);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The value as it is written: <see cref="None"/> when it is missing or empty; otherwise each control character
    /// escaped, and the text cut after <see cref="MaxLength"/> characters with an ellipsis.
    /// </summary>
    public static string Shown(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return None;
        }

        // Escaped until the text is one character past the limit, or the value ends.
        var text = new StringBuilder(Math.Min(raw.Length, MaxLength) + 8);
        var index = 0;

        for (; index < raw.Length && text.Length <= MaxLength; index++)
        {
            var c = raw[index];

            switch (c)
            {
                case '\n':
                    text.Append("\\n");
                    break;

                case '\r':
                    text.Append("\\r");
                    break;

                case '\t':
                    text.Append("\\t");
                    break;

                default:
                    if (char.IsControl(c))
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        if (index == raw.Length && text.Length <= MaxLength)
        {
            return text.ToString();
        }

        // Never half a character: a cut between the two halves of a surrogate pair leaves an invalid string.
        var length = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;

        return text.ToString(0, length) + "…";
    }
}

/// <summary>A field that an event read into a fixed list, by its name on the wire.</summary>
public enum UnknownField
{
    /// <summary>A <c>Notification</c>'s <c>notification_type</c>.</summary>
    NotificationType = 1,

    /// <summary>A <c>SessionStart</c>'s <c>source</c>.</summary>
    Source = 2,

    /// <summary>A <c>StopFailure</c>'s <c>error</c>, its error kind (T1.53).</summary>
    Error = 3,

    /// <summary>A <c>SessionEnd</c>'s <c>reason</c>.</summary>
    Reason = 4,

    /// <summary>The <c>type</c> of a running task in a <c>Stop</c>'s <c>background_tasks</c> (T1.41).</summary>
    BackgroundTaskType = 5,
}

/// <summary>One value this build does not know: the event, the field, and the value as Claude Code sent it.</summary>
/// <param name="HookEventName">The event's name.</param>
/// <param name="Field">The field.</param>
/// <param name="Raw">The value as sent; an empty string when the field was missing.</param>
public sealed record UnknownValue(string HookEventName, UnknownField Field, string Raw);
