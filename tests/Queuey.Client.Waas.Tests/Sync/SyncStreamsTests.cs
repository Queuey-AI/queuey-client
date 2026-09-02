using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class SyncStreamsTests
{
    [Fact]
    public async Task Applies_each_stream_via_put_waas_streams_with_headers_and_body()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody()));
        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(OrderCreated), null),
        });

        SyncResult result = await service.SyncStreamsAsync();

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.Total);
        StreamApplyResult applied = result.Applied.Single();
        Assert.Equal("order-events", applied.Name);
        Assert.Equal("cat_1", applied.PublicId);
        Assert.Equal("Published", applied.Status);

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("https://api.example/waas/streams", req.RequestUri!.ToString());
        Assert.Equal("lic_1", req.Headers.GetValues(QueueyHeaders.LicensePublicId).Single());
        Assert.Equal("qak_kid.secret", req.Headers.GetValues(QueueyHeaders.ApiKey).Single());

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        JsonElement root = doc.RootElement;
        Assert.Equal("ten_abc", root.GetProperty("producerTenantPublicId").GetString());
        Assert.Equal("order-events", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("isPublic").GetBoolean());
        Assert.Equal(new[] { "order.created", "order.paid" }, root.GetProperty("eventTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Empty_event_types_are_omitted_from_the_body()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody("invoice-events")));
        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromName("invoice-events", null),
        });

        await service.SyncStreamsAsync();

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.False(doc.RootElement.TryGetProperty("eventTypes", out _)); // null → omitted (WhenWritingNull)
    }

    [Fact]
    public async Task A_failing_stream_stops_the_run_and_names_what_was_not_attempted()
    {
        // Fail whichever request carries name "bad-stream"; succeed otherwise.
        var api = new StubHttpMessageHandler((_, _, body) =>
        {
            string json = Encoding.UTF8.GetString(body!);
            bool bad = json.Contains("bad-stream", StringComparison.Ordinal);
            return bad
                ? StubHttpMessageHandler.Json(HttpStatusCode.Conflict, new { error = new { code = "queue_paused", message = "paused" } })
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody());
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromName("good-stream", null),
            StreamDefinitionFactory.FromName("bad-stream", null),
            StreamDefinitionFactory.FromName("later-stream", null),
        });

        // Default is all-or-nothing: the run stops at "bad-stream" and throws rather than returning a
        // half-converged result the caller might read as success.
        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => service.SyncStreamsAsync());
        SyncResult result = ex.Result;

        Assert.Equal(3, result.Total);                       // all three were selected
        Assert.Equal(1, result.Succeeded);                   // good-stream applied
        Assert.Equal(1, result.Failed);                      // bad-stream failed
        Assert.Equal(new[] { "later-stream" }, result.NotAttempted.ToArray());
        Assert.False(result.AllSucceeded);

        StreamApplyResult bad = result.Applied.Single(r => r.Name == "bad-stream");
        Assert.IsType<QueueyConflictException>(bad.Error);

        Assert.Contains("not attempted: later-stream", ex.Message);
        Assert.Contains("idempotent", ex.Message);
    }

    [Fact]
    public async Task ContinueOnError_applies_the_rest_and_still_throws()
    {
        var api = new StubHttpMessageHandler((_, _, body) =>
        {
            string json = Encoding.UTF8.GetString(body!);
            return json.Contains("bad-stream", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(HttpStatusCode.Conflict, new { error = new { code = "queue_paused", message = "paused" } })
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody());
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromName("good-stream", null),
            StreamDefinitionFactory.FromName("bad-stream", null),
            StreamDefinitionFactory.FromName("later-stream", null),
        });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(
            () => service.SyncStreamsAsync(new SyncOptions { ContinueOnError = true }));

        Assert.Equal(2, ex.Result.Succeeded);        // good-stream and later-stream both applied
        Assert.Equal(1, ex.Result.Failed);
        Assert.Empty(ex.Result.NotAttempted);        // nothing was skipped — the whole picture was gathered
    }

    [Fact]
    public async Task An_apply_that_returns_no_catalog_id_counts_as_a_failure()
    {
        // A 2xx with no publicId used to be recorded as success while the stream silently dropped out
        // of the package phase — "applied" but reaching no partner.
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, new { key = "order-events", status = "Published" }));

        QueueyService service = WaasTestHost.Build(apiStub: api,
            streams: new[] { StreamDefinitionFactory.FromType(typeof(OrderCreated), null) });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => service.SyncStreamsAsync());

        Assert.Equal(0, ex.Result.Succeeded);
        Assert.Contains("returned no catalog id", ex.Result.Applied.Single().Error!.Message);
    }

    [Fact]
    public async Task Missing_license_fails_fast_before_any_http()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody()));
        QueueyService service = WaasTestHost.Build(apiStub: api,
            streams: new[] { StreamDefinitionFactory.FromType(typeof(OrderCreated), null) },
            configure: o => o.LicensePublicId = null);

        await Assert.ThrowsAsync<QueueyConfigurationException>(() => service.SyncStreamsAsync());
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task DryRun_needs_no_license_and_makes_no_api_call()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody()));
        QueueyService service = WaasTestHost.Build(apiStub: api,
            streams: new[] { StreamDefinitionFactory.FromType(typeof(OrderCreated), null) },
            configure: o => o.LicensePublicId = null); // license is the sync credential; dry-run must not require it

        SyncResult result = await service.SyncStreamsAsync(new SyncOptions { DryRun = true });

        Assert.True(result.AllSucceeded);
        Assert.True(result.Applied.Single().DryRun);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task SyncStreams_by_type_rejects_duplicate_stream_names()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody()));
        QueueyService service = WaasTestHost.Build(apiStub: api);

        await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => service.SyncStreamsAsync(new[] { typeof(OrderCreated), typeof(OrderCreated) })); // same name twice
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void Plan_previews_registered_streams_without_network()
    {
        QueueyService service = WaasTestHost.Build(streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(OrderCreated), null),
        });

        StreamPlan plan = service.Plan().Single();
        Assert.Equal("order-events", plan.Name);
        Assert.Equal(typeof(OrderCreated).FullName, plan.ModelType);
        Assert.True(plan.IsPublic);
        Assert.False(plan.HasPayloadSchema);
    }
}
