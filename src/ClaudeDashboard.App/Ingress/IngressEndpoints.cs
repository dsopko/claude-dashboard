using System.Text.Json;
using ClaudeDashboard.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serilog;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// The three ingress endpoints (Impl §3.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure observer, without exception (Impl §3.3).</strong> <c>/hook</c> answers
/// <c>200</c> with an empty body and no decision field — for a well-formed event, for an
/// unrecognized one, for malformed JSON, for a missing session id, and for a full pipeline.
/// A <c>2xx</c> empty body is "success, no decision" to Claude Code, so the dashboard cannot
/// block, delay or alter a turn. The tempting wrong answer is a <c>503</c> when the pipeline
/// is full; that is precisely the moment it matters most, because a dashboard under load must
/// never push back on the thing it is watching. A dead dashboard must degrade Claude Code to
/// "no hooks fire", never to "Claude is stuck".
/// </para>
/// <para>
/// The only status other than <c>200</c> is <c>401</c> for a bad token, which is a request that
/// did not come from Claude Code at all.
/// </para>
/// <para>
/// Nothing here touches the Registry (Impl §3.2). Map, publish, return.
/// </para>
/// </remarks>
public static class IngressEndpoints
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>Maps <c>/hook</c>, <c>/show</c> and <c>/health</c>.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="onShow">What to do when a second instance asks this one to surface (T1.15).</param>
    public static void MapIngress(this IEndpointRouteBuilder app, Action? onShow = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/hook", (Delegate)HandleHook);
        app.MapPost("/show", (HttpContext context) => HandleShow(context, onShow));
        app.MapGet("/health", () => Results.Text("ok", "text/plain"));
    }

    /// <summary>The single ingest endpoint (Impl §3.2).</summary>
    private static async Task<IResult> HandleHook(HttpContext context)
    {
        var services = context.RequestServices;
        var logger = services.GetService(typeof(ILogger)) as ILogger ?? Log.Logger;

        if (!Authorized(context, services))
        {
            logger.Warning("Rejected a /hook post with a missing or incorrect token.");
            return Results.Unauthorized();
        }

        var mapper = (HookEventMapper)services.GetService(typeof(HookEventMapper))!;
        var sink = (IEventSink)services.GetService(typeof(IEventSink))!;

        HookPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<HookPayload>(
                context.Request.Body,
                PayloadOptions,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Distinguishable in the log from a well-formed unknown event, on purpose — at 2am
            // "Claude Code changed its payload shape" and "Claude Code sent an event we do not
            // consume" are different diagnoses with different fixes.
            logger.Warning("Discarded a /hook post whose body was not valid JSON: {Reason}", ex.Message);
            return Empty200();
        }
        catch (OperationCanceledException)
        {
            logger.Debug("A /hook post was aborted by the client before its body arrived.");
            return Empty200();
        }

        if (payload is null)
        {
            logger.Warning("Discarded a /hook post with an empty body.");
            return Empty200();
        }

        var mapping = mapper.Map(payload);

        if (!mapping.Mapped)
        {
            switch (mapping.Rejection)
            {
                case HookRejection.UnknownEvent:
                    logger.Information(
                        "Ignored hook event {HookEventName}, which ingress does not consume.",
                        payload.HookEventName ?? "(absent)");
                    break;

                case HookRejection.NoSessionId:
                    logger.Warning(
                        "Discarded hook event {HookEventName} with no session_id; it cannot be filed against a session.",
                        payload.HookEventName);
                    break;

                default:
                    break;
            }

            return Empty200();
        }

        if (!sink.TryPublish(mapping.Event!))
        {
            // A full pipeline is a real state, not an error to report upstream (Impl §4).
            logger.Warning(
                "Dropped hook event {HookEventName} for session {SessionId}: the pipeline would not accept it.",
                payload.HookEventName,
                mapping.Event!.SessionId.Value);
        }

        return Empty200();
    }

    /// <summary>The single-instance signal (Impl §3.2, §5.3). T1.15 supplies the action.</summary>
    private static IResult HandleShow(HttpContext context, Action? onShow)
    {
        var services = context.RequestServices;
        var logger = services.GetService(typeof(ILogger)) as ILogger ?? Log.Logger;

        if (!Authorized(context, services))
        {
            logger.Warning("Rejected a /show post with a missing or incorrect token.");
            return Results.Unauthorized();
        }

        onShow?.Invoke();
        return Empty200();
    }

    private static bool Authorized(HttpContext context, IServiceProvider services)
    {
        var token = (IngressToken)services.GetService(typeof(IngressToken))!;
        var presented = context.Request.Headers[IngressToken.HeaderName].ToString();

        return token.Accepts(string.IsNullOrEmpty(presented) ? null : presented);
    }

    /// <summary><c>200</c> with an empty body and no decision field — see the remarks on this type.</summary>
    private static IResult Empty200() => Results.StatusCode(StatusCodes.Status200OK);
}
