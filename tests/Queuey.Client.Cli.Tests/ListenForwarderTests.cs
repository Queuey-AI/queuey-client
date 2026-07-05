using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Queuey.Client.Cli;
using Xunit;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// The local replay must reproduce the delivery byte-for-byte — same method, path+query, headers, and body —
/// swapping only the host for <c>--forward-to</c>. This mirrors the backend envelope-capture fidelity test.
/// </summary>
public sealed class ListenForwarderTests
{
    private static ListenEnvelope SampleEnvelope(byte[] body) => new(
        EventId: "evt_1",
        QueuePublicId: "que_1",
        QueueName: "orders",
        EventType: "order.created",
        GroupKey: "cust_42",
        Method: "PUT",
        PathAndQuery: "/hook?x=1&y=2",
        OriginalUrl: "https://partner.example.com/hook?x=1&y=2",
        Headers: new List<ListenHeader>
        {
            new("Authorization", "Bearer secret-token"),
            new("X-Queuey-Signature", "abc123"),
            new("X-Env", "prod"),
            new("Content-Type", "application/json"),
        },
        ContentType: "application/json",
        BodyBase64: Convert.ToBase64String(body));

    [Fact]
    public async Task BuildLocalRequest_swaps_host_but_preserves_method_path_query_headers_and_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"orderId\":\"ord_1\"}");
        using HttpRequestMessage req = ListenForwarder.BuildLocalRequest(SampleEnvelope(body), "http://localhost:5094");

        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("http://localhost:5094/hook?x=1&y=2", req.RequestUri!.AbsoluteUri);   // host swapped, path+query kept

        Assert.Equal("Bearer secret-token", Assert.Single(req.Headers.GetValues("Authorization")));   // auth preserved
        Assert.Equal("abc123", Assert.Single(req.Headers.GetValues("X-Queuey-Signature")));            // signature preserved
        Assert.Equal("prod", Assert.Single(req.Headers.GetValues("X-Env")));                           // custom header preserved
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);                 // content-type on the content

        Assert.Equal(body, await req.Content.ReadAsByteArrayAsync());   // body byte-for-byte
    }

    [Fact]
    public void BuildLocalRequest_appends_path_onto_a_base_that_has_its_own_path()
    {
        var env = SampleEnvelope(Encoding.UTF8.GetBytes("{}"));
        using HttpRequestMessage req = ListenForwarder.BuildLocalRequest(env, "http://localhost:5094/ingest/");

        Assert.Equal("http://localhost:5094/ingest/hook?x=1&y=2", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void BuildLocalRequest_drops_stale_content_length()
    {
        var body = Encoding.UTF8.GetBytes("{}");   // 2 bytes
        var env = SampleEnvelope(body) with
        {
            Headers = new List<ListenHeader> { new("Content-Length", "999") },
        };
        using HttpRequestMessage req = ListenForwarder.BuildLocalRequest(env, "http://localhost:5094");

        // The forwarded Content-Length must reflect the actual body (2), not the captured "999".
        Assert.Equal(body.Length, req.Content!.Headers.ContentLength);
    }
}
