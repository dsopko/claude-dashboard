using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ClaudeDashboard.App.Hosting;

/// <summary>Who, if anyone, is on the ingress port (Impl §3.2, §5.3).</summary>
public enum PortOccupant
{
    /// <summary>Nothing is listening. The connection was refused.</summary>
    Free = 1,

    /// <summary>A dashboard whose gate name matches ours: genuinely another copy of us.</summary>
    OurInstance = 2,

    /// <summary>
    /// A dashboard, but a different one — another logon session's, or another data folder's.
    /// It holds the port and it must <strong>not</strong> be signalled.
    /// </summary>
    OtherInstance = 3,

    /// <summary>Something answered, but not with our health contract. A stranger, or an old build.</summary>
    Unrecognised = 4,

    /// <summary>The connection was accepted and nothing came back before the timeout.</summary>
    Silent = 5,
}

/// <summary>The health answer, when there was one.</summary>
/// <param name="Occupant">Who is there.</param>
/// <param name="Instance">The gate name it reported, when it reported one.</param>
/// <param name="Problem">The transport failure or the shape complaint, when there was one.</param>
public readonly record struct HealthProbeResult(
    PortOccupant Occupant,
    string? Instance = null,
    string? Problem = null);

/// <summary>
/// Asks whoever holds the ingress port who they are (Impl §3.2, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The port cannot decide this on its own, and neither can a bare "ok".</strong> A port
/// this dashboard releases is a port anything may take — a dev server, a tunnel, anything that
/// liked the same number — so after a hard kill the occupant is simply unknown. Deriving the port
/// per user (§3.1, T1.21) made that less likely and not less possible, which is the same thing
/// here. And a healthy dashboard answering on it is not
/// necessarily <em>ours</em>: a loopback bind is machine-wide while the gate is per logon
/// session and per data folder, so under fast user switching the dashboard on that port can
/// belong to another signed-in user. Signalling it would raise <em>their</em> window on
/// <em>their</em> desktop and leave this user with nothing and no explanation. So the answer
/// carries the gate name, and this compares it.
/// </para>
/// <para>
/// <strong>Everything that is not a clean match falls to the conservative side.</strong> Free is
/// concluded only from a refused connection. A reply that is late, malformed, unauthorised or
/// merely unexpected is somebody else's port, not ours to take and not ours to signal.
/// </para>
/// </remarks>
public static class HealthProbe
{
    /// <summary>How long to wait for an answer from a port that is known to be occupied.</summary>
    /// <remarks>
    /// A stranger that accepts the connection and never writes must not hold up startup, and
    /// nothing on 127.0.0.1 legitimately takes a second to answer a constant. This bounds only
    /// the conversation with an occupant that already exists — see <see cref="Probe"/> for why
    /// "is anyone there at all" is not asked over HTTP.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The <c>status</c> value a healthy dashboard reports.</summary>
    public const string HealthyStatus = "ok";

    /// <summary>Builds the <c>/health</c> body for <paramref name="instanceName"/>.</summary>
    /// <remarks>
    /// Written as text rather than serialised from an object so the wire shape is visible here
    /// and cannot change under a serializer setting; the name itself still goes through the JSON
    /// encoder, because a Windows path hash lives behind a namespace separator and a raw
    /// backslash in a JSON string is malformed.
    /// </remarks>
    public static string BodyFor(string instanceName) =>
        $$"""{"status":"{{HealthyStatus}}","instance":{{JsonSerializer.Serialize(instanceName)}}}""";

