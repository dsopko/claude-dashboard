using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// <c>post-status.cmd</c> itself, run as Claude Code runs it (issue #29, acceptance §6.9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>THE SCRIPT MUST PRINT NOTHING, ON EVERY PATH, AND THAT IS WHAT THIS FILE IS FOR.</strong>
/// On <c>UserPromptSubmit</c> and <c>SessionStart</c> — two of the eight events registered —
/// Claude Code adds a hook's stdout to the model's context as if the operator had typed it. A
/// stray line therefore alters every prompt in every session, and <em>nothing in the transcript
/// shows it</em>. It is not a crash, it is not an error, and it cannot be seen from the session.
/// The only place it can be observed is here.
/// </para>
/// <para>
/// <strong>The branches tested are the ones that only run when something has already gone
/// wrong.</strong> That is not thoroughness for its own sake: those are precisely the branches
/// whose output is invisible and harmful, and precisely the ones a per-line redirect gets wrong
/// because they are the ones nobody remembers. Each case below is arranged so that the script
/// takes a path it is never expected to take in normal use.
/// </para>
/// <para>
/// <strong>The real script, written by the real writer.</strong> Nothing here restates the script
/// text; <see cref="HookScript.EnsureWritten"/> puts it on disk, so a test cannot pass against a
/// script the application would not produce.
/// </para>
/// <para>
/// <strong>Two assertions per case, not one.</strong> Empty streams and exit 0 prove the script
/// <em>said</em> nothing. Only the listener proves it <em>did</em> nothing — a script that
/// silently posted a malformed port to whatever answered would satisfy the first pair completely.
/// </para>
/// </remarks>
public sealed class HookScriptBehaviourTests : IDisposable
{
    /// <summary>A payload of the shape Claude Code puts on the script's stdin.</summary>
    private const string Payload = """{"hook_event_name":"Stop","session_id":"a-session","cwd":"C:\\work"}""";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public HookScriptBehaviourTests()
    {
        _paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_root);
        HookScript.EnsureWritten(_paths, Logger.None);
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

    // ---- The five cases of §6.9 --------------------------------------------------------------------

    /// <summary>
    /// <strong>(a) No <c>listening.txt</c>: silence, exit 0, and no connection attempted.</strong>
    /// </summary>
    /// <remarks>
    /// The ordinary case whenever the dashboard is closed, which for this operator is most of the
    /// day. It is the whole reason the hook can now stay installed: before issue #29 this was an
    /// HTTP hook posting to a dead port, and Claude Code printed an error on every turn in every
    /// session.
    /// </remarks>
    [Fact]
    public void With_no_announcement_it_says_nothing_and_connects_to_nothing()
    {
        using var listener = new Recorder(200);

        // The listener is running and its port is not announced, so any connection at all is a
        // connection this script had no business making.
        var run = Run();

        AssertSilent(run);
        Assert.Empty(listener.Requests);
    }

    /// <summary>
    /// <strong>(b) An announcement nothing answers: silence, exit 0.</strong>
    /// </summary>
    /// <remarks>
    /// The state after a hard kill, until the next start overwrites the file. The connection fails
    /// and <c>curl</c> has plenty to say about it — all of which must go nowhere.
    /// </remarks>
    [Fact]
    public void With_nothing_listening_on_the_announced_port_it_says_nothing()
    {
        Announce(FreePort());

        AssertSilent(Run());
    }

