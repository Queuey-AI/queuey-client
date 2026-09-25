using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>Captures outgoing requests (and bodies) and returns configured responses; supports per-call branching.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int, HttpRequestMessage, byte[]?, HttpResponseMessage> _responder;
    private int _count;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = (_, req, _) => responder(req);

    public StubHttpMessageHandler(Func<int, HttpRequestMessage, byte[]?, HttpResponseMessage> responder)
        => _responder = responder;

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<byte[]?> Bodies { get; } = new();

    public HttpRequestMessage? LastRequest => Requests.Count == 0 ? null : Requests[^1];
    public byte[]? LastBody => Bodies.Count == 0 ? null : Bodies[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(request);
        Bodies.Add(body);
        return _responder(_count++, request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body)
    {
        string json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public static HttpResponseMessage Accepted(object body) => Json(HttpStatusCode.Accepted, body);

    /// <summary>
    /// What a deployment apply reads and writes around the queues themselves: the workspace's queue
    /// listing (empty — every queue is new) and the mode change. Null for anything else.
    /// </summary>
    public static HttpResponseMessage? DeployDefaults(HttpRequestMessage req)
    {
        string path = req.RequestUri!.AbsolutePath;
        if (req.Method == HttpMethod.Get && path.StartsWith("/tenants/", StringComparison.Ordinal) && path.EndsWith("/queues", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, Array.Empty<object>());
        if (path.EndsWith("/mode-change", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        return null;
    }
}
