using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Cli;

/// <summary>What happened when a forward was sent on to this machine.</summary>
/// <param name="Status">The local response's status, or 502 when nothing answered.</param>
/// <param name="Error">Why nothing answered, when nothing did.</param>
internal sealed record LocalForwardResult(int Status, long DurationMs, Uri LocalUrl, string? Error);

/// <summary>Replays a received envelope as a local HTTP request — faithful except the host, which becomes <c>--forward-to</c>.</summary>
internal static class ListenForwarder
{
    /// <summary>
    /// How long the local app gets to answer: less than the 20 s Queuey waits for the answer, so a slow app is a 504 the
    /// delivery records, not a forward nobody answered.
    /// </summary>
    // Var HttpClient-standarden på 100 s (review av #50, K4): da hadde Queuey gitt opp lenge før appen svarte.
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(18);

    /// <summary>
    /// Where a forward goes on this machine: <paramref name="forwardTo"/> with the path and query of the queue's
    /// endpoint after it, or <paramref name="forwardTo"/> as given when <paramref name="preservePath"/> is false.
    /// </summary>
    public static Uri LocalUrl(ListenEnvelope env, string forwardTo, bool preservePath = true)
        => preservePath
            ? new Uri(forwardTo.TrimEnd('/') + env.PathAndQuery, UriKind.Absolute)
            : new Uri(forwardTo, UriKind.Absolute);

    /// <summary>Rebuilds the outbound request against the local base: same method, path+query, headers, and body.
    /// When <paramref name="preservePath"/> is false, forwards to <paramref name="forwardTo"/> VERBATIM (the
    /// original request path is ignored) — for bridging to a fixed local endpoint (e.g. a local ingress route)
    /// rather than replaying the webhook at its original path.</summary>
    public static HttpRequestMessage BuildLocalRequest(ListenEnvelope env, string forwardTo, bool preservePath = true)
    {
        var url = LocalUrl(env, forwardTo, preservePath);
        var body = Convert.FromBase64String(env.BodyBase64);

        var req = new HttpRequestMessage(new HttpMethod(env.Method), url)
        {
            Content = new ByteArrayContent(body),
        };

        foreach (ListenHeader h in env.Headers ?? new List<ListenHeader>())
        {
            // Content-Length is recomputed by HttpClient; forwarding a stale one corrupts the request.
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            // Request headers first; content headers (Content-Type, …) fall through to the content.
            if (!req.Headers.TryAddWithoutValidation(h.Name, h.Value))
                req.Content.Headers.TryAddWithoutValidation(h.Name, h.Value);
        }

        // Dispatch aid: which queue this event came from, so one local server can route by queue as well as
        // by path. Queuey-namespaced, added on top of the faithfully-replayed original headers.
        if (!string.IsNullOrWhiteSpace(env.QueuePublicId))
            req.Headers.TryAddWithoutValidation("X-Queuey-Queue", env.QueuePublicId);

        return req;
    }

    /// <summary>
    /// Sends the forward to this machine. A receiver that cannot be reached answers 502, and one that does not answer
    /// within the client's timeout 504, so the delivery records the failure honestly instead of Queuey waiting for an
    /// answer that never comes.
    /// </summary>
    public static async Task<LocalForwardResult> ForwardAsync(HttpClient http, ListenEnvelope env, string forwardTo, CancellationToken ct, bool preservePath = true)
    {
        Uri localUrl = new(forwardTo, UriKind.Absolute);
        var sw = Stopwatch.StartNew();
        try
        {
            localUrl = LocalUrl(env, forwardTo, preservePath);
            using HttpRequestMessage req = BuildLocalRequest(env, forwardTo, preservePath);
            using HttpResponseMessage res = await http.SendAsync(req, ct).ConfigureAwait(false);
            return new LocalForwardResult((int)res.StatusCode, sw.ElapsedMilliseconds, localUrl, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LocalForwardResult(504, sw.ElapsedMilliseconds, localUrl,
                $"The local app did not answer within {http.Timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex)
        {
            return new LocalForwardResult(502, sw.ElapsedMilliseconds, localUrl, ex.Message);
        }
    }
}
