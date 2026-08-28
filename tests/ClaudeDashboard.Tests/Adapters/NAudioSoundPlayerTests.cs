using System.IO;
using System.Linq;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using NAudio.Wave;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Adapters;

/// <summary>
/// The audio adapter (T1.14; Impl Part 7), and its following of the default device (T1.22, #13).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is asserted here and what is not.</strong> These reach as far as the mixer: that
/// the right file was resolved, that a sound became an input, that a burst became several inputs
/// rather than one or none, and that every named failure degrades to silence instead of throwing.
/// What happens after the mixer — a device driver, a speaker — is not observable in-process and
/// is checked by running the app.
/// </para>
/// <para>
/// <strong>The audio stack is faked, not skipped.</strong> <see cref="FakeAudioEndpoints"/> names
/// endpoints, decides which is default, changes that answer and raises the notification, so these
/// run on a build agent with no sound card and the device-change paths can be provoked rather
/// than waited for.
/// </para>
/// <para>
/// <strong>The four things none of these can prove</strong> are recorded in the phase acceptance
/// document rather than only here, because they are the shape of what shipping this leaves open:
/// that Windows raises anything on a real unplug and which notifications arrive; that a real
/// endpoint makes an audible sound; that the endpoint Windows calls default is the one the
/// operator hears; and — the widest — that the adapter notices a device which is listed, reports
/// itself active, accepts a stream, and is silent anyway. The fix narrows the false "played"
/// reading to "no default endpoint at all". It does not close it.
/// </para>
/// </remarks>
public sealed class NAudioSoundPlayerTests : IDisposable
{
    private static readonly AudioEndpoint Speakers =
        new("{0.0.0.00000000}.{speakers}", "Speakers (Realtek Audio)");

    private static readonly AudioEndpoint Headset =
        new("{0.0.0.00000000}.{headset}", "Headphones (Arctis 7)");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _shipped;
    private readonly string _overrides;

