using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The per-user port choice: derivation, continuity, the walk, and giving up (Impl §3.1; T1.21).
/// </summary>
/// <remarks>
/// The probe is a table rather than a socket, so the walk can be tested without occupying real
/// ports and without a test's outcome depending on what else is running on the machine. What a
/// real bind does is <c>HealthProbeTests</c>' subject; what is decided from the answers is this
/// one's.
/// </remarks>
public sealed class PortSelectionTests
{
    private const int Base = DashboardSettings.DefaultPort;

    private const string Sid = "S-1-5-21-3953501118-1735086671-3633542688-1001";
    private const string OtherSid = "S-1-5-21-3953501118-1735086671-3633542688-1002";

    /// <summary>A probe that says everything is free.</summary>
    private static Func<int, PortOccupant> AllFree => _ => PortOccupant.Free;

    /// <summary>A probe where the named ports are held and everything else is free.</summary>
    private static Func<int, PortOccupant> Held(PortOccupant by, params int[] ports) =>
        port => ports.Contains(port) ? by : PortOccupant.Free;

    // ---- The derivation ---------------------------------------------------------------------

    /// <summary>
    /// <strong>The same user derives the same port every time, and that is the whole feature.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value below is hard-coded on purpose. Computing the expectation with the same call the
    /// product uses would assert that <c>Derive</c> equals <c>Derive</c>, which is true of a
    /// function that returns a random number. A literal is the only form of this assertion that
    /// can fail.
    /// </para>
    /// <para>
    /// <strong>And the literal was computed outside this codebase, which is the half that
    /// matters.</strong> SHA-256 of the SID in PowerShell: first four bytes <c>DE 3B 30 EB</c>,
    /// big-endian <c>3728421099</c>, modulo 1000 gives <c>99</c>, plus the base gives
    /// <c>52888</c>. The first draft of this test guessed a number instead, and the guess was
    /// wrong — which was useful, because it meant the assertion had to be settled by a second
    /// implementation rather than by reading the first one.
    /// </para>
    /// <para>
    /// <strong>If this ever fails after a framework upgrade, the feature has broken and the fix is
    /// not to update the number.</strong> Every user's port would move, every recorded
    /// <c>port.txt</c> would be stale, and every allowlist entry would be orphaned. It would have
    /// to be a deliberate migration.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_user_derives_one_specific_port_and_it_does_not_move()
    {
        Assert.Equal(52888, PortSelection.Derive(Sid, Base));
    }

