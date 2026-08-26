using System.Net;
using System.Net.Http;

namespace ClaudeDashboard.App.Hosting;

/// <summary>What a <c>POST /show</c> to the resident instance did.</summary>
public enum ShowSignalOutcome
{
    /// <summary>The resident instance accepted the signal and is surfacing its window.</summary>
    Shown = 1,

    /// <summary>It answered <c>401</c>: this process could not authenticate to it.</summary>
    Rejected = 2,

    /// <summary>Nothing answered on that port — refused, or no reply before the timeout.</summary>
    Unreachable = 3,

    /// <summary>It answered, with something other than <c>200</c> or <c>401</c>.</summary>
    Failed = 4,
}

/// <summary>The outcome of one signal, and enough to log why.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="StatusCode">The status, when there was a reply.</param>
/// <param name="Problem">The transport failure, when there was one.</param>
public readonly record struct ShowSignalResult(
    ShowSignalOutcome Outcome,
    HttpStatusCode? StatusCode = null,
    string? Problem = null);

/// <summary>
/// How a second instance asks the resident one to surface (Impl §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ingress, not a second channel.</strong> Impl §5.3 requires the signal to reuse
/// <c>/show</c> — no pipe, no memory-mapped file, no second socket. So this presents the token
/// exactly the way Claude Code does, in the <c>X-Dashboard-Token</c> header from
/// <c>CLAUDE_DASHBOARD_TOKEN</c>: to the resident instance a second instance is just another
/// client, and there is no privileged path for one to take.
/// </para>
/// <para>
/// <strong>Bounded, and synchronous.</strong> This runs on the entry thread of a process whose
/// only remaining job is to exit, so a hang here is a process the operator has to go and kill.
/// <see cref="HttpClient.Send(HttpRequestMessage)"/> rather than an awaited call, because
/// <c>Main</c> is <c>[STAThread]</c> and synchronous for the reasons <c>Program</c> gives.
/// </para>
/// </remarks>
public static class ShowSignal
{
    /// <summary>How long to wait for the resident instance to answer.</summary>
    /// <remarks>
    /// Long enough for a busy dashboard mid-render, short enough that a wrong port does not look
    /// like a hang. Nothing recovers by waiting longer: the resident instance either has the
    /// socket open or it does not.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Posts <c>/show</c> to the resident instance on <paramref name="port"/>.</summary>
    /// <param name="port">The loopback port from the same settings file the first instance read.</param>
    /// <param name="token">The ingress token, or null when none is configured.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when null.</param>
    /// <remarks>Never throws. Every failure is an outcome, because the caller's next act is to exit.</remarks>
    public static ShowSignalResult Send(int port, string? token, TimeSpan? timeout = null)
    {
        using var client = new HttpClient { Timeout = timeout ?? DefaultTimeout };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"http://127.0.0.1:{port}/show"));

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add(Ingress.IngressToken.HeaderName, token);
        }

        try
        {
            using var response = client.Send(request);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ShowSignalResult(ShowSignalOutcome.Shown, response.StatusCode),
                HttpStatusCode.Unauthorized => new ShowSignalResult(ShowSignalOutcome.Rejected, response.StatusCode),
                _ => new ShowSignalResult(ShowSignalOutcome.Failed, response.StatusCode),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // A refused connection and a timeout are the same diagnosis to the operator —
            // nothing is listening there — so they share an outcome and differ only in the text.
            return new ShowSignalResult(ShowSignalOutcome.Unreachable, Problem: ex.Message);
        }
    }
}