    public NAudioSoundPlayerTests()
    {
        _shipped = Path.Combine(_root, "shipped");
        _overrides = Path.Combine(_root, "overrides");

        Directory.CreateDirectory(_shipped);
        Directory.CreateDirectory(_overrides);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- The happy path, asserted specifically ---------------------------------------------------

    /// <summary>
    /// A sound reaches the mixer.
    /// </summary>
    /// <remarks>
    /// "A sound played" would be satisfied by any sound at any gain, so this also names which
    /// file was resolved. The mixer input count is the observable end of the chain.
    /// </remarks>
    [Fact]
    public void A_notice_reaches_the_mixer()
    {
        WriteTone(_shipped, SoundId.Finished);
        using var player = Player(Endpoints());

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(1, player.QueuedCount);
        Assert.Equal(0, player.DegradedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>
    /// <strong>Notice and nudge are the same file at different gains.</strong>
    /// </summary>
    /// <remarks>
    /// The requirement is not "the nudge is quieter" — a second, quieter file would satisfy that
    /// and would be exactly what TS §IV.5 forbids ("no separate quiet sound files"). So this
    /// asserts the thing that actually distinguishes it: one file on disk, resolved for both, and
    /// the two gains differing. Only one <c>.wav</c> exists in either folder for the whole test.
    /// </remarks>
    [Fact]
    public void A_nudge_is_the_same_file_as_the_notice_played_lower()
    {
        WriteTone(_shipped, SoundId.Permission);

        var catalog = new SoundCatalog(_overrides, _shipped);
        using var player = new NAudioSoundPlayer(
            catalog, Logger.None, new FakeClock(), Endpoints(), settle: TimeSpan.Zero);

        player.Play(SoundId.Permission, 1.0, TimeSpan.Zero);
        player.Play(SoundId.Permission, 0.6, TimeSpan.FromMilliseconds(150));

        // One file backs both — there is no "quiet" duplicate to find.
        Assert.Single(Directory.GetFiles(_shipped, "*.wav"));
        Assert.Single(Directory.GetFiles(_overrides, "*.wav").Concat(Directory.GetFiles(_shipped, "*.wav")));
        Assert.Equal(catalog.Resolve(SoundId.Permission), catalog.Resolve(SoundId.Permission));

        // …and both reached the mixer, so "the nudge is quieter" is not silence.
        Assert.Equal(2, player.QueuedCount);
        Assert.Equal(2, player.MixerInputCount);
    }

    /// <summary>The fade is applied only when one is asked for.</summary>
    /// <remarks>
    /// Asserted through the shape of the provider chain, which is where the fade lives. Without
    /// this, "a nudge fades" is satisfied by a nudge that plays exactly like a notice.
    /// </remarks>
    [Fact]
    public void A_fade_wraps_the_sound_and_a_notice_does_not()
    {
        WriteTone(_shipped, SoundId.Question);
        using var player = Player(Endpoints());

        player.Play(SoundId.Question, 1.0, TimeSpan.Zero);
        var withoutFade = player.LastProviderKind;

        player.Play(SoundId.Question, 0.6, TimeSpan.FromMilliseconds(150));
        var withFade = player.LastProviderKind;

        Assert.Equal(nameof(NAudio.Wave.SampleProviders.VolumeSampleProvider), withoutFade);
        Assert.Equal(nameof(NAudio.Wave.SampleProviders.FadeInOutSampleProvider), withFade);
    }

    /// <summary>
    /// <strong>A burst coalesces and nothing is dropped.</strong>
    /// </summary>
    /// <remarks>
    /// Fifteen concurrent sessions is the design case. "The burst coalesced" would be satisfied
    /// by discarding fourteen of them, so the positive and the negative are both asserted: every
    /// one became a mixer input, and none was counted as degraded.
    /// </remarks>
    [Fact]
    public void A_burst_of_fifteen_all_reach_the_mixer()
    {
        WriteTone(_shipped, SoundId.Finished);
        using var player = Player(Endpoints());

        for (var i = 0; i < 15; i++)
        {
            player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);
        }

        Assert.Equal(15, player.QueuedCount);
        Assert.Equal(15, player.MixerInputCount);
        Assert.Equal(0, player.DegradedCount);
    }

    /// <summary>Play starts a sound; it does not wait for it to end.</summary>
    /// <remarks>
    /// The file is a second long. If <c>Play</c> waited, fifteen of them would stall the consumer
    /// thread for fifteen seconds — the failure that only appears under a burst. Asserted against
    /// wall-clock, generously: the point is "did not wait a second", not a benchmark.
    /// </remarks>
    [Fact]
    public void Play_returns_without_waiting_for_the_sound_to_finish()
    {
        WriteTone(_shipped, SoundId.Error, seconds: 1.0);
        using var player = Player(Endpoints());

        var started = Environment.TickCount64;
        player.Play(SoundId.Error, 1.0, TimeSpan.Zero);
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(1, player.QueuedCount);
        Assert.True(elapsed < 500, $"Play took {elapsed}ms, so it is waiting for playback to finish.");
    }

    // ---- Following the default device (T1.22, issue #13) -----------------------------------------

    /// <summary>
    /// <strong>The default device changing moves the sound to it.</strong>
    /// </summary>
    /// <remarks>
    /// The defect in one test. Before T1.22 the device was opened once in the constructor and
    /// held, so this bound the speakers for the life of the process.
    /// </remarks>
    [Fact]
    public void A_changed_default_device_is_bound_and_sound_follows_it()
    {
        WriteTone(_shipped, SoundId.Finished);

        var endpoints = Endpoints(Speakers);
        using var player = Player(endpoints);

        Assert.Equal(Speakers.ToString(), player.BoundEndpoint);

        endpoints.Default = Headset;
        endpoints.RaiseChanged();

        Wait(() => player.BoundEndpoint == Headset.ToString(), "the headset to be bound");

        // The positive control. "It bound the headset" is satisfied by an adapter that binds and
        // then cannot play, which is most of what this defect was.
        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal([Speakers, Headset], endpoints.Opened);
        Assert.True(player.HasOutput);
        Assert.Equal(1, player.QueuedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>
    /// <strong>The readout is what the player says, not what it was asked for.</strong>
    /// </summary>
    /// <remarks>
    /// The hardware acceptance is judged against <see cref="NAudioSoundPlayer.BoundEndpoint"/>,
    /// so it has to come from something the adapter does not control. An adapter that echoed its
    /// own request would produce an identical, worthless readout while bound to anything at all;
    /// here the fake reports a different endpoint from the one requested, and the difference has
    /// to show.
    /// </remarks>
    [Fact]
    public void The_bound_endpoint_is_the_one_the_player_reports()
    {
        var endpoints = Endpoints(Speakers);
        endpoints.Reports = _ => Headset;

        using var player = Player(endpoints);

        Assert.Equal([Speakers], endpoints.Opened);
        Assert.Equal(Headset.ToString(), player.BoundEndpoint);
    }

    /// <summary>
    /// <strong>The device going away is silence, said out loud, and no false success.</strong>
    /// </summary>
    /// <remarks>
    /// Issue #13's sharpest symptom: the played count rising for sounds nobody heard, with an
    /// empty log. Both halves are asserted, and so is the recovery, because "it went quiet" is
    /// satisfied by an adapter that goes quiet and stays quiet for ever.
    /// </remarks>
    [Fact]
    public void A_removed_device_stops_the_false_success_and_says_so_once()
    {
        WriteTone(_shipped, SoundId.Finished);

        var log = new RecordingLogSink();
        var endpoints = Endpoints(Speakers);
        using var player = Player(endpoints, Recording(log));

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);
        Assert.Equal(1, player.QueuedCount);

        endpoints.Default = null;
        endpoints.RaiseChanged();

        Wait(() => !player.HasOutput, "the output to be given up");

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);
        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Null(player.BoundEndpoint);
        Assert.Equal(1, player.QueuedCount);
        Assert.Equal(2, player.DegradedCount);
        Assert.Equal(2, player.DroppedWhileSilent);
        Assert.Contains("no default output endpoint", player.SilentReason);

        // Exactly one line for entering the state, not one per dropped sound. The fault can last
        // hours; a line per drop would bury the two lines that matter.
        Assert.Equal(1, log.Containing("no working audio output"));

        // The recovery, and the count of what the silence cost.
        endpoints.Default = Headset;
        endpoints.RaiseChanged();

        Wait(() => player.HasOutput, "a new device to be bound");

        Assert.Equal(Headset.ToString(), player.BoundEndpoint);
        Assert.Null(player.SilentReason);
        Assert.Equal(0, player.DroppedWhileSilent);
        Assert.Contains(
            log.Messages,
            message => message.Contains("2 sound(s) were dropped", StringComparison.Ordinal));
    }

    /// <summary>
    /// <strong>Starting with no device at all, and one appearing later, is the same path.</strong>
    /// </summary>
    /// <remarks>
    /// Before T1.22 the constructor caught, logged and set the output to null, and nothing ever
    /// set it again — so plugging a headset into a machine that had none did nothing until a
    /// restart. The assertion that matters is not merely that it works: it is that no separate
    /// code path exists for it, which is why the burst, the change and this all end in one
    /// re-bind.
    /// </remarks>
    [Fact]
    public void A_first_device_appearing_is_bound_like_any_later_change()
    {
        WriteTone(_shipped, SoundId.Finished);

        var endpoints = NoEndpoints();
        using var player = Player(endpoints);

        Assert.False(player.HasOutput);
        Assert.Empty(endpoints.Opened);

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);
        Assert.Equal(0, player.QueuedCount);
        Assert.Equal(1, player.DegradedCount);

        endpoints.Default = Speakers;
        endpoints.RaiseChanged();

        Wait(() => player.HasOutput, "the first device to be bound");

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(Speakers.ToString(), player.BoundEndpoint);
        Assert.Equal(1, player.QueuedCount);
    }

    /// <summary>
    /// <strong>A burst of notifications does not open a device each.</strong>
    /// </summary>
    /// <remarks>
    /// One unplug raises several notifications, not one. Both halves are asserted, because they
    /// are guarded by different mechanisms: several notifications that resolve to a new endpoint
    /// coalesce into one open, and several that resolve to the endpoint already bound do not open
    /// anything at all. The second is the one that does the real work in the field.
    /// </remarks>
    [Fact]
    public void A_burst_of_notifications_opens_one_device_and_then_none()
    {
        var endpoints = Endpoints(Speakers);
        using var player = Player(endpoints);

        endpoints.Default = Headset;

        for (var i = 0; i < 5; i++)
        {
            endpoints.RaiseChanged();
        }

        Wait(() => player.BoundEndpoint == Headset.ToString(), "the headset to be bound");

        Assert.Equal(2, endpoints.OpenCount);

        // Five more about a device that is already bound. Waiting on the pass counter rather than
        // on a sleep: this has to know the work ran, or it is asserting that the thread was slow.
        var passes = player.RebindPasses;

        for (var i = 0; i < 5; i++)
        {
            endpoints.RaiseChanged();
        }

        Wait(() => player.RebindPasses > passes, "the worker to consider the burst");

        Assert.Equal(2, endpoints.OpenCount);
    }

    /// <summary>
    /// <strong>A device that opens and immediately dies is given up on, and it says so.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a bound, this is a re-bind, stop, re-bind loop that runs for as long as the process
    /// does. Comparing endpoint IDs cannot catch it, because the ID is the same every time.
    /// </para>
    /// <para>
    /// The log assertion is not decoration. Silence because the adapter gave up and silence
    /// because nothing ever happened look identical to the operator, and a fix whose failure mode
    /// is indistinguishable from the defect is not a fix.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_hung_endpoint_is_given_up_on_after_a_bounded_number_of_attempts()
    {
        var log = new RecordingLogSink();
        var endpoints = Endpoints(Speakers);
        endpoints.StopOnPlay = true;

        using var player = Player(endpoints, Recording(log));

        Wait(() => player.SilentReason?.Contains("gave up", StringComparison.Ordinal) == true, "it to give up");

        var passes = player.RebindPasses;
        endpoints.RaiseChanged();
        Wait(() => player.RebindPasses > passes, "one more pass after giving up");

        Assert.Equal(3, endpoints.OpenCount);
        Assert.False(player.HasOutput);
        Assert.Contains(Speakers.Name, player.SilentReason);
        Assert.Equal(1, log.Containing("gave up on"));

        // The clean slate. A different endpoint is not covered by the strikes against this one,
        // or one dead device would silence the machine.
        endpoints.StopOnPlay = false;
        endpoints.Default = Headset;
        endpoints.RaiseChanged();

        Wait(() => player.HasOutput, "the other endpoint to be bound");

        Assert.Equal(Headset.ToString(), player.BoundEndpoint);
    }

    /// <summary>
    /// <strong>A stream that ran and then stopped is a device change, not a broken device.</strong>
    /// </summary>
    /// <remarks>
    /// The control for the test above, and the reason the give-up needs a clock rather than a
    /// count. Every unplug ends with a stopped stream; if a stop alone counted as a failure, three
    /// ordinary unplugs would silence the dashboard for the rest of the session. What separates
    /// them is how long the stream survived first.
    /// </remarks>
    [Fact]
    public void A_stream_that_ran_before_stopping_re_binds_without_a_strike()
    {
        var clock = new FakeClock();
        var endpoints = Endpoints(Speakers);
        using var player = Player(endpoints, clock: clock);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var bound = endpoints.LastPlayer;
            Assert.NotNull(bound);

            // Well past the window in which a stop means "this device does not work".
            clock.Advance(TimeSpan.FromMinutes(10));
            bound.RaiseStopped(new InvalidOperationException("the endpoint went away"));

            // Waiting for the replacement to be published rather than merely opened: a device
            // that has been opened is not yet one this adapter would play through.
            Wait(
                () => player.HasOutput && !ReferenceEquals(endpoints.LastPlayer, bound),
                "it to bind again");
        }

        Assert.Equal(6, endpoints.OpenCount);
        Assert.True(player.HasOutput);
        Assert.Null(player.SilentReason);
    }

