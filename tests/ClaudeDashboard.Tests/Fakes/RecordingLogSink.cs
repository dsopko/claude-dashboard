using System.Globalization;
using System.IO;
using Serilog.Core;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// A Serilog sink that keeps what was written, so a test can count log lines.
/// </summary>
/// <remarks>
/// Used where the <em>number</em> of lines is the behaviour under test — the storm guard — rather
/// than their content. Reading the file back would work too and would say nothing extra, since
/// the question is "how many", not "did the sink work"; the file-based assertions in
/// <c>AppHostTests</c> stay where they are for the wiring they cover.
/// </remarks>
public sealed class RecordingLogSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];
    private readonly Lock _gate = new();

    /// <summary>Everything written so far, oldest first.</summary>
    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The rendered messages, oldest first.</summary>
    public IReadOnlyList<string> Messages =>
        [.. Events.Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture))];

    /// <summary>How many messages contain <paramref name="text"/>.</summary>
    public int Containing(string text) =>
        Messages.Count(message => message.Contains(text, StringComparison.Ordinal));

    /// <summary>The messages that contain <paramref name="text"/>.</summary>
    public IReadOnlyList<string> Matching(string text) =>
        [.. Messages.Where(message => message.Contains(text, StringComparison.Ordinal))];

    /// <summary>How many events carry an exception — a first sighting, in the storm guard's terms.</summary>
    public int WithException => Events.Count(entry => entry.Exception is not null);

    /// <summary>Forgets everything written so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        lock (_gate)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>The whole log, for a failure message.</summary>
    public override string ToString() => string.Join(Environment.NewLine, Messages);

    /// <summary>Renders one event, exception included, for a failure message.</summary>
    public static string Render(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        logEvent.RenderMessage(writer, CultureInfo.InvariantCulture);
        return writer.ToString();
    }
}
