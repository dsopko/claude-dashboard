using System.Reflection;
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
        _sound = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions());
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
            WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\w"),
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
    /// <strong>The T1.5 race, converted.</strong> <c>Evaluate</c> <em>enumerates</em> the tracked
    /// sessions while <c>OnSessionChanged</c> inserts into the same dictionary — an enumeration
    /// invalidated by a structural modification, which is why the review could make it throw
    /// <see cref="InvalidOperationException"/> from the collection within a few hundred
    /// iterations. It now fails as the guard's own exception instead: the same collision, named,
    /// at the moment it happens, rather than a mangled walk.
    /// </summary>
    /// <remarks>
    /// Note what the hazard is <em>not</em>: two writes colliding. <c>Evaluate</c> also writes,
    /// but a version of it that only read would race identically. See the comment at its guard
    /// entry.
    /// </remarks>
    [Fact]
    public void Evaluate_racing_a_session_change_is_caught_as_a_violation_not_a_mangled_collection()
    {
        _sound.ChangedUngrouped(Blocked("s-1", At));

        using (_guard.Enter("evaluating the nudge schedule"))
        {
            var thrown = OnAnotherThread(() => _sound.ChangedUngrouped(Blocked("s-2", At)));

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

    /// <summary>
    /// Every method classified as a mutator, proven to enter the region.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="No_public_method_escapes_classification"/>, which is what stops
    /// this list from being the same kind of enumeration the guard replaced. On its own it
    /// proves only that the five methods someone thought of are guarded; together they prove
    /// that every public method is either on this list — and therefore guarded — or explicitly
    /// declared a query.
    /// </remarks>
    [Fact]
    public void Every_mutator_enters_the_region()
    {
        using var held = _guard.Enter("the consumer, mid-event");

        foreach (var (name, call) in Mutators)
        {
            Assert.True(
                OnAnotherThread(call) is SingleWriterViolationException,
                $"{name} does not enter the single-writer region, so a second thread can reach it unnoticed.");
        }
    }

    /// <summary>
    /// <strong>A new public method is guilty until classified.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard is structural, but the proof that it covers everything was not: a hand-written
    /// list of five mutators is "the ones someone thought of", moved out of a comment and into a
    /// test. A sixth public mutator added tomorrow would be unguarded and invisible — the exact
    /// failure this task existed to remove, displaced one level.
    /// </para>
    /// <para>
    /// So the default is inverted. Anything public must be declared a mutator — which the test
    /// above proves guarded — or a query. Adding a method and neither classifying it nor
    /// guarding it fails here. Classification is asserted rather than invocation, deliberately:
    /// synthesising arguments reflectively is fragile, and it is not needed, because the mutator
    /// list is independently proven.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_public_method_escapes_classification()
    {
        // Queries: they read and do not mutate, so they do not enter the region. See
        // SingleWriterGuard for why reads are documented rather than guarded.
        string[] queries = ["IsMuted", "NextNudgeAt", "IsSilenced"];

        var classified = Mutators
            .Select(mutator => mutator.Name.Split('.')[^1])
            .Concat(queries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var type in new[] { typeof(SessionRegistry), typeof(SoundPolicyEngine) })
        {
            var unclassified = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name)
                .Where(name => !classified.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(
                unclassified.Count == 0,
                $"{type.Name} has unclassified public methods: {string.Join(", ", unclassified)}. " +
                "Add each to the mutator list — which proves it enters the guard — or to the query list.");
        }
    }

    /// <summary>
    /// …and a public <em>property setter</em> does not escape it either.
    /// </summary>
    /// <remarks>
    /// <c>IsSpecialName</c> filters out <c>get_</c> and <c>set_</c>, so the classification above
    /// sees methods and nothing else. A public settable property would therefore be an
    /// unguarded mutator that the guard reports as fully classified. Neither type has one today
    /// and one would be a smell, which is exactly why this is cheap: it stays green until
    /// somebody adds the thing nobody should add.
    /// </remarks>
    [Fact]
    public void No_public_property_setter_escapes_classification()
    {
        foreach (var type in new[] { typeof(SessionRegistry), typeof(SoundPolicyEngine) })
        {
            var settable = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.SetMethod?.IsPublic == true)
                .Select(property => property.Name)
                .ToList();

            Assert.True(
                settable.Count == 0,
                $"{type.Name} exposes a public setter: {string.Join(", ", settable)}. A setter mutates "
                + "without entering the region and is invisible to the method classification, which "
                + "filters accessors out. Make it a method that enters the guard, or make it read-only.");
        }
    }

    /// <summary>
    /// …and a new <em>overload</em> of an already-classified name does not inherit its
    /// classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification is by name, so <c>Evaluate()</c> and <c>Evaluate(now)</c> are one entry —
    /// which is what lets the list stay readable, and also means an overload added tomorrow is
    /// covered by a proof that was only ever run against its sibling. Pinning the count per name
    /// turns that into a failure that says so.
    /// </para>
    /// <para>
    /// Counts rather than signatures, deliberately. Matching signatures would force this guard
    /// to synthesise an argument for every parameter list in order to prove entry, which is the
    /// fragility the name-based design was chosen to avoid. A count is enough: it cannot say
    /// which overload is new, but it cannot fail to notice that one is.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_new_overload_inherits_a_classification()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Apply"] = 1,
            ["OnSessionChanged"] = 1,
            ["Evaluate"] = 2,
            ["SetSessionMuted"] = 1,
            ["SetGroupMuted"] = 1,
            ["OnRosterGroupSettled"] = 1,
            ["OnRosterGroupUnsettled"] = 1,
            ["SetAllMuted"] = 1,
            ["SetMonitoringPaused"] = 1,
            ["IsMuted"] = 1,
            ["IsSilenced"] = 1,
            ["NextNudgeAt"] = 1,
        };

        var actual = new[] { typeof(SessionRegistry), typeof(SoundPolicyEngine) }
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName)
            .GroupBy(method => method.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// The methods that mutate shared state and must therefore enter the single-writer region.
    /// </summary>
    /// <remarks>
    /// An instance member because each entry must actually be callable; the classification test
    /// takes the names from here so the two cannot drift apart.
    /// </remarks>
    private (string Name, Action Call)[] Mutators =>
    [
        ("SessionRegistry.Apply", () => _registry.Apply(Prompt("s-9", At))),
        ("SoundPolicyEngine.OnSessionChanged", () => _sound.ChangedUngrouped(Blocked("s-9", At))),
        ("SoundPolicyEngine.Evaluate", () => _sound.Evaluate(_clock.Now)),
        ("SoundPolicyEngine.SetSessionMuted", () => _sound.SetSessionMuted(new SessionId("s-9"), true)),
        ("SoundPolicyEngine.SetGroupMuted", () => _sound.SetGroupMuted(GroupKeys.ForWorkspace(@"C:w"), true)),
        ("SoundPolicyEngine.OnRosterGroupSettled", () => _sound.OnRosterGroupSettled(GroupKeys.ForRoster("orchestration"), At)),
        ("SoundPolicyEngine.OnRosterGroupUnsettled", () => _sound.OnRosterGroupUnsettled(GroupKeys.ForRoster("orchestration"))),
        ("SoundPolicyEngine.SetAllMuted", () => _sound.SetAllMuted(muted: true)),
        ("SoundPolicyEngine.SetMonitoringPaused", () => _sound.SetMonitoringPaused(paused: true)),
    ];

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
        _registry.SessionChanged += (_, e) => _sound.ChangedUngrouped(e.Session);

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
        var first = new SessionRegistry(new SingleWriterGuard());
        var second = new SessionRegistry(new SingleWriterGuard());

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
