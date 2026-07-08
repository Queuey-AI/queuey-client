using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Cli;

/// <summary>Replays a received envelope as a local HTTP request — faithful except the host, which becomes <c>--forward-to</c>.</summary>
internal static class ListenForwarder
{
    /// <summary>Rebuilds the outbound request against the local base: same method, path+query, headers, and body.
    /// When <paramref name="preservePath"/> is false, forwards to <paramref name="forwardTo"/> VERBATIM (the
    /// original request path is ignored) — for bridging to a fixed local endpoint (e.g. a local ingress route)
    /// rather than replaying the webhook at its original path.</summary>
    public static HttpRequestMessage BuildLocalRequest(ListenEnvelope env, string forwardTo, bool preservePath = true)
    {
        var url = preservePath
            ? new Uri(forwardTo.TrimEnd('/') + env.PathAndQuery, UriKind.Absolute)
            : new Uri(forwardTo, UriKind.Absolute);
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

    public static async Task<(int Status, long Ms)> ForwardAsync(HttpClient http, ListenEnvelope env, string forwardTo, CancellationToken ct, bool preservePath = true)
    {
        using HttpRequestMessage req = BuildLocalRequest(env, forwardTo, preservePath);
        var sw = Stopwatch.StartNew();
        using HttpResponseMessage res = await http.SendAsync(req, ct).ConfigureAwait(false);
        sw.Stop();
        return ((int)res.StatusCode, sw.ElapsedMilliseconds);
    }
}
