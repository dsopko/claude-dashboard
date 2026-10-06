using ClaudeDashboard.Core.Events;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// Says in the log file, once, that Claude Code sent a value this build does not know (T1.79, issue #9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why:</strong> an unknown value changes nothing, which is safe, but before T1.79 nothing said that it had
/// arrived. A new notification type that should turn a row red would have been ignored without a word. The
/// database keeps every event with its whole body; the log file at the normal level says what someone should
/// notice, and an unknown value is that.
/// </para>
/// <para>
/// <strong>One Information line for each event, field and value, for the life of the process</strong>, not one
/// for each arrival, so a common unknown type cannot fill the file. The memory holds at most
/// <see cref="MaxValues"/> values; after that, one more line says that no more are named in this run, so a broken
/// or hostile sender cannot grow it without end. The value is written by <see cref="EventValues.Shown"/>.
/// </para>
/// <para>
/// <strong>On the consumer thread only</strong>, like the Registry: the memory has one writer and needs no lock.
/// </para>
/// </remarks>
public sealed class UnknownValueLog
{
    /// <summary>The most values the memory holds, and so the most lines this writes in one run, before the last line.</summary>
    public const int MaxValues = 100;

    private readonly ILogger _logger;
    private readonly HashSet<(string HookEventName, UnknownField Field, string Shown)> _seen = [];
    private bool _full;

    /// <summary>Creates the log for one consumer.</summary>
    /// <param name="logger">The dashboard's logger.</param>
    public UnknownValueLog(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <summary>How many values the memory holds.</summary>
    public int Count => _seen.Count;

    /// <summary>Writes one line for each value of the event that this build does not know and this run has not named.</summary>
    public void Note(InboundEvent inboundEvent)
    {
        foreach (var unknown in EventValues.UnknownsOf(inboundEvent))
        {
            var shown = EventValues.Shown(unknown.Raw);
            var key = (unknown.HookEventName, unknown.Field, shown);

            if (_seen.Contains(key))
            {
                continue;
            }

            if (_seen.Count >= MaxValues)
            {
                if (!_full)
                {
                    _full = true;
                    _logger.Information(
                        "Claude Code sent more values that this build does not know. The log file names no more of them in this run; the database keeps every event.");
                }

                continue;
            }

            _seen.Add(key);
            Write(unknown, shown);
        }
    }

    private void Write(UnknownValue unknown, string shown)
    {
        switch (unknown.Field)
        {
            case UnknownField.NotificationType:
                _logger.Information(
                    "Claude Code sent a Notification of type {Value}, which this build does not know. It changed nothing.",
                    shown);
                break;

            case UnknownField.BackgroundTaskType:
                _logger.Information(
                    "Claude Code sent a Stop with a running background task of type {Value}, which this build does not know. The dashboard does not wait for that task.",
                    shown);
                break;

            default:
                // A start source, an error kind or an end reason: no rule reads the value, so the event does what
                // any event of its name does.
                _logger.Information(
                    "Claude Code sent a {HookEventName:l} with {Field:l} {Value}, which this build does not know. The value changed nothing; the event was handled as usual.",
                    unknown.HookEventName,
                    WireName(unknown.Field),
                    shown);
                break;
        }
    }

    private static string WireName(UnknownField field) => field switch
    {
        UnknownField.NotificationType => "notification_type",
        UnknownField.Source => "source",
        UnknownField.Error => "error",
        UnknownField.Reason => "reason",
        UnknownField.BackgroundTaskType => "background task type",
        _ => field.ToString(),
    };
}
