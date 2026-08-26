using System.Diagnostics;
using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// Probes a silent socket many times in one test, so a rare race is caught in one run (T1.15b).
/// </summary>
/// <remarks>
/// <para>
/// <c>A_socket_that_never_answers_is_silent</c> probes once. That is the right shape for a
/// behaviour test and the wrong shape for catching a race: at roughly one failure in ten Release
/// suites it took a deliberate hunt to see the message at all, and an earlier sighting was lost
/// entirely. Forty probes in one test turns "it might show up this week" into "it shows up in this
/// run, or the race is gone".
/// </para>
/// <para>
/// <strong>Why the timeout here is shorter than the behaviour test's.</strong> A shorter wait
/// makes the window tighter, so this exercises the race harder rather than more gently — and it
/// keeps forty probes to about two seconds. The classification cannot be made <em>more</em> wrong
/// by giving up sooner: a probe that gives up is <c>Silent</c> by construction, and the failure
/// this exists to catch is the opposite — a probe that never waited at all.
/// </para>
/// <para>
/// <strong>What a failure here means.</strong> The message carries the occupant, the elapsed
/// milliseconds, the probe's own problem string and the server's state. The original flake was
/// five milliseconds and <c>Unrecognised</c>, and the five milliseconds is the whole diagnosis:
/// the probe never waited, so it never timed out, so something ended the connection. A planted
/// early exit in the accept loop reproduces that signature exactly — sub-thirty milliseconds,
/// <c>Unrecognised</c>, "an error occurred while sending the request" — which is how that class of
/// cause was confirmed rather than assumed. What ended the loop in the wild was never identified,
/// and <c>CannedServer.Fault</c> exists so that a recurrence says.
/// </para>
/// </remarks>
public sealed class SilentProbeSoak(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Enough probes that a one-in-ten race is near-certain to appear in one run.</summary>
    private const int Probes = 40;

    private const string OurGate = @"Local\ClaudeDashboard-silent-soak";

    private static readonly TimeSpan Tight = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Forty_silent_sockets_are_forty_silences()
    {
        var wrong = new List<string>();
        var slowest = TimeSpan.Zero;

        for (var probe = 0; probe < Probes; probe++)
        {
            using var server = CannedServer.Silent();

            var clock = Stopwatch.StartNew();
            var result = HealthProbe.Probe(server.Port, OurGate, Tight);
            clock.Stop();

            if (clock.Elapsed > slowest)
            {
                slowest = clock.Elapsed;
            }

            if (result.Occupant != PortOccupant.Silent)
            {
                wrong.Add(
                    $"#{probe} took={clock.Elapsed.TotalMilliseconds:F1}ms occupant={result.Occupant} " +
                    $"problem={result.Problem} server=[{server.State}]");
            }
        }

        output.WriteLine($"{Probes} probes, slowest {slowest.TotalMilliseconds:F1}ms, {wrong.Count} not Silent");

        foreach (var line in wrong)
        {
            output.WriteLine(line);
        }

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} of {Probes} silent sockets were not classified Silent. " +
            $"An elapsed time far below the {Tight.TotalMilliseconds}ms timeout means the probe never waited, " +
            "so read the server state: a loop that ended closed the connection under it. " +
            string.Join(" || ", wrong.Take(5)));
    }
}
