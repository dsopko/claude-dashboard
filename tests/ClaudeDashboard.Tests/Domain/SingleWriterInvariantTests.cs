using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Pipeline;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// That the single-writer invariant is enforced by the mutators themselves, for every caller,
/// rather than by the callers remembering to ask (Impl §2.2, §4).
/// </summary>
/// <remarks>
/// <para>
/// Every "is caught" test here has a matching "is not caught" test, because a guard that fires
/// on everything would satisfy the first half and be useless — an assertion satisfiable by a
/// degenerate implementation. The legitimate path is nested two deep in normal operation, so
/// "never fires" and "always fires" are both wrong and both have to be excluded.
/// </para>
/// <para>
/// Contention is arranged deterministically: one thread holds the region open while another
/// arrives. Racing two threads and hoping their microsecond windows overlap would prove nothing
/// in either direction.
/// </para>
/// </remarks>
public sealed class SingleWriterInvariantTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly SingleWriterGuard _guard = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly FakeClock _clock = new();
    private readonly SessionRegistry _registry;
    private readonly SoundPolicyEngine _sound;

    public SingleWriterInvariantTests()
    {
        _registry = new SessionRegistry(_guard);
        _sound = new SoundPolicyEngine(_player, _clock, options: null, guard: _guard);
    }

    private static UserPromptSubmit Prompt(string sessionId, DateTimeOffset stamp, string promptId = "p-1") => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = stamp,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "p",
    };

    private static Session Blocked(string sessionId, DateTimeOffset enteredAt)
    {
        var id = new SessionId(sessionId);
        return new Session
        {
            Id = id,
            State = SessionState.NeedsPermission,
            Latest = new Exchange { Prompt = "p", StartedAt = enteredAt },
            Cwd = @"C:\w",
            Group = GroupKeys.ForWorkspace(@"C:\w"),
            EnteredAt = enteredAt,
            LastActivity = enteredAt,
        };
    }

    /// <summary>Runs <paramref name="mutate"/> on another thread and returns what it threw.</summary>
    private static Exception? OnAnotherThread(Action mutate)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                mutate();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The second thread did not finish.");

        return captured;
    }

    // ---- What must be caught ------------------------------------------------------------------

    /// <summary>
    /// The case that was unguarded until now: something resolves the Registry from DI and calls
    /// <c>Apply</c> directly, never going near <c>EventConsumer</c> or its wrapper.
    /// </summary>
    [Fact]
    public void A_direct_Apply_from_another_thread_is_caught()
    {
        using (_guard.Enter("the consumer, mid-event"))
        {
            var thrown = OnAnotherThread(() => _registry.Apply(Prompt("s-1", At)));

            Assert.IsType<SingleWriterViolationException>(thrown);
        }

        Assert.Equal(1, _guard.ViolationCount);
    }

    /// <summary>
    /// <strong>The T1.13 shape, which three reviews flagged and nothing prevented.</strong> The
    /// tray menu's mute is the caller that will reach for these from the Dispatcher.
    /// </summary>
    [Theory]
    [InlineData("session")]
    [InlineData("group")]
    public void A_mute_change_from_another_thread_is_caught(string scope)
    {
        using (_guard.Enter("the consumer, mid-event"))
        {
            var thrown = OnAnotherThread(() =>
            {
                if (scope == "session")
                {
                    _sound.SetSessionMuted(new SessionId("s-1"), muted: true);
                }
                else
                {
                    _sound.SetGroupMuted(GroupKeys.ForWorkspace(@"C:\w"), muted: true);
                }
            });

            Assert.IsType<SingleWriterViolationException>(thrown);
        }

        Assert.Equal(1, _guard.ViolationCount);
    }

    /// <summary>
    /// <strong>The T1.5 race, converted.</strong> <c>Evaluate</c> enumerates the tracked
    /// sessions while mutating them and <c>OnSessionChanged</c> adds and removes from the same
    /// dictionary, which is why the review could make it throw
    /// <see cref="InvalidOperationException"/> from the collection within a few hundred
    /// iterations. It now fails as the guard's own exception instead — the same collision,
    /// named, at the moment it happens, rather than a mangled enumeration.
    /// </summary>
    [Fact]
    public void Evaluate_racing_a_session_change_is_caught_as_a_violation_not_a_mangled_collection()
    {
        _sound.OnSessionChanged(Blocked("s-1", At));

        using (_guard.Enter("evaluating the nudge schedule"))
        {
            var thrown = OnAnotherThread(() => _sound.OnSessionChanged(Blocked("s-2", At)));

            var violation = Assert.IsType<SingleWriterViolationException>(thrown);
            Assert.Contains("evaluating the nudge schedule", violation.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>And the other direction: a change in progress, an evaluation arriving.</summary>
    [Fact]
    public void A_session_change_racing_evaluate_is_caught()
    {
        using (_guard.Enter("recording a session change in the sound engine"))
        {
            var thrown = OnAnotherThread(() => _sound.Evaluate(_clock.Now));

            Assert.IsType<SingleWriterViolationException>(thrown);
        }
    }

    /// <summary>Every mutator, so none is left opt-in.</summary>
    [Fact]
    public void Every_mutator_enters_the_region()
    {
        var mutators = new (string Name, Action Call)[]
        {
            ("SessionRegistry.Apply", () => _registry.Apply(Prompt("s-9", At))),
            ("SoundPolicyEngine.OnSessionChanged", () => _sound.OnSessionChanged(Blocked("s-9", At))),
            ("SoundPolicyEngine.Evaluate", () => _sound.Evaluate(_clock.Now)),
            ("SoundPolicyEngine.SetSessionMuted", () => _sound.SetSessionMuted(new SessionId("s-9"), true)),
            ("SoundPolicyEngine.SetGroupMuted", () => _sound.SetGroupMuted(GroupKeys.ForWorkspace(@"C:\w"), true)),
        };

        using var held = _guard.Enter("the consumer, mid-event");

        foreach (var (name, call) in mutators)
        {
            Assert.True(
                OnAnotherThread(call) is SingleWriterViolationException,
                $"{name} does not enter the single-writer region, so a second thread can reach it unnoticed.");
        }
    }

    // ---- What must NOT be caught ----------------------------------------------------------------

    /// <summary>
    /// The legitimate path, which must stay silent. Without this the guard could fire on
    /// everything and every "is caught" test above would still pass.
    /// </summary>
    [Fact]
    public void The_ordinary_single_threaded_path_never_fires()
    {
        for (var i = 0; i < 200; i++)
        {
            _registry.Apply(Prompt($"s-{i % 7}", At.AddSeconds(i), $"p-{i}"));
            _sound.Evaluate(_clock.Now);
        }

        _sound.SetSessionMuted(new SessionId("s-1"), muted: true);
        _sound.SetGroupMuted(GroupKeys.ForWorkspace(@"C:\w"), muted: true);

        Assert.Equal(0, _guard.ViolationCount);
    }

    /// <summary>
    /// <strong>Re-entrancy, which is the ordinary path rather than a corner case.</strong>
    /// Applying an event raises a change notification, and the handler enters the sound engine —
    /// so the normal flow is already nested two deep on one thread. A guard that refused that
    /// would break the pipeline while looking correct in every contention test.
    /// </summary>
    [Fact]
    public void The_registry_notifying_the_sound_engine_on_one_thread_never_fires()
    {
        _registry.SessionChanged += (_, e) => _sound.OnSessionChanged(e.Session);

        for (var i = 0; i < 50; i++)
        {
            _registry.Apply(Prompt($"s-{i}", At.AddSeconds(i), $"p-{i}"));
        }

        Assert.Equal(0, _guard.ViolationCount);
        Assert.Equal(50, _registry.Sessions.Count);
    }

    /// <summary>
    /// Different threads are fine as long as they do not overlap — the invariant is one thread
    /// <em>at a time</em>, not one thread forever. A guard that pinned an owner at startup would
    /// fail this, and would also fail any host that restarts its consumer.
    /// </summary>
    [Fact]
    public void Threads_taking_turns_never_fire()
    {
        for (var i = 0; i < 20; i++)
        {
            var round = i;
            var thrown = OnAnotherThread(() => _registry.Apply(Prompt($"s-{round}", At.AddSeconds(round))));

            Assert.Null(thrown);
        }

        Assert.Equal(0, _guard.ViolationCount);
        Assert.Equal(20, _registry.Sessions.Count);
    }

    /// <summary>
    /// A refused entrant must leave the region as it found it, or one stray caller would wedge
    /// the pipeline permanently — trading an intermittent race for a total stall.
    /// </summary>
    [Fact]
    public void A_refused_caller_does_not_wedge_the_region()
    {
        using (_guard.Enter("the consumer, mid-event"))
        {
            OnAnotherThread(() => _registry.Apply(Prompt("s-1", At)));
        }

        Assert.Equal(ApplyOutcome.Applied, _registry.Apply(Prompt("s-2", At)));
        Assert.Equal(1, _guard.ViolationCount);
    }

    /// <summary>
    /// Registries built without a shared guard get their own, so an isolated unit test is not
    /// accidentally contending with an unrelated one.
    /// </summary>
    [Fact]
    public void Independently_constructed_registries_do_not_contend()
    {
        var first = new SessionRegistry();
        var second = new SessionRegistry();

        Assert.Equal(ApplyOutcome.Applied, first.Apply(Prompt("s-1", At)));
        Assert.Null(OnAnotherThread(() => second.Apply(Prompt("s-2", At))));
    }

    // ---- Under real concurrency ---------------------------------------------------------------------

    /// <summary>
    /// The whole point, at the scale it will actually happen: many threads calling mutators
    /// directly, which is what T1.13 and anything else resolving these from DI would do. Every
    /// collision is the guard's exception; none is a corrupted collection.
    /// </summary>
    [Fact]
    public void Concurrent_direct_mutators_all_fail_as_violations()
    {
        var unexpected = new List<Exception>();

        var failure = Concurrently.Run(8, worker =>
        {
            for (var n = 0; n < 300; n++)
            {
                try
                {
                    if (worker % 2 == 0)
                    {
                        _registry.Apply(Prompt($"s-{worker}-{n}", At.AddSeconds(n), $"p-{n}"));
                    }
                    else
                    {
                        _sound.SetSessionMuted(new SessionId($"s-{worker}-{n}"), muted: true);
                    }
                }
                catch (SingleWriterViolationException)
                {
                    // The expected outcome of contention: loud, named, and harmless.
                }
                catch (Exception ex)
                {
                    lock (unexpected)
                    {
                        unexpected.Add(ex);
                    }
                }
            }
        });

        Assert.Null(failure);
        Assert.True(
            unexpected.Count == 0,
            "Contention produced something other than a SingleWriterViolationException — which means a " +
            $"structure was mutated concurrently rather than refused: {unexpected.FirstOrDefault()}");

        Assert.True(
            _guard.ViolationCount > 0,
            "Eight threads hammering the mutators produced no contention at all; the test is not exercising " +
            "what it claims to.");
    }
}
