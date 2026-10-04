using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.App.Ui;

/// <summary>Something that shows which row made a sound (T1.67, issue #99).</summary>
public interface ISoundSignTarget
{
    /// <summary>
    /// The player queued <paramref name="sound"/> at <paramref name="at"/>, for the row of
    /// <paramref name="session"/>. Called on the UI thread.
    /// </summary>
    void SoundPlayed(SessionId session, SoundId sound, DateTimeOffset at);
}

/// <summary>
/// Carries each sound that played from the sound engine to the window, so the row that made it
/// shows the speaker sign (T1.67, issue #99).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One post for each sound that played, never one for each event.</strong> The engine
/// raises <see cref="SoundPolicyEngine.SoundMarked"/> on the consumer thread, only for a sound the
/// player queued, with the session it has already decided. This captures the three values and posts
/// them through the existing dispatcher, the way <see cref="SessionProjection"/> posts a session. It
/// does no work of its own on the consumer thread, takes no lock, and writes nothing to the Registry.
/// </para>
/// <para>
/// <strong>Attached, as the tick's targets are (<see cref="IUiTickTarget"/>).</strong> The window's
/// view model is built on the UI thread by <c>Program</c>, which hands it over. A sound before that
/// reaches no row, and nothing is kept for later: the sign lives in memory and only for its minute.
/// </para>
/// <para>
/// No log line. A sign is a display, and the decisions record already has the sound.
/// </para>
/// </remarks>
public sealed class SoundSigns : IDisposable
{
    private readonly SoundPolicyEngine _engine;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>
    /// Everything that shows signs. Swapped whole, so the consumer thread always reads a complete
    /// array.
    /// </summary>
    private ISoundSignTarget[] _targets = [];

    private bool _disposed;

    /// <summary>Starts listening to <paramref name="engine"/>.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SoundSigns(SoundPolicyEngine engine, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _engine = engine;
        _dispatcher = dispatcher;
        _engine.SoundMarked += OnSoundMarked;
    }

    /// <summary>How many sounds have been posted to the UI thread. Diagnostic only.</summary>
    public long PostedCount { get; private set; }

    /// <summary>Starts showing signs on <paramref name="target"/>. Called on the UI thread.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    public void Attach(ISoundSignTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        Volatile.Write(ref _targets, [.. Volatile.Read(ref _targets), target]);
    }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _engine.SoundMarked -= OnSoundMarked;
        _disposed = true;
    }

    /// <summary>Runs on the consumer thread: captures the values, posts once, and returns.</summary>
    private void OnSoundMarked(object? sender, SoundMarkedEventArgs e)
    {
        var targets = Volatile.Read(ref _targets);

        if (targets.Length == 0)
        {
            return;
        }

        var session = e.Session;
        var sound = e.Sound;
        var at = e.At;

        PostedCount++;

        _dispatcher.Post(() =>
        {
            foreach (var target in targets)
            {
                target.SoundPlayed(session, sound, at);
            }
        });
    }
}
