using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Tests;

/// <summary>Captures the outgoing request (and its body) and returns a configured response.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    public HttpRequestMessage? LastRequest { get; private set; }
    public byte[]? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return _responder(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body)
    {
        string json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    public static HttpResponseMessage Accepted(object body) => Json(HttpStatusCode.Accepted, body);
}
