using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Keeps the <see cref="StateReport"/> that <c>/state</c> serves (T1.46), built on the consumer
/// thread and read on a request thread.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The request thread never touches the Registry.</strong> <see cref="SessionRegistry"/>
/// has one writer and no locks, and <see cref="SessionRegistry.Sessions"/> is a live view that
/// throws when enumerated mid-apply — the T1.2 review hit exactly that. So this copies
/// <see cref="Ui.SessionProjection"/>: it hears <see cref="SessionRegistry.SessionChanged"/> on
/// the consumer thread, takes the immutable <see cref="Session"/> out of the event arguments, and
/// keeps its own copy. It never reads <see cref="SessionRegistry.Sessions"/> at all.
/// </para>
/// <para>
/// <strong>Published by swapping one reference.</strong> Each change builds a whole new
/// <see cref="StateReport"/> on the consumer thread and writes it to one field with a volatile
/// write; a request reads that field once with a volatile read and serializes what it got. Chosen
/// over a lock because the two sides then never wait for each other: a slow client cannot hold
/// the consumer, which is the Registry's only writer, and a request never sees a report half
/// built. Chosen over a snapshot built on the request because that would mean reading the sound
/// engine's dictionary off its thread. The cost is one small allocation per change, across about
/// fifteen sessions.
/// </para>
/// <para>
/// <strong>The nudge time is read here, on the consumer thread.</strong>
/// <see cref="SoundPolicyEngine.NextNudgeAt"/> reads a plain dictionary the consumer writes. Two
/// things move it. A session change moves it inside the sound engine's own
/// <c>SessionChanged</c> handler — so this board must be subscribed <em>after</em> that handler,
/// which <c>AppHost</c> guarantees by resolving it after wiring the engine. A nudge firing moves
/// it with no session change at all, so this also hears
/// <see cref="SoundPolicyEngine.NudgeScheduleAdvanced"/>.
/// </para>
/// <para>
/// <strong>Degrade, never crash.</strong> Both handlers run inside the consumer's apply and tick.
/// A failure here keeps the last good report and is logged; it never reaches the consumer.
/// </para>
/// <para>
/// <strong>A roster's settle changes the report with no session change</strong> (T1.83, issue #130). The bands
/// count a roster group once, at its roll-up, and the roll-up moves from Working to Unread when the settle window
/// runs out, which no event marks. So the consumer calls <see cref="Restate"/> after each settle pass: at the settle
/// deadline (its settle wake), and after each drain, which is also how a roster edit arrives.
/// </para>
/// </remarks>
public sealed class StateBoard : IDisposable
{
    private readonly SessionRegistry _registry;
    private readonly SoundPolicyEngine _sound;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Configuration.RosterStore _rosters;

    /// <summary>The consumer thread's own copy. Only ever touched on that thread.</summary>
    private readonly Dictionary<SessionId, Session> _sessions = [];

    private StateReport _current;
    private bool _disposed;

    /// <summary>Starts keeping the report for <paramref name="registry"/>.</summary>
    /// <param name="registry">The Registry whose changes it hears.</param>
    /// <param name="sound">The sound engine, for the nudge schedule.</param>
    /// <param name="clock">The instant each report is built at.</param>
    /// <param name="logger">Where a failure to build a report is logged.</param>
    /// <param name="rosters">
    /// The rosters (T1.83): a roster group counts once in the bands. Required, because the container builds this
    /// board, and an optional registered collaborator could go missing without a failure.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public StateBoard(
        SessionRegistry registry,
        SoundPolicyEngine sound,
        IClock clock,
        ILogger logger,
        Configuration.RosterStore rosters)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(rosters);

        _registry = registry;
        _sound = sound;
        _clock = clock;
        _logger = logger;
        _rosters = rosters;
        _current = StateReport.Empty(clock.Now);

        _registry.SessionChanged += OnSessionChanged;
        _sound.NudgeScheduleAdvanced += OnNudgeScheduleAdvanced;
    }

    /// <summary>The last report the consumer thread published. Safe to read from any thread.</summary>
    public StateReport Current => Volatile.Read(ref _current);

    /// <summary>Stops keeping the report.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _registry.SessionChanged -= OnSessionChanged;
        _sound.NudgeScheduleAdvanced -= OnNudgeScheduleAdvanced;
        _disposed = true;
    }

    /// <summary>Runs on the consumer thread.</summary>
    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        _sessions[e.Session.Id] = e.Session;
        Publish();
    }

    /// <summary>Runs on the consumer thread, inside the tick.</summary>
    private void OnNudgeScheduleAdvanced(object? sender, EventArgs e) => Publish();

    /// <summary>
    /// Builds the report again at the clock's instant, with no session change: a roster's settle (T1.83). Called on the
    /// consumer thread, after each settle pass.
    /// </summary>
    public void Restate() => Publish();

    private void Publish()
    {
        try
        {
            Volatile.Write(ref _current, StateReport.Of(_sessions.Values, _sound.NextNudgeAt, _clock.Now, _rosters.Book));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Building the /state report failed. It keeps the last one; the pipeline continues.");
        }
    }
}