    /// <summary>
    /// <strong>The randomised-hash trap, caught by the only thing that can catch it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>string.GetHashCode()</c> is randomised per process, so a derivation built on it returns
    /// the same value all through one test run and a different one tomorrow. <strong>Every
    /// in-process test passes, and the feature silently does not work</strong> — the user's port
    /// moves on every launch, <c>port.txt</c> papers over it by being tried first, and the only
    /// visible symptom is allowlist entries quietly accumulating.
    /// </para>
    /// <para>
    /// This is the T1.15 trap, and the only in-process defence against it is an expectation
    /// computed <em>outside</em> this process. The literal above is that. This test states the
    /// property the literal is protecting so nobody deletes one without understanding the other.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_derivation_does_not_use_a_per_process_hash()
    {
        // Same input, same answer, computed independently of the product's own arithmetic.
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Sid));
        var offset = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];

        Assert.Equal(Base + (int)(offset % 1000u), PortSelection.Derive(Sid, Base));

        // And it is not the framework's hash, which is what a careless implementation reaches for.
        Assert.NotEqual(Base + Math.Abs(Sid.GetHashCode(StringComparison.Ordinal) % 1000), PortSelection.Derive(Sid, Base));
    }

    /// <summary>Two users get two ports, which is the point of deriving at all.</summary>
    [Fact]
    public void Two_users_derive_different_ports()
    {
        Assert.NotEqual(PortSelection.Derive(Sid, Base), PortSelection.Derive(OtherSid, Base));
    }

    /// <summary>Every derivation lands inside the range it was given.</summary>
    /// <remarks>
    /// Checked across many identities rather than one, because an off-by-one at the top of the
    /// range is exactly the defect a single example misses.
    /// </remarks>
    [Fact]
    public void Every_derivation_lands_inside_the_range()
    {
        for (var i = 0; i < 2000; i++)
        {
            var port = PortSelection.Derive($"S-1-5-21-0-0-0-{i}", Base, range: 100);

            Assert.InRange(port, Base, Base + 99);
        }
    }

    [Fact]
    public void A_derivation_needs_an_identity()
    {
        Assert.Throws<ArgumentException>(() => PortSelection.Derive("", Base));
        Assert.Throws<ArgumentException>(() => PortSelection.Derive("   ", Base));
        Assert.Throws<ArgumentException>(() => PortSelection.Derive(null!, Base));
    }

    /// <summary>A range that would run off the end of the port space is refused, not truncated.</summary>
    [Fact]
    public void A_range_past_the_top_of_the_port_space_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortSelection.Derive(Sid, 65000, range: 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortSelection.Derive(Sid, Base, range: 0));
    }

    // ---- The three attempts ------------------------------------------------------------------

    /// <summary>A fresh profile has no recorded port, so it derives.</summary>
    [Fact]
    public void A_fresh_profile_derives_and_binds()
    {
        var choice = PortSelection.Choose(Base, Sid, recorded: null, AllFree);

        Assert.Equal(PortSource.Derived, choice.Source);
        Assert.Equal(PortSelection.Derive(Sid, Base), choice.Port);
        Assert.True(choice.Found);
    }

    /// <summary>A recorded port that is still free is kept — that is the continuity clause.</summary>
    [Fact]
    public void A_recorded_port_is_preferred_to_the_derivation()
    {
        var choice = PortSelection.Choose(Base, Sid, recorded: 54321, AllFree);

        Assert.Equal(PortSource.Recorded, choice.Source);
        Assert.Equal(54321, choice.Port);
    }

    /// <summary>A recorded port somebody else has taken falls through to the derivation.</summary>
    [Fact]
    public void A_recorded_port_that_is_taken_falls_through_to_the_derivation()
    {
        var choice = PortSelection.Choose(Base, Sid, recorded: 54321, Held(PortOccupant.Unrecognised, 54321));

        Assert.Equal(PortSource.Derived, choice.Source);
        Assert.Equal(PortSelection.Derive(Sid, Base), choice.Port);

        // The stranger is on the record, not merely stepped over.
        Assert.Contains(choice.Attempts, a => a.Port == 54321 && a.Occupant == PortOccupant.Unrecognised);
    }

    /// <summary>A junk recorded port is ignored rather than tried.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void A_recorded_port_outside_the_port_space_is_ignored(int recorded)
    {
        var choice = PortSelection.Choose(Base, Sid, recorded, AllFree);

        Assert.Equal(PortSource.Derived, choice.Source);
        Assert.DoesNotContain(choice.Attempts, a => a.Port == recorded);
    }

    /// <summary>A stranger on the derived port causes a walk, not an exit.</summary>
    [Fact]
    public void A_stranger_on_the_derived_port_causes_a_walk()
    {
        var derived = PortSelection.Derive(Sid, Base);

        var choice = PortSelection.Choose(Base, Sid, null, Held(PortOccupant.Unrecognised, derived));

        Assert.Equal(PortSource.Walked, choice.Source);
        Assert.Equal(derived + 1, choice.Port);
        Assert.True(choice.Found);
    }

    /// <summary>Another user's dashboard is walked past too, and stays distinguishable.</summary>
    /// <remarks>
    /// The classification is the reason the walk probes rather than merely binding: "another
    /// user's dashboard", "another copy of ours" and "a stranger" are three different diagnoses,
    /// and a walk that only counted would put all three in the log as "taken".
    /// </remarks>
    [Fact]
    public void Another_users_dashboard_is_walked_past_and_named()
    {
        var derived = PortSelection.Derive(Sid, Base);

        var choice = PortSelection.Choose(Base, Sid, null, Held(PortOccupant.OtherInstance, derived, derived + 1));

        Assert.Equal(PortSource.Walked, choice.Source);
        Assert.Equal(derived + 2, choice.Port);
        Assert.Contains(choice.Attempts, a => a.Occupant == PortOccupant.OtherInstance);
        Assert.Contains("OtherInstance", choice.Trail, StringComparison.Ordinal);
    }

    /// <summary>The walk is bounded, and running out is not a crash.</summary>
    [Fact]
    public void A_walk_that_finds_nothing_gives_up_and_says_so()
    {
        var choice = PortSelection.Choose(Base, Sid, null, _ => PortOccupant.Unrecognised, walk: 5);

        Assert.False(choice.Found);
        Assert.Equal(PortSource.None, choice.Source);

        // One derivation plus five walk steps: the bound is honoured rather than approximated.
        Assert.Equal(6, choice.Attempts.Count);
    }

    /// <summary>The walk stays inside the range instead of running off the top of it.</summary>
    /// <remarks>
    /// A user whose derivation lands on the last port of the range must get as many chances as one
    /// who lands on the first. Without the wrap they would get none.
    /// </remarks>
    [Fact]
    public void The_walk_wraps_within_the_range_rather_than_running_past_it()
    {
        // A range of 4 and an identity whose derived port is the last of it.
        const int Range = 4;

        var identity = Enumerable.Range(0, 500)
            .Select(i => $"S-1-5-21-0-0-0-{i}")
            .First(id => PortSelection.Derive(id, Base, Range) == Base + Range - 1);

        var choice = PortSelection.Choose(
            Base,
            identity,
            null,
            Held(PortOccupant.Unrecognised, Base + Range - 1),
            range: Range,
            walk: 3);

        Assert.Equal(PortSource.Walked, choice.Source);
        Assert.Equal(Base, choice.Port);
        Assert.InRange(choice.Port, Base, Base + Range - 1);
    }

    /// <summary>Two users on one machine both get a port, neither contending with the other.</summary>
    /// <remarks>
    /// The acceptance criterion, and the thing issue #5 is actually about. Each holds what the
    /// other derived; both still bind, and <strong>neither reaches the walk</strong>, because the
    /// derivation separated them before contention could arise.
    /// </remarks>
    [Fact]
    public void Two_users_on_one_machine_both_bind_without_contending()
    {
        var first = PortSelection.Derive(Sid, Base);
        var second = PortSelection.Derive(OtherSid, Base);

        Assert.NotEqual(first, second);

        var forFirst = PortSelection.Choose(Base, Sid, null, Held(PortOccupant.OtherInstance, second));
        var forSecond = PortSelection.Choose(Base, OtherSid, null, Held(PortOccupant.OtherInstance, first));

        Assert.Equal(PortSource.Derived, forFirst.Source);
        Assert.Equal(PortSource.Derived, forSecond.Source);
        Assert.NotEqual(forFirst.Port, forSecond.Port);

        // One probe each: the other user's port was never a candidate, so nothing queued.
        Assert.Single(forFirst.Attempts);
        Assert.Single(forSecond.Attempts);
    }

    [Fact]
    public void It_needs_a_probe() =>
        Assert.Throws<ArgumentNullException>(() => PortSelection.Choose(Base, Sid, null, null!));

    // ---- Attempt 0: an explicit pin -----------------------------------------------------------

    /// <summary>A pinned port wins over the recorded one and over the derivation.</summary>
    [Fact]
    public void A_pinned_port_is_honoured_before_everything_else()
    {
        var choice = PortSelection.Choose(Base, Sid, recorded: 54321, AllFree, pinned: 55555);

        Assert.Equal(PortSource.Pinned, choice.Source);
        Assert.Equal(55555, choice.Port);
        Assert.Single(choice.Attempts);
    }

    /// <summary>
    /// A pinned port that is taken fails visibly instead of moving the operator somewhere else.
    /// </summary>
    /// <remarks>
    /// The one attempt that does not fall through. Somebody who names a port has said where they
    /// want the dashboard; walking them off it would make the setting a suggestion and leave them
    /// with a dashboard answering at an address they never chose. A deaf start puts the fault in
    /// the tray tooltip, where they can see it and act.
    /// </remarks>
    [Fact]
    public void A_pinned_port_that_is_taken_does_not_fall_through()
    {
        var choice = PortSelection.Choose(Base, Sid, null, Held(PortOccupant.Unrecognised, 55555), pinned: 55555);

        Assert.False(choice.Found);
        Assert.Equal(PortSource.None, choice.Source);
        Assert.Single(choice.Attempts);
    }

    /// <summary>
    /// <strong>No pin means derive — the distinction the whole feature rests on.</strong>
    /// </summary>
    /// <remarks>
    /// Were "unset" not representable, every operator who has never opened <c>settings.json</c>
    /// would carry the base port and be indistinguishable from one who typed it. Honouring that as
    /// a pin would put every user back on one machine-wide port, which is the collision T1.21
    /// exists to remove.
    /// </remarks>
    [Fact]
    public void An_unset_port_derives_rather_than_pinning_the_base()
    {
        var choice = PortSelection.Choose(Base, Sid, null, AllFree, pinned: null);

        Assert.Equal(PortSource.Derived, choice.Source);
        Assert.NotEqual(Base, choice.Port);
    }
}
