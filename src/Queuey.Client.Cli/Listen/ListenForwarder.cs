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
    /// <summary>Rebuilds the outbound request against the local base: same method, path+query, headers, and body.</summary>
    public static HttpRequestMessage BuildLocalRequest(ListenEnvelope env, string forwardTo)
    {
        var url = new Uri(forwardTo.TrimEnd('/') + env.PathAndQuery, UriKind.Absolute);
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

        return req;
    }

    public static async Task<(int Status, long Ms)> ForwardAsync(HttpClient http, ListenEnvelope env, string forwardTo, CancellationToken ct)
    {
        using HttpRequestMessage req = BuildLocalRequest(env, forwardTo);
        var sw = Stopwatch.StartNew();
        using HttpResponseMessage res = await http.SendAsync(req, ct).ConfigureAwait(false);
        sw.Stop();
        return ((int)res.StatusCode, sw.ElapsedMilliseconds);
    }
}
