using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// A global sound mode published from the tray travels the Channel and reaches the engine
/// (T1.13; Impl §4, §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists as a pipeline test.</strong> Asserting that the tray puts a
/// <see cref="SoundCommand"/> on the channel proves the menu is wired; it says nothing about
/// whether anything reads it. A consumer that dropped every <see cref="SoundCommand"/> would
/// leave the whole suite green while "Pause monitoring" did precisely nothing — the operator
/// clicks it, the glyph stays coloured, the sounds keep coming, and no test anywhere notices.
/// That mutation was planted and it survived until this file existed.
/// </para>
/// <para>
/// So the assertions here are on the <em>far</em> end: the engine's mode, and then the port. The
/// proof that mute works is that <see cref="ClaudeDashboard.Core.Ports.ISoundPlayer.Play"/> is
/// not called at all.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class SoundCommandPipelineTests : IAsyncLifetime
{
    private const string Workspace = @"C:\dev\PennCustQuote";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);

    private SoundPolicyEngine _sound = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _sound = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions());
        _registry.SessionChanged += (_, e) => _sound.OnSessionChanged(e.Session);

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            _sound,
            _clock,
            _guard,
            Logger.None,
            new RecordingUiTick(),
            _archive,
            tickInterval: TimeSpan.FromMilliseconds(25));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>Pause reaches the engine, and then the port goes quiet.</summary>
    [Fact]
    public async Task Pause_travels_the_channel_and_silences_the_port()
    {
        Assert.True(_pipeline.Sink.TryPublish(Command(SoundCommandKind.PauseMonitoring)));

        Assert.True(
            await Until(() => _sound.IsMonitoringPaused),
            "the pause command never reached the engine — nothing is routing SoundCommand.");

        // …and the effect, which is the half that matters to the operator.
        Assert.True(_pipeline.Sink.TryPublish(Blocked("s-1")));
        Assert.True(await Until(() => _consumer.AppliedCount >= 1));

        Assert.Empty(_player.Played);
    }

    /// <summary>Resume brings the sound back, so pause is not a one-way door.</summary>
    [Fact]
    public async Task Resume_travels_the_channel_and_lets_sound_through()
    {
        _pipeline.Sink.TryPublish(Command(SoundCommandKind.PauseMonitoring));
        Assert.True(await Until(() => _sound.IsMonitoringPaused));

        _pipeline.Sink.TryPublish(Command(SoundCommandKind.ResumeMonitoring));
        Assert.True(await Until(() => !_sound.IsMonitoringPaused));

        _pipeline.Sink.TryPublish(Blocked("s-2"));

        Assert.True(
            await Until(() => _player.Played.Count > 0),
            "resuming did not let the next notice through.");
    }

    /// <summary>A timed mute arrives carrying its expiry, not a duration the engine invents.</summary>
    [Fact]
    public async Task A_timed_mute_arrives_with_its_expiry()
    {
        var until = At.AddMinutes(30);

        _pipeline.Sink.TryPublish(Command(SoundCommandKind.MuteAll, until));

        Assert.True(await Until(() => _sound.AllMutedUntil is not null));
        Assert.Equal(until, _sound.AllMutedUntil);
    }

    /// <summary>Unmute clears it.</summary>
    [Fact]
    public async Task Unmute_clears_the_mute()
    {
        _pipeline.Sink.TryPublish(Command(SoundCommandKind.MuteAll));
        Assert.True(await Until(() => _sound.AllMutedUntil is not null));

        _pipeline.Sink.TryPublish(Command(SoundCommandKind.UnmuteAll));

        Assert.True(await Until(() => _sound.AllMutedUntil is null));
    }

    /// <summary>
    /// A sound command is not session state: the Registry never sees one.
    /// </summary>
    /// <remarks>
    /// The command rides the same channel as hooks so that it lands on the consumer thread in
    /// order with them, which is the only reason it is an <see cref="InboundEvent"/> at all. If
    /// it reached <see cref="SessionRegistry.Apply"/> it would arrive naming no session, and the
    /// Registry would have to grow a case for something that is not about a session.
    /// </remarks>
    [Fact]
    public async Task A_sound_command_never_reaches_the_registry()
    {
        _pipeline.Sink.TryPublish(Command(SoundCommandKind.PauseMonitoring));

        Assert.True(await Until(() => _consumer.SoundCommandCount >= 1));

        Assert.Empty(_registry.Sessions);
        Assert.Equal(0, _consumer.AppliedCount);
        Assert.Equal(0, _consumer.DeclinedCount);
    }

    private static SoundCommand Command(SoundCommandKind kind, DateTimeOffset? until = null) =>
        new()
        {
            SessionId = default,
            Timestamp = At,
            Cwd = string.Empty,
            Kind = kind,
            Until = until,
        };

    private static Notification Blocked(string id) =>
        new()
        {
            SessionId = new SessionId(id),
            Timestamp = At,
            Cwd = Workspace,
            NotificationType = "permission_prompt",
        };

    private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }
}
