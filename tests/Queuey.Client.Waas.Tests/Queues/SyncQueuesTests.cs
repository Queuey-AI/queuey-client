using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class SyncQueuesTests
{
    private static object ApplyBody(string name, bool created = true, bool hasTarget = true) => new
    {
        publicId = "que_" + name,
        displayName = name,
        created,
        hasDeliveryTarget = hasTarget,
    };

    private static QueueyService Build(HttpMessageHandler api, params QueueDefinition[] queues)
        => WaasTestHost.Build(apiStub: api, queues: queues);

    [Fact]
    public async Task Applies_each_queue_and_patches_only_declared_policy()
    {
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/policy", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders")));

        QueueyService service = Build(api,
            QueueDefinitionFactory.FromType(typeof(OrderQueue), null),      // declares ordering + retentionDays
            QueueDefinitionFactory.FromType(typeof(ShipmentUpdates), null)); // declares nothing

        QueueSyncResult result = await service.SyncQueuesAsync();

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.Total);

        // Three writes besides the mode, not four: the all-inherit queue gets an apply but no policy
        // patch. Sending an empty patch would risk pinning values the queue meant to keep inheriting.
        Assert.Equal(3, api.Requests.Count(r => !r.RequestUri!.AbsolutePath.EndsWith("/mode-change", StringComparison.Ordinal)));
        Assert.Equal(new[] { true, false }, result.Applied.Select(r => r.PolicyApplied).ToArray());

        HttpRequestMessage patch = api.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/policy", StringComparison.Ordinal));
        Assert.Equal("PATCH", patch.Method.Method);
        Assert.EndsWith("/queues/que_orders/policy", patch.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task The_policy_patch_carries_declared_fields_and_omits_the_rest()
    {
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/policy", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders")));

        QueueyService service = Build(api, QueueDefinitionFactory.FromType(typeof(OrderQueue), null));
        await service.SyncQueuesAsync();

        int patchIndex = api.Requests.ToList().FindIndex(r => r.RequestUri!.AbsolutePath.EndsWith("/policy", StringComparison.Ordinal));
        using JsonDocument doc = JsonDocument.Parse(api.Bodies[patchIndex]!);

        Assert.Equal("bykey", doc.RootElement.GetProperty("ordering").GetString());
        Assert.Equal(30, doc.RootElement.GetProperty("retentionDays").GetInt32());

        // Undeclared fields are ABSENT from the body, not sent as null. Absence is the unambiguous
        // way to say "leave it alone" — an explicit null could just as easily read as "clear it".
        Assert.False(doc.RootElement.TryGetProperty("dlqEnabled", out _));
        Assert.False(doc.RootElement.TryGetProperty("idempotent", out _));
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task The_apply_body_is_just_the_tenant_and_the_name()
    {
        // Existence only. Policy travels in its own PATCH so that an apply never has to round-trip
        // (and risk clearing) the queue's delivery config.
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders")));
        QueueyService service = Build(api, QueueDefinitionFactory.FromName("orders", null));

        await service.SyncQueuesAsync();

        Assert.Equal(HttpMethod.Put, api.Requests[0].Method);
        Assert.EndsWith("/queues", api.Requests[0].RequestUri!.AbsolutePath);

        using JsonDocument doc = JsonDocument.Parse(api.Bodies[0]!);
        Assert.Equal("ten_abc", doc.RootElement.GetProperty("tenantPublicId").GetString());
        Assert.Equal("orders", doc.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(true, true, "deliver")]    // ny, og workspacet har en base-URL: leverer
    [InlineData(true, false, "logOnly")]   // ny, uten mål: logger og sier fra
    [InlineData(false, true, null)]        // fantes fra før: modusen er noen andres, og røres ikke
    public async Task A_queue_this_sync_creates_delivers_when_it_has_somewhere_to_deliver(bool created, bool hasTarget, string? mode)
    {
        // Før 2026-09-23 ble en ny kø stående i LogOnly selv med workspacets base-URL å levere til,
        // og hver event endte som Logged uten et ord, for advarselen gjaldt bare køer helt uten mål.
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/mode-change", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders", created, hasTarget)));
        QueueyService service = Build(api, QueueDefinitionFactory.FromName("orders", null));

        QueueSyncResult result = await service.SyncQueuesAsync();

        Assert.Equal(mode, result.Applied.Single().Mode);
        int modeChange = api.Requests.ToList().FindIndex(r => r.RequestUri!.AbsolutePath.EndsWith("/queues/que_orders/mode-change", StringComparison.Ordinal));
        if (mode == "deliver")
        {
            Assert.Equal("PATCH", api.Requests[modeChange].Method.Method);
            using JsonDocument doc = JsonDocument.Parse(api.Bodies[modeChange]!);
            Assert.Equal(3, doc.RootElement.GetProperty("mode").GetInt32());   // Deliver, som tall på ledningen
        }
        else
        {
            Assert.Equal(-1, modeChange);
        }
    }

    [Fact]
    public async Task A_queue_with_nowhere_to_deliver_warns_but_does_not_fail()
    {
        // The decided semantics: the declared state landed, so this is readiness, not convergence.
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("shipment-updates", hasTarget: false)));

        QueueyService service = Build(api, QueueDefinitionFactory.FromType(typeof(ShipmentUpdates), null));

        QueueSyncResult result = await service.SyncQueuesAsync();

        Assert.True(result.AllSucceeded);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("no delivery target", warning);
        Assert.Contains("logs them without delivering", warning);
    }

    [Fact]
    public async Task A_failing_queue_stops_the_run_and_names_what_was_not_attempted()
    {
        var api = new StubHttpMessageHandler((_, _, body) =>
        {
            string json = System.Text.Encoding.UTF8.GetString(body ?? Array.Empty<byte>());
            return json.Contains("bad-queue", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "forbidden", message = "nope" } })
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("ok-queue"));
        });

        QueueyService service = Build(api,
            QueueDefinitionFactory.FromName("good-queue", null),
            QueueDefinitionFactory.FromName("bad-queue", null),
            QueueDefinitionFactory.FromName("later-queue", null));

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => service.SyncQueuesAsync());

        Assert.Equal(new[] { "later-queue" }, ex.Queues!.NotAttempted.ToArray());
        Assert.Equal(1, ex.Queues!.Succeeded);
        Assert.Contains("not attempted: later-queue", ex.Message);
        Assert.Null(ex.Streams);   // the run kind is distinguishable from the exception alone
    }

    [Fact]
    public async Task Dry_run_needs_no_credentials_and_touches_nothing()
    {
        var api = new StubHttpMessageHandler(_ => throw new InvalidOperationException("a dry run must not call the API"));
        QueueyService service = WaasTestHost.Build(
            apiStub: api,
            queues: new[] { QueueDefinitionFactory.FromType(typeof(OrderQueue), null) },
            configure: o => o.LicensePublicId = null);   // no control-plane credential at all

        QueueSyncResult result = await service.SyncQueuesAsync(new SyncOptions { DryRun = true });

        Assert.True(result.AllSucceeded);
        Assert.Empty(api.Requests);
        Assert.True(result.Applied.Single().DryRun);
        Assert.True(result.Applied.Single().PolicyApplied);   // the plan shows a patch would be sent
    }

    [Fact]
    public async Task An_apply_that_returns_no_queue_id_counts_as_a_failure()
    {
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, new { displayName = "orders", created = true }));

        QueueyService service = Build(api, QueueDefinitionFactory.FromName("orders", null));

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => service.SyncQueuesAsync());
        Assert.Contains("returned no queue id", ex.Queues!.Applied.Single().Error!.Message);
    }

    [Fact]
    public async Task SyncAsync_applies_queues_before_streams()
    {
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.Contains("/waas/streams", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody())
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders")));

        QueueyService service = WaasTestHost.Build(
            apiStub: api,
            streams: new[] { StreamDefinitionFactory.FromType(typeof(OrderCreated), null) },
            queues: new[] { QueueDefinitionFactory.FromName("orders", null) });

        await service.SyncAsync();

        // A stream is published on top of a queue, so the queue must land first — mode included.
        var paths = api.Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();
        Assert.EndsWith("/queues", paths[0]);
        Assert.True(paths.FindIndex(p => p.EndsWith("/waas/streams", StringComparison.Ordinal))
                    > paths.FindIndex(p => p.EndsWith("/mode-change", StringComparison.Ordinal)));
    }
}
