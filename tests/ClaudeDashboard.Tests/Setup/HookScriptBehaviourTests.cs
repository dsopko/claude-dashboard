using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
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
        AssertNothingArrived(listener);
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
        using var nobody = new ReservedPort();
        Announce(nobody.Port);

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
        AssertNothingArrived(listener);
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
        using var nobody = new ReservedPort();
        Announce(nobody.Port);

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

    // ---- The token, from listening.txt (T1.48, issue #57) --------------------------------------------

    /// <summary>The token on line 2 travels as the header, on every post.</summary>
    [Fact]
    public void The_token_in_the_file_travels_on_every_post()
    {
        using var listener = new Recorder(200);
        Announce(listener.Port);

        AssertSilent(Run());
        AssertSilent(Run());

        Assert.Equal(2, listener.Requests.Count);
        Assert.All(listener.Requests, request =>
            Assert.Contains($"X-Dashboard-Token: {_token.Reveal()}\r\n", request, StringComparison.Ordinal));
    }

    /// <summary>
    /// The environment is not read. A session launched with the retired variable sends the file's
    /// token, not its own copy — which is the whole of issue #57: a session's environment is fixed at
    /// launch, and the file is read fresh.
    /// </summary>
    [Fact]
    public void The_retired_variable_is_not_read()
    {
        using var listener = new Recorder(200);
        Announce(listener.Port);

        AssertSilent(Run(environment: psi => psi.Environment["CLAUDE_DASHBOARD_TOKEN"] = "stale-token-from-the-environment"));

        var request = Assert.Single(listener.Requests);

        Assert.Contains($"X-Dashboard-Token: {_token.Reveal()}\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-token-from-the-environment", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token made of every one of the 64 characters, in two halves, and tokens that start with the
    /// two punctuation characters, all travel. The control for the refusals below: a check that
    /// refused everything would pass every one of them.
    /// </summary>
    [Theory]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq")]
    [InlineData("rstuvwxyz0123456789-_ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("_bCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("-bCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    public void Every_base64url_character_is_accepted(string token)
    {
        using var listener = new Recorder(200);
        WriteAnnouncement($"{listener.Port}\r\n{token}");

        AssertSilent(Run());

        var request = Assert.Single(listener.Requests);
        Assert.Contains($"X-Dashboard-Token: {token}\r\n", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line 2 that is not exactly 43 characters of <c>A–Z a–z 0–9 - _</c> sends nothing, prints
    /// nothing, and exits 0 (brief §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every case the brief names — line 2 missing, empty, 42 and 44 characters, and 43 characters
    /// containing each of <c>" &amp; % ! ^ &lt; &gt; |</c> and a space — and the cases that broke the
    /// first prototype or could: a <c>;</c> first or mid-token, which the default <c>for /f</c> eol
    /// would have let through; <c>!PATH!</c> and <c>%PATH%</c>, which must not expand; a tab; a
    /// lone <c>!</c> pair that delayed expansion would strip back to 43; a non-ASCII letter; and an
    /// LF-only file, which reads as one line and fails the port check.
    /// </para>
    /// <para>
    /// "Sends nothing" is asserted as no connection at all: the recorder saw no request.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("{0}")]
    [InlineData("{0}\r\n")]
    [InlineData("{0}\r\n\r\nAbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCd")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdEF")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt\"vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt&vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt%vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt!vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt^vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt<vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt>vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt|vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\n\"bCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCd\"")]
    [InlineData("{0}\r\n;bCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt;vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt\tvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt=vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt.vWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\n!PATH!GhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\n%PATH%GhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIj!!KlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrStéWxYz0123456789-_AbCdE")]
    [InlineData("{0}\nAbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    // A bare LF inside line 2 (the T1.48 review): set /p keeps it, the length test counts it, and
    // for /f splits at it, so both halves passed the character check. Middle, first, last, doubled.
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt\nvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nA\nCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrStUvWxYz0123\n56789-_AbCdE")]
    [InlineData("{0}\r\n\nbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCd\n")]
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt\n\nWxYz0123456789-_AbCdE")]
    // A lone CR is not a line break to for /f; it is an ordinary character and is refused.
    [InlineData("{0}\r\nAbCdEfGhIjKlMnOpQrSt\rvWxYz0123456789-_AbCdE")]
    public void A_line_2_that_is_not_a_token_sends_nothing_and_says_nothing(string format)
    {
        using var listener = new Recorder(200);
        WriteAnnouncement(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, listener.Port));

        AssertSilent(Run());

        AssertNothingArrived(listener);
    }

    /// <summary>
    /// Every byte from 0x80 to 0xFF, and every control byte, LF and CR included, inside an
    /// otherwise valid token: none of them travels. Written as raw bytes, because what cmd reads is
    /// bytes in the console code page, and a UTF-8 string would test only the multi-byte case.
    /// </summary>
    [Fact]
    public void No_high_or_control_byte_passes_the_check()
    {
        using var listener = new Recorder(200);
        var head = Encoding.ASCII.GetBytes($"{listener.Port}\r\n");
        var token = Encoding.ASCII.GetBytes("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE");
        var bytes = Enumerable.Range(1, 31).Append(127).Concat(Enumerable.Range(128, 128));

        foreach (var value in bytes)
        {
            var hostile = (byte[])token.Clone();
            hostile[20] = (byte)value;
            File.WriteAllBytes(_paths.ListeningFile, [.. head, .. hostile]);

            AssertSilent(Run());
        }

        AssertNothingArrived(listener);
    }

    /// <summary>
    /// A trailing line ending in the announcement is tolerated; leading whitespace is not.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed: <c>set /p</c> strips a trailing CRLF and does not strip a
    /// leading space. We write the file without a trailing newline, so this is about a hand-edit —
    /// and being strict about the number in it is what keeps a malformed value out of a URL. Since
    /// T1.48 the file has two lines; an LF-only file reads as one line and is refused, which the
    /// not-a-token cases assert.
    /// </remarks>
    [Theory]
    [InlineData("{0}\r\n{1}", true)]
    [InlineData("{0}\r\n{1}\r\n", true)]
    [InlineData(" {0}\r\n{1}", false)]
    public void A_line_ending_is_tolerated_and_leading_space_is_not(string format, bool arrives)
    {
        using var listener = new Recorder(200);

        WriteAnnouncement(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, listener.Port, _token.Reveal()));

        AssertSilent(Run());

        Assert.Equal(arrives ? 1 : 0, listener.Requests.Count);
    }

    // ---- Running it --------------------------------------------------------------------------------

    private readonly IngressToken _token = new();

    private void Announce(int port) => ListeningFile.Write(_paths, port, _token);

    /// <summary>
    /// Nothing arrived at <paramref name="listener"/> — no connection at all, whatever it sent.
    /// </summary>
    /// <remarks>
    /// Strict on purpose (the T1.48 review): any connection counts as a send, including one that
    /// carried an empty or partial body. A narrower check that looked for the payload let a real
    /// send with an empty body pass. On failure the message quotes every recorded request, escaped,
    /// so the next failure says what arrived instead of only that something did.
    /// </remarks>
    private static void AssertNothingArrived(Recorder listener)
    {
        var requests = listener.Requests;

        Assert.True(
            requests.Count == 0,
            $"{requests.Count} connection(s) arrived where none should: " +
            string.Join(" || ", requests.Select(request =>
                $"[{request.Replace("\r", "\r", StringComparison.Ordinal).Replace("\n", "\n", StringComparison.Ordinal)}]")));
    }

    /// <summary>Writes <paramref name="content"/> as the announcement, byte for byte, with no BOM.</summary>
    private void WriteAnnouncement(string content) =>
        File.WriteAllText(_paths.ListeningFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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
    /// collide with anything on the machine — the operator's own dashboard included. Other test
    /// classes hold the ports they connect to (see <c>ReservedPort</c>), so nothing of theirs can
    /// be handed this one.
    /// </para>
    /// <para>
    /// <strong>Every accepted connection is recorded, even one that sends nothing</strong> (the
    /// T1.48 review). A request is read until its headers and its <c>Content-Length</c> body have
    /// arrived, or the peer closes, with a ten-second bound — not for a fixed few passes, which
    /// could record a real send empty or half-read under load. <see cref="Requests"/> waits for
    /// connections still pending or being read before it answers.
    /// </para>
    /// </remarks>
    private sealed class Recorder : IDisposable
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<string> _requests = [];
        private readonly Lock _guard = new();
        private readonly Task _loop;
        private int _inFlight;

        public Recorder(int status)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptAsync(status, _stopping.Token));
        }

        /// <summary>The port it is listening on.</summary>
        public int Port { get; }

        /// <summary>
        /// What arrived, in order — one entry per connection, whatever it sent, once nothing is
        /// still pending or being read.
        /// </summary>
        public IReadOnlyList<string> Requests
        {
            get
            {
                // Every assertion on this is made after the script has exited. A connection it made
                // is by then in the backlog or being read; this waits for both to drain.
                Thread.Sleep(150);

                var deadline = DateTime.UtcNow + Bound;

                while (DateTime.UtcNow < deadline && (Volatile.Read(ref _inFlight) > 0 || _listener.Pending()))
                {
                    Thread.Sleep(20);
                }

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

                Interlocked.Increment(ref _inFlight);

                var text = new StringBuilder();

                try
                {
                    using (client)
                    {
                        var stream = client.GetStream();

                        try
                        {
                            await ReadRequestAsync(stream, text).ConfigureAwait(false);
                            await stream.WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
                            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                        {
                            // The client gave up, or the bound ran out. What was read still counts
                            // as having arrived, and so does the connection itself.
                        }
                    }
                }
                finally
                {
                    lock (_guard)
                    {
                        _requests.Add(text.ToString());
                    }

                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }

        /// <summary>
        /// Reads one request: to the end of the headers, then <c>Content-Length</c> bytes of body,
        /// or until the peer closes — whichever comes first, within <see cref="Bound"/>.
        /// </summary>
        private static async Task ReadRequestAsync(NetworkStream stream, StringBuilder text)
        {
            using var bound = new CancellationTokenSource(Bound);
            var buffer = new byte[64 * 1024];
            var received = new List<byte>();

            while (true)
            {
                var read = await stream.ReadAsync(buffer, bound.Token).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                received.AddRange(buffer.AsSpan(0, read).ToArray());
                text.Clear().Append(Encoding.UTF8.GetString([.. received]));

                var all = text.ToString();
                var headersEnd = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                if (headersEnd < 0)
                {
                    continue;
                }

                var length = ContentLength(all[..headersEnd]);
                var bodyBytes = received.Count - Encoding.UTF8.GetByteCount(all[..(headersEnd + 4)]);

                if (bodyBytes >= length)
                {
                    break;
                }
            }
        }

        private static int ContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var length))
                {
                    return length;
                }
            }

            return 0;
        }
    }
}