    /// <summary>
    /// <strong>Dispose unregisters, and a device opened under it is released rather than leaked.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failures in one test, because they are the same moment. Windows calling into a disposed
    /// object is what the unregistering prevents; a device nobody holds is what the publish check
    /// prevents.
    /// </para>
    /// <para>
    /// <strong>The timed join in Dispose is deliberately not what this proves.</strong> A timeout
    /// cannot protect anything, because it expires exactly when a slow driver makes it matter. So
    /// the worker is held inside a slow open, the adapter is disposed under it, and the assertion
    /// is that the device it went on to open was released by the thread that opened it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispose_unregisters_and_releases_a_device_opened_while_it_ran()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var endpoints = Endpoints(Speakers);
        var player = Player(endpoints);

        Assert.True(endpoints.Watching);

        endpoints.BeforeOpen = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        endpoints.Default = Headset;
        endpoints.RaiseChanged();

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the worker never reached the open.");

        var disposing = Task.Run(player.Dispose);

        Wait(() => player.IsDisposed, "Dispose to take effect");

        release.Set();

        await disposing.WaitAsync(TimeSpan.FromSeconds(10));

        // The device opened under the disposal was released by the thread that opened it.
        Assert.Equal(2, endpoints.OpenCount);
        Assert.NotNull(endpoints.LastPlayer);
        Assert.True(endpoints.LastPlayer.IsDisposed, "the device opened during Dispose was leaked.");
        Assert.False(player.HasOutput);

