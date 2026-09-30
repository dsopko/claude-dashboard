using System.Net;
using System.Net.Sockets;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// A loopback port that nothing answers on, held for as long as the test needs it (the T1.48 review).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why not bind, release, and connect later.</strong> A port released by one test is free
/// for the operating system to hand to the next thing that binds port 0 — and test classes run in
/// parallel. A test that then connects to "its" unused port can land on another class's listener
/// and be recorded there as a request that listener never expected. The hook script's refusal
/// tests assert that nothing at all arrived, so a stranger's probe there reads as a leak.
/// </para>
/// <para>
/// <strong>So the port stays bound, and is never listened on.</strong> A connection to it is
/// refused — measured on this machine at about two seconds, Windows retrying the SYN, which is the
/// same as a released port — and no other socket can be given it while this is held: a second bind
/// to it is refused. <see cref="Socket.ExclusiveAddressUse"/> keeps a reuse-address bind out too.
/// </para>
/// </remarks>
internal sealed class ReservedPort : IDisposable
{
    private readonly Socket _socket;

    /// <summary>Binds a port on 127.0.0.1 and holds it without listening.</summary>
    public ReservedPort()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true,
        };

        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    /// <summary>The held port.</summary>
    public int Port { get; }

    /// <summary>Releases the port.</summary>
    public void Dispose() => _socket.Dispose();
}