    /// <summary>Asks who holds <paramref name="port"/>, comparing against <paramref name="ourInstance"/>.</summary>
    /// <param name="port">The loopback port to probe.</param>
    /// <param name="ourInstance">This process's gate name, from <see cref="SingleInstanceGate.NameFor"/>.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when null.</param>
    /// <remarks>
    /// <para>
    /// Never throws. Every failure is an occupant, because the caller has to decide either way.
    /// </para>
    /// <para>
    /// <strong>"Is anyone there" is asked by trying to bind, not by connecting.</strong> A bind
    /// answers the question actually being asked — may this process have this port — and answers
    /// it definitively, with no number to tune. A connect answers a different question, and makes
    /// one timeout do two jobs that pull against each other: it must be short enough to bound a
    /// stranger that accepts and never replies, and long enough to outlast a refusal. Wherever
    /// refusal is slower than the bound chosen for silence, a <em>free</em> port is classified as
    /// a stranger — and the ordinary first start comes up unable to hear anything, on a port
    /// nobody had taken. There is no timeout that satisfies both, which is why the answer is not
    /// a better timeout. It is also the interlock Impl §5.3 names anyway.
    /// </para>
    /// <para>
    /// Such machines exist, and this is one. Measured here, with raw TCP connects so nothing
    /// above the socket is in the way: a refused loopback connect takes about 2045 ms, on
    /// <em>both</em> <c>127.0.0.1</c> and <c>[::1]</c>, while connecting to an open loopback port
    /// takes well under a millisecond and the bind attempt below about 0.4 ms. Whatever delays
    /// the reset is not specific to one address family. Those figures describe one machine and
    /// are not the reason for the design; the argument above needs no measurement and holds where
    /// refusal is fast.
    /// </para>
    /// <para>
    /// The listener is closed immediately, so Kestrel binds the port a moment later. Losing that
    /// race means Kestrel throws, which <c>Program</c> already reports with the port named — a
    /// loud failure, not a silent one.
    /// </para>
    /// </remarks>
    public static HealthProbeResult Probe(int port, string ourInstance, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(ourInstance);

        if (NothingIsListening(port))
        {
            return new HealthProbeResult(PortOccupant.Free);
        }

        using var client = new HttpClient { Timeout = timeout ?? DefaultTimeout };

        try
        {
            using var response = client.Send(
                new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{port}/health")),
                HttpCompletionOption.ResponseContentRead);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new HealthProbeResult(
                    PortOccupant.Unrecognised,
                    Problem: $"answered {(int)response.StatusCode}");
            }

            return Classify(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), ourInstance);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // Accepted and never answered. The timeout is the only thing that distinguishes this
            // from a dashboard that is merely busy, and it is why the timeout is short.
            return new HealthProbeResult(PortOccupant.Silent, Problem: ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Refused(ex)
                ? new HealthProbeResult(PortOccupant.Free)
                : new HealthProbeResult(PortOccupant.Unrecognised, Problem: ex.Message);
        }
    }

    /// <summary>Reads a health body and decides whose it is.</summary>
    private static HealthProbeResult Classify(string body, string ourInstance)
    {
        string? status;
        string? instance;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new HealthProbeResult(PortOccupant.Unrecognised, Problem: "the health body was not an object");
            }

            status = Read(document.RootElement, "status");
            instance = Read(document.RootElement, "instance");
        }
        catch (JsonException ex)
        {
            // An old build answering the bare text "ok" lands here, and it is right that it does:
            // it cannot tell us whose it is, so it is not ours to signal.
            return new HealthProbeResult(PortOccupant.Unrecognised, Problem: ex.Message);
        }

        if (!string.Equals(status, HealthyStatus, StringComparison.Ordinal))
        {
            return new HealthProbeResult(
                PortOccupant.Unrecognised,
                instance,
                $"the health body reported status '{status ?? "(absent)"}'");
        }

        if (string.IsNullOrEmpty(instance))
        {
            return new HealthProbeResult(
                PortOccupant.Unrecognised,
                Problem: "the health body carried no instance name");
        }

        // Ordinal: the name is a lowercase hex hash behind a fixed prefix, so any difference at
        // all is a different instance. Nothing here should be case-folded — that would make two
        // genuinely different gates look like one.
        return string.Equals(instance, ourInstance, StringComparison.Ordinal)
            ? new HealthProbeResult(PortOccupant.OurInstance, instance)
            : new HealthProbeResult(PortOccupant.OtherInstance, instance);
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Whether <paramref name="port"/> can be bound, which is to say nobody holds it.</summary>
    /// <remarks>
    /// Only the IPv4 loopback address, matching what a probing process cares about: what actually
    /// connects is <c>post-status.cmd</c>, and it posts to <c>http://127.0.0.1:port/hook</c>.
    /// Kestrel's <c>ListenLocalhost</c> binds both families and
    /// tolerates one of them failing, so an occupant that holds only <c>[::1]</c> is not a reason
    /// to declare the port taken here.
    /// </remarks>
    private static bool NothingIsListening(int port)
    {
        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// <summary>
    /// Whether the transport failure means nothing is listening, as opposed to something being
    /// there and behaving badly.
    /// </summary>
    /// <remarks>
    /// Reached only when the bind attempt above already found the port occupied, so this is the
    /// narrow race where the occupant left in between. Only a refusal counts: a reset, a protocol
    /// error or a garbage reply all mean a socket was accepted by <em>somebody</em>.
    /// </remarks>
    private static bool Refused(HttpRequestException ex) =>
        ex.InnerException is SocketException
        {
            SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable,
        };
}