    /// <summary>
    /// <strong>(c) An announcement that is not a number: silence, exit 0, nothing sent.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The listener is running throughout and its port is never announced, so
    /// <see cref="Recorder.Requests"/> being empty is the assertion that the script did nothing —
    /// which is a stronger claim than that it said nothing, and the one that matters for a value
    /// the script did not write.
    /// </para>
    /// <para>
    /// <strong>The metacharacter case is not decoration.</strong> The URL is built from
    /// <c>!BOUND!</c>, an integer produced by <c>set /a</c>, precisely so that the file's text
    /// never reaches a command line. CLAUDE.md's "text is data, never executed" has exactly one
    /// place to be broken in this feature, and it is here.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("not-a-port")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("99999999999999999999")]
    [InlineData("52789abc")]
    [InlineData("1+1")]
    [InlineData(" 52789")]
    [InlineData("""52789" & echo INJECTED & rem """)]
    [InlineData("52789 & echo INJECTED")]
    public void An_announcement_that_is_not_a_port_is_ignored_in_silence(string content)
    {
        using var listener = new Recorder(200);

        File.WriteAllText(_paths.ListeningFile, content);

        var run = Run();

        AssertSilent(run);
        Assert.DoesNotContain("INJECTED", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("INJECTED", run.Error, StringComparison.Ordinal);
        Assert.Empty(listener.Requests);
    }

    /// <summary>
    /// <strong>(d) No <c>curl.exe</c>: silence, exit 0.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Arranged by overriding <c>SystemRoot</c> in the child's environment</strong>, which
    /// makes <c>%SystemRoot%\System32\curl.exe</c> resolve to a path that is not there. The script
    /// is not touched, and <c>cmd.exe</c> is unaffected because it is launched by absolute path.
    /// </para>
    /// <para>
    /// <strong>The simpler arrangement was rejected on purpose.</strong> Calling <c>curl.exe</c>
    /// unqualified and handing the child an empty <c>PATH</c> would also work, and would make the
    /// shipped script use whichever <c>curl.exe</c> came first on the operator's <c>PATH</c> — a
    /// program handed their prompts. A production property is not traded for a test convenience.
    /// </para>
    /// <para>
    /// <strong>This case earns its place.</strong> Measured without the redirect, <c>cmd</c> writes
    /// <c>The system cannot find the path specified.</c> to stderr and sets errorlevel 3. Both the
    /// redirect and the unconditional <c>exit /b 0</c> are visibly load-bearing here.
    /// </para>
    /// </remarks>
    [Fact]
    public void With_no_curl_it_says_nothing_and_still_exits_zero()
    {
        Announce(FreePort());

        AssertSilent(Run(environment: psi =>
            psi.Environment["SystemRoot"] = Path.Combine(_root, "no-windows-here")));
    }

    /// <summary>
    /// <strong>(e) Something answering 500: silence, exit 0.</strong>
    /// </summary>
    /// <remarks>
    /// A dashboard mid-fault, or a stranger on the port. The response body is discarded by
    /// <c>-o nul</c> as well as by the redirect, and both matter: a hook's JSON stdout carries
    /// decisions, so an echoed response body would be JSON on stdout on the two events that read
    /// it — the pure-observer rule broken from the far end.
    /// </remarks>
    [Fact]
    public void An_error_from_the_dashboard_is_swallowed_in_silence()
    {
        using var listener = new Recorder(500);
        Announce(listener.Port);

        AssertSilent(Run());

        Assert.Single(listener.Requests);
    }

    // ---- The path that is supposed to run ----------------------------------------------------------

    /// <summary>
    /// The payload reaches <c>POST /hook</c> on the announced port, byte for byte.
    /// </summary>
    /// <remarks>
    /// The control for all five cases above. A script that did nothing at all, ever, would pass
    /// every one of them — and this is also the assertion that the stdin pass-through works, which
    /// is the only thing the script is actually for.
    /// </remarks>
    [Fact]
    public void The_payload_reaches_the_announced_port()
    {
        using var listener = new Recorder(200);
        Announce(listener.Port);

        AssertSilent(Run());

        var request = Assert.Single(listener.Requests);

        Assert.StartsWith("POST /hook HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains($"Host: 127.0.0.1:{listener.Port}", request, StringComparison.Ordinal);
        Assert.EndsWith(Payload, request.TrimEnd('\r', '\n'), StringComparison.Ordinal);
    }

    /// <summary>The token travels as a header when the variable is set, and not otherwise.</summary>
    /// <remarks>
    /// <para>
    /// Both halves, because they fail apart. A header that was always sent would interpolate an
    /// unset variable and arrive empty, which ingress cannot tell from no header at all — so the
    /// hook would claim a protection it did not have.
    /// </para>
    /// <para>
    /// <strong>Measured residual, and it is a constraint on T10.2 rather than a defect here:</strong>
    /// a token containing a double quote does not survive <c>cmd</c>'s argument quoting. The
    /// generated token is to use <c>[A-Za-z0-9_-]</c> only. An <c>&amp;</c> does survive, which is
    /// the character that would matter for injection, and that is asserted.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_token_travels_only_when_the_variable_is_set()
    {
        using var listener = new Recorder(200);
        Announce(listener.Port);

        AssertSilent(Run());
        Assert.DoesNotContain("X-Dashboard-Token", listener.Requests[0], StringComparison.Ordinal);

        AssertSilent(Run(environment: psi =>
            psi.Environment["CLAUDE_DASHBOARD_TOKEN"] = "t0ken-A_b & c"));

        Assert.Contains("X-Dashboard-Token: t0ken-A_b & c", listener.Requests[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing line ending in the announcement is tolerated; leading whitespace is not.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed: <c>set /p</c> strips a trailing LF or CRLF and does not strip
    /// a leading space. We write the file without a newline, so this is about a hand-edit — and
    /// being strict about the one number in it is what keeps a malformed value out of a URL.
    /// </remarks>
    [Theory]
    [InlineData("{0}", true)]
    [InlineData("{0}\n", true)]
    [InlineData("{0}\r\n", true)]
    [InlineData(" {0}", false)]
    public void A_line_ending_is_tolerated_and_leading_space_is_not(string format, bool arrives)
    {
        using var listener = new Recorder(200);

        File.WriteAllText(
            _paths.ListeningFile,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, format, listener.Port));

        AssertSilent(Run());

        Assert.Equal(arrives ? 1 : 0, listener.Requests.Count);
    }

    // ---- Running it --------------------------------------------------------------------------------

    private void Announce(int port) => ListeningFile.Write(_paths, port);

    /// <summary>A loopback port nothing is bound to.</summary>
    /// <remarks>
    /// Taken by binding and releasing rather than by picking a number, so the test cannot collide
    /// with whatever else is running on the machine — including the operator's own dashboard.
    /// </remarks>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>Runs the script the way Claude Code's exec form runs it.</summary>
    /// <remarks>
    /// <c>cmd.exe</c> by the same absolute path the registration writes, the script as the last
    /// argument, and the payload on stdin — so what is exercised here is the arrangement that will
    /// actually be in the operator's settings file.
    /// </remarks>
    private RunResult Run(Action<ProcessStartInfo>? environment = null)
    {
        var start = new ProcessStartInfo(HookInstaller.Interpreter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(_paths.HookScriptFile);

        environment?.Invoke(start);

        using var process = Process.Start(start)!;

        process.StandardInput.Write(Payload);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30_000), "post-status.cmd did not exit within 30 seconds.");

        return new RunResult(process.ExitCode, output, error);
    }

    /// <summary>Nothing on either stream, and an exit code that neither reports nor blocks.</summary>
    /// <remarks>
    /// Exit 1 is shown to the operator as a hook error. <strong>Exit 2 blocks the turn</strong>,
    /// and the dashboard blocking a Claude turn breaks the pure-observer rule outright — so the
    /// assertion is on zero exactly, never on "not 2".
    /// </remarks>
    private static void AssertSilent(RunResult run)
    {
        Assert.Equal(string.Empty, run.Out);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    private readonly record struct RunResult(int ExitCode, string Out, string Error);

    /// <summary>A loopback listener that records what it is sent and answers a fixed status.</summary>
    /// <remarks>
    /// <para>
    /// Raw TCP rather than <c>HttpListener</c>, which needs a URL reservation and would refuse to
    /// answer <c>500</c> without one being arranged first. What is being asserted is the bytes the
    /// script sends, so a socket that records them is closer to the claim than a web server is.
    /// </para>
    /// <para>
    /// Bound on port 0, so the port is assigned by the operating system and the test cannot
    /// collide with anything on the machine — the operator's own dashboard included.
    /// </para>
    /// </remarks>
    private sealed class Recorder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<string> _requests = [];
        private readonly Lock _guard = new();
        private readonly Task _loop;

        public Recorder(int status)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptAsync(status, _stopping.Token));
        }

        /// <summary>The port it is listening on.</summary>
        public int Port { get; }

        /// <summary>What arrived, in order.</summary>
        public IReadOnlyList<string> Requests
        {
            get
            {
                // Give a request that is in flight a moment to land. Every assertion on this is
                // made after the script has exited, so the wait is bounded by the socket rather
                // than by the script.
                Thread.Sleep(150);

                lock (_guard)
                {
                    return [.. _requests];
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Cancellation on the way out. There is nothing left to report it to.
            }

            _stopping.Dispose();
        }

        private async Task AcceptAsync(int status, CancellationToken token)
        {
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[64 * 1024];
                    var text = new StringBuilder();

                    stream.ReadTimeout = 2000;

                    try
                    {
                        // Read until the peer stops sending. curl sends headers and body together
                        // and then waits, so one short read is enough in practice; the loop is
                        // here so a split write does not truncate what is recorded.
                        for (var pass = 0; pass < 4; pass++)
                        {
                            if (!stream.DataAvailable)
                            {
                                await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);
                                continue;
                            }

                            var read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);

                            if (read == 0)
                            {
                                break;
                            }

                            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        }

                        await stream.WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
                        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        // The client gave up. What was read still counts as having arrived.
                    }

                    lock (_guard)
                    {
                        _requests.Add(text.ToString());
                    }
                }
            }
        }
    }
}
