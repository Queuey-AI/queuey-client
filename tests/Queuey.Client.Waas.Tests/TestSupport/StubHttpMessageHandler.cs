using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Whether the test answers the calls for managed resources itself (Queuey F2.4): starting an apply and reading the
    /// workspace. Without it, the stub answers as a Queuey from before managed resources, 404 to both, and records them in
    /// <see cref="ManagementRequests"/> instead of <see cref="Requests"/>, so a test of something else sees what it did before.
    /// </summary>
    public bool AnswersManagement { get; init; }

    /// <summary>The managed-resource calls the stub answered itself.</summary>
    public List<HttpRequestMessage> ManagementRequests { get; } = new();

    /// <summary>The token <see cref="ApplyStarted"/> answers with.</summary>
    public const string ApplyToken = "apply-token-1";

    /// <summary>True for <c>POST /tenants/{t}/deployment/applies</c> and <c>GET /tenants/{t}</c>.</summary>
    public static bool IsManagementCall(HttpRequestMessage req)
    {
        string path = req.RequestUri!.AbsolutePath;
        return (req.Method == HttpMethod.Post && path.EndsWith("/deployment/applies", StringComparison.Ordinal))
               || (req.Method == HttpMethod.Get && path.StartsWith("/tenants/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2);
    }

    /// <summary>A started apply, with the workspace's management when <paramref name="workspaceState"/> is given.</summary>
    public static HttpResponseMessage ApplyStarted(string? workspaceState = null, string enforcement = "Enforce")
        => Json(HttpStatusCode.OK, new
        {
            token = ApplyToken,
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            enforcement,
            workspace = workspaceState is null ? null : new
            {
                state = workspaceState, detachReason = "moved off the file",
                detachedAtUtc = DateTimeOffset.Parse("2026-10-06T10:00:00Z"), detachedBy = new { kind = "user", name = "Kenneth" },
            },
        });

    public HttpRequestMessage? LastRequest => Requests.Count == 0 ? null : Requests[^1];
    public byte[]? LastBody => Bodies.Count == 0 ? null : Bodies[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!AnswersManagement && IsManagementCall(request))
        {
            ManagementRequests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

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
