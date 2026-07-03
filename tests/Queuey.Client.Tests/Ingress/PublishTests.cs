using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Tests;

public class PublishTests
{
    private static QueueyOptions ApiKeyOptions() => new()
    {
        ApiKey = "qak_kid.secret",
        TenantPublicId = "ten_abc",
        IngressBaseAddress = new Uri("https://ingress.example"),
    };

    private static StubHttpMessageHandler Ok202() => new(_ =>
        StubHttpMessageHandler.Accepted(new
        {
            queuePublicId = "que_1",
            eventId = "evt_1",
            receivedAtUtc = DateTimeOffset.UnixEpoch,
            mode = "Deliver",
            replayed = false,
        }));

    [Fact]
    public async Task Publish_object_posts_json_to_prod_route()
    {
        var handler = Ok202();
        using var client = new QueueyClient(ApiKeyOptions(), new HttpClient(handler));

        PublishResult result = await client.Ingress.PublishAsync("orders", new { hello = "world" });

        HttpRequestMessage req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://ingress.example/events/ten_abc/orders", req.RequestUri!.ToString());
        Assert.Equal("qak_kid.secret", req.Headers.GetValues(QueueyHeaders.ApiKey).Single());
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"hello\":\"world\"}", Encoding.UTF8.GetString(handler.LastBody!));

        Assert.Equal("que_1", result.QueuePublicId);
        Assert.Equal("evt_1", result.EventId);
        Assert.Equal("Deliver", result.Mode);
        Assert.False(result.Replayed);
    }

    [Fact]
    public async Task Publish_raw_bytes_uses_octet_stream_and_passes_body_through()
    {
        var handler = Ok202();
        using var client = new QueueyClient(ApiKeyOptions(), new HttpClient(handler));

        byte[] payload = new byte[] { 1, 2, 3, 4 };
        await client.Ingress.PublishAsync("orders", payload);

        Assert.Equal("application/octet-stream", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(payload, handler.LastBody);
    }

    [Fact]
    public async Task Publish_options_set_context_and_trace_headers()
    {
        var handler = Ok202();
        using var client = new QueueyClient(ApiKeyOptions(), new HttpClient(handler));

        await client.Ingress.PublishAsync("orders", new { x = 1 }, new PublishOptions
        {
            EventType = "order.created",
            GroupKey = "cust_42",
            IdempotencyKey = "idem-1",
            Source = "checkout-svc",
        });

        HttpRequestMessage req = handler.LastRequest!;
        Assert.Equal("order.created", req.Headers.GetValues(QueueyHeaders.EventType).Single());
        Assert.Equal("cust_42", req.Headers.GetValues(QueueyHeaders.GroupKey).Single());
        Assert.Equal("idem-1", req.Headers.GetValues(QueueyHeaders.IdempotencyKey).Single());
        Assert.Equal("checkout-svc", req.Headers.GetValues(QueueyHeaders.Source).Single());
    }

    [Fact]
    public async Task Default_source_from_options_is_applied()
    {
        var options = ApiKeyOptions();
        options.Source = "default-src";
        var handler = Ok202();
        using var client = new QueueyClient(options, new HttpClient(handler));

        await client.Ingress.PublishAsync("orders", new { x = 1 });

        Assert.Equal("default-src", handler.LastRequest!.Headers.GetValues(QueueyHeaders.Source).Single());
    }

    [Fact]
    public async Task Sandbox_publish_uses_sandbox_route_and_sb_query()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(new
        {
            queuePublicId = "que_1",
            eventId = "evt_1",
            receivedAtUtc = DateTimeOffset.UnixEpoch,
            mode = "sandbox",
            replayed = false,
            sandbox = new { enabled = true, policy = new { failStatusCode = 500, successStatusCode = 200, ttlSec = 900 } },
        }));
        using var client = new QueueyClient(ApiKeyOptions(), new HttpClient(handler));

        PublishResult result = await client.Ingress.PublishSandboxAsync("orders", new { x = 1 }, new SandboxPublishOptions
        {
            FailStatusCode = 500,
            FailCount = 1,
            RunId = "run_9",
        });

        Uri uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/events/sandbox/ten_abc/orders", uri.AbsolutePath);
        Assert.Equal("?sb_fail_code=500&sb_fail_count=1&runId=run_9", uri.Query);
        Assert.Equal("sandbox", result.Mode);
        Assert.NotNull(result.Sandbox);
        Assert.True(result.Sandbox!.Enabled);
        Assert.Equal(500, result.Sandbox.Policy!.FailStatusCode);
    }

    [Fact]
    public async Task Hmac_signing_path_sets_five_headers_and_no_api_key()
    {
        var options = new QueueyOptions
        {
            SigningKeyId = "kid_1",
            SigningSecret = "sekret",
            TenantPublicId = "ten_abc",
            IngressBaseAddress = new Uri("https://ingress.example"),
        };
        var handler = Ok202();
        using var client = new QueueyClient(options, new HttpClient(handler));

        await client.Ingress.PublishAsync("orders", new { x = 1 });

        HttpRequestMessage req = handler.LastRequest!;
        Assert.False(req.Headers.Contains(QueueyHeaders.ApiKey));
        Assert.True(req.Headers.Contains(QueueyHeaders.KeyId));
        Assert.True(req.Headers.Contains(QueueyHeaders.Timestamp));
        Assert.True(req.Headers.Contains(QueueyHeaders.Nonce));
        Assert.True(req.Headers.Contains(QueueyHeaders.ContentSha256));
        Assert.True(req.Headers.Contains(QueueyHeaders.Signature));
    }

    [Fact]
    public async Task Conflict_maps_to_typed_exception_with_code()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.Conflict,
            new { error = new { code = "queue_paused", message = "Queue is paused and cannot receive events." } }));
        using var client = new QueueyClient(ApiKeyOptions(), new HttpClient(handler));

        QueueyConflictException ex = await Assert.ThrowsAsync<QueueyConflictException>(
            () => client.Ingress.PublishAsync("orders", new { x = 1 }));

        Assert.Equal("queue_paused", ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Publish_without_tenant_throws_configuration_exception()
    {
        var options = new QueueyOptions { ApiKey = "qak_k.s", IngressBaseAddress = new Uri("https://ingress.example") };
        using var client = new QueueyClient(options, new HttpClient(Ok202()));

        await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => client.Ingress.PublishAsync("orders", new { x = 1 }));
    }

    [Fact]
    public void Client_without_credential_throws_configuration_exception()
    {
        var options = new QueueyOptions { TenantPublicId = "ten_abc" };
        Assert.Throws<QueueyConfigurationException>(() => new QueueyClient(options, new HttpClient(Ok202())));
    }
}