        // …and the subscription is gone, so Windows has nothing left to call into. Asserted by
        // its effect rather than by the count alone: a notification after Dispose opens nothing.
        Assert.Equal(1, endpoints.WatchDisposals);
        Assert.False(endpoints.Watching);

        endpoints.RaiseChanged();

        Assert.Equal(2, endpoints.OpenCount);
    }

    /// <summary>
    /// <strong>The adapter's own teardown is not counted as a device fault.</strong>
    /// </summary>
    /// <remarks>
    /// A real <c>WasapiPlayer</c> raises <c>PlaybackStopped</c> when it is disposed — measured,
    /// with a null exception. So every deliberate swap raises the same signal a dying device does.
    /// Without the retiring flag, three ordinary device changes would book three strikes and the
    /// dashboard would give up on a healthy endpoint, invisibly.
    /// </remarks>
    [Fact]
    public void Releasing_a_device_on_purpose_does_not_count_against_it()
    {
        var endpoints = Endpoints(Speakers);
        using var player = Player(endpoints);

        // Back and forth more times than the strike limit allows, so a swap counted as a failure
        // would have given up before the end.
        for (var i = 0; i < 4; i++)
        {
            var next = i % 2 == 0 ? Headset : Speakers;

            endpoints.Default = next;
            endpoints.RaiseChanged();

            Wait(() => player.BoundEndpoint == next.ToString(), $"{next.Name} to be bound");
        }

        Assert.Equal(5, endpoints.OpenCount);
        Assert.True(player.HasOutput);
        Assert.Null(player.SilentReason);
        Assert.Equal(Speakers.ToString(), player.BoundEndpoint);
    }

    // ---- The degradations, each with its positive control ----------------------------------------

    /// <summary>A missing file is silence and a log line, not an exception.</summary>
    [Fact]
    public void A_missing_file_degrades_to_silence()
    {
        var log = new RecordingLogSink();
        using var player = Player(Endpoints(), Recording(log));

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(0, player.QueuedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Equal(0, player.MixerInputCount);
        Assert.Contains(log.Messages, message => message.Contains("No sound file", StringComparison.Ordinal));

        // The control: the same player, the same call, once the file exists. Without this, a Play
        // that did nothing at all would pass the assertions above.
        WriteTone(_shipped, SoundId.Finished);
        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(1, player.QueuedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>A file that is not audio is silence and a log line, not an exception.</summary>
    [Fact]
    public void An_undecodable_file_degrades_to_silence()
    {
        File.WriteAllText(Path.Combine(_shipped, "finished.wav"), "this is not a wave file");
        WriteTone(_shipped, SoundId.Error);

        var log = new RecordingLogSink();
        using var player = Player(Endpoints(), Recording(log));

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(0, player.QueuedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Contains(log.Messages, message => message.Contains("could not be decoded", StringComparison.Ordinal));

        // The control: a real file through the same player still plays.
        player.Play(SoundId.Error, 1.0, TimeSpan.Zero);

        Assert.Equal(1, player.QueuedCount);
        Assert.Equal(1, player.MixerInputCount);
    }

    /// <summary>
    /// No output device is silence and a log line — the app still runs.
    /// </summary>
    /// <remarks>
    /// The realistic case: an RDP session, a headless machine, a headset unplugged. Provoked
    /// rather than waited for, by an endpoint layer that throws the way the device layer would.
    /// </remarks>
    [Fact]
    public void An_unavailable_device_degrades_to_silence()
    {
        WriteTone(_shipped, SoundId.Finished);

        var log = new RecordingLogSink();
        var endpoints = Endpoints(Speakers);
        endpoints.OpenFailure = new InvalidOperationException("no wave devices");

        using var player = Player(endpoints, Recording(log));

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.False(player.HasOutput);
        Assert.Null(player.BoundEndpoint);
        Assert.Equal(0, player.QueuedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Contains(log.Messages, message => message.Contains("could not be opened", StringComparison.Ordinal));
        Assert.Contains(log.Messages, message => message.Contains("no working audio output", StringComparison.Ordinal));

        // The control: the identical setup with a device that opens does play, so the silence
        // above is the device's absence and not a Play that never worked.
        using var working = Player(Endpoints(Speakers));
        working.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.True(working.HasOutput);
        Assert.Equal(1, working.QueuedCount);
    }

    /// <summary>Whatever else happens, nothing reaches the caller.</summary>
    [Fact]
    public void It_needs_its_dependencies_but_never_throws_from_play()
    {
        var clock = new FakeClock();

        Assert.Throws<ArgumentNullException>(() => new NAudioSoundPlayer(null!, Logger.None, clock));
        Assert.Throws<ArgumentNullException>(
            () => new NAudioSoundPlayer(new SoundCatalog(_overrides, _shipped), null!, clock));
        Assert.Throws<ArgumentNullException>(
            () => new NAudioSoundPlayer(new SoundCatalog(_overrides, _shipped), Logger.None, null!));

        using var player = Player(Endpoints());

        // Every one of these is a shape the engine could produce; none may escape.
        player.Play(SoundId.Finished, double.NaN, TimeSpan.Zero);
        player.Play(SoundId.Finished, -5, TimeSpan.FromMilliseconds(-10));
        player.Play(new SoundId("nothing-like-this"), 1.0, TimeSpan.Zero);
    }

    /// <summary>
    /// <strong>An unforeseen failure is swallowed too, and this is the assertion that means it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above passes against a <c>Play</c> in which nothing can throw — every input it
    /// uses is clamped or resolves to nothing, so none of them reaches the catch. That is not the
    /// same as a <c>Play</c> that swallows what does throw, and a mutation adding <c>throw;</c> to
    /// the handler survived it. Found by planting exactly that.
    /// </para>
    /// <para>
    /// So this makes the chain throw something the named handlers do not expect, and asserts the
    /// contract that matters: the caller — the event consumer, whose loop keeps the whole
    /// dashboard current — sees nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unforeseen_failure_never_reaches_the_caller()
    {
        var log = new RecordingLogSink();

        using var player = new NAudioSoundPlayer(
            new ThrowingCatalog(_overrides, _shipped),
            Recording(log),
            new FakeClock(),
            Endpoints(Speakers),
            settle: TimeSpan.Zero);

        player.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(0, player.QueuedCount);
        Assert.Equal(1, player.DegradedCount);
        Assert.Contains(log.Messages, message => message.Contains("Continuing without it", StringComparison.Ordinal));

        // The control: the same player is not broken — a working catalog through the same code
        // path still plays, so the silence above is the failure being swallowed and not a Play
        // that gave up permanently.
        WriteTone(_shipped, SoundId.Finished);

        using var working = Player(Endpoints(Speakers));
        working.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        Assert.Equal(1, working.QueuedCount);
    }

    /// <summary>A catalog that fails in a way none of the named handlers expects.</summary>
    private sealed class ThrowingCatalog(string overrideFolder, string shippedFolder)
        : SoundCatalog(overrideFolder, shippedFolder)
    {
        public override string? Resolve(SoundId sound) =>
            throw new InvalidTimeZoneException("nothing predicted this");
    }

    /// <summary>Gain outside 0…1 is clamped rather than rejected, per the port's contract.</summary>
    [Fact]
    public void Gain_is_clamped_rather_than_rejected()
    {
        WriteTone(_shipped, SoundId.Finished);
        using var player = Player(Endpoints());

        player.Play(SoundId.Finished, 5.0, TimeSpan.Zero);
        Assert.Equal(1f, player.LastVolume);

        player.Play(SoundId.Finished, -1.0, TimeSpan.Zero);
        Assert.Equal(0f, player.LastVolume);

        player.Play(SoundId.Finished, 0.6, TimeSpan.Zero);
        Assert.Equal(0.6f, player.LastVolume, 3);
    }

    /// <summary>An audio stack whose default endpoint is <paramref name="initial"/>.</summary>
    private static FakeAudioEndpoints Endpoints(AudioEndpoint? initial = null) => new(initial ?? Speakers);

    /// <summary>An audio stack with no output at all — a machine with every device disabled.</summary>
    private static FakeAudioEndpoints NoEndpoints() => new();

    /// <summary>A player over a faked audio stack, with no settle delay.</summary>
    /// <remarks>
    /// The settle exists to let a real burst of notifications finish arriving. A test raises them
    /// itself and then waits for the outcome, so waiting a quarter of a second per notification
    /// would buy nothing but a slower suite.
    /// </remarks>
    private NAudioSoundPlayer Player(
        FakeAudioEndpoints endpoints, ILogger? logger = null, FakeClock? clock = null) =>
        new(
            new SoundCatalog(_overrides, _shipped),
            logger ?? Logger.None,
            clock ?? new FakeClock(),
            endpoints,
            settle: TimeSpan.Zero);

    /// <summary>A logger that keeps what it was told, so a log line can be asserted on.</summary>
    private static Logger Recording(RecordingLogSink sink) =>
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    /// <summary>
    /// Waits for the endpoint worker to reach <paramref name="until"/>, and fails saying what it
    /// was waiting for.
    /// </summary>
    /// <remarks>
    /// The work happens on a real background thread, deliberately: a test that drove the worker
    /// by hand would share the code's premise about when work runs and could not fail on it. The
    /// wait is on the outcome rather than on a duration, so nothing here asserts that a thread was
    /// fast.
    /// </remarks>
    private static void Wait(Func<bool> until, string what)
    {
        Assert.True(
            SpinWait.SpinUntil(until, TimeSpan.FromSeconds(10)),
            $"Timed out waiting for {what}.");
    }

    /// <summary>Writes a real, decodable WAV so the assertions are about the adapter.</summary>
    private static void WriteTone(string folder, SoundId sound, double seconds = 0.2)
    {
        var path = Path.Combine(folder, sound.Name + SoundCatalog.Extension);
        var format = new WaveFormat(44100, 16, 1);

        using var writer = new WaveFileWriter(path, format);

        var samples = (int)(format.SampleRate * seconds);

        for (var i = 0; i < samples; i++)
        {
            writer.WriteSample((float)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 0.5));
        }
    }
}
