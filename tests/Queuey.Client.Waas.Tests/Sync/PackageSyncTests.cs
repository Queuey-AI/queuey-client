using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

public class PackageSyncTests
{
    [Fact]
    public void Attribute_packages_are_mapped_to_the_definition()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(TieredOrder), null);
        Assert.Equal(new[] { "standard", "premium" }, def.Packages.ToArray());
    }

    [Fact]
    public async Task SyncStreams_applies_stream_then_upserts_packages_then_assigns_stream_to_each()
    {
        var api = new StubHttpMessageHandler((_, req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path.EndsWith("/waas/streams"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "cat_1", key = "tiered-orders", status = "Published" });
            if (req.Method == HttpMethod.Put && path.EndsWith("/waas/packages"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "pkg_x", key = "k", name = "n", status = "Active" });
            return new HttpResponseMessage(HttpStatusCode.NoContent); // assign
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(TieredOrder), null),
        });

        SyncResult result = await service.SyncStreamsAsync();

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.Total);                       // one stream
        Assert.Equal(2, result.Packages.Count);              // standard + premium
        Assert.All(result.Packages, p => Assert.True(p.Succeeded));
        Assert.All(result.Packages, p => Assert.Equal(1, p.AssignedStreams));

        var puts = api.Requests.Where(r => r.Method == HttpMethod.Put).Select(r => r.RequestUri!.AbsolutePath).ToList();
        Assert.Contains("/waas/streams", puts);
        Assert.Equal(2, puts.Count(p => p.EndsWith("/waas/packages")));

        var posts = api.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count); // one assign per (stream, package)
        Assert.All(posts, r => Assert.Contains("/packages/pkg_x/streams", r.RequestUri!.AbsolutePath));

        // The assign body carries the stream's catalog entry id.
        int assignIdx = api.Requests.FindIndex(r => r.Method == HttpMethod.Post);
        Assert.Contains("cat_1", Encoding.UTF8.GetString(api.Bodies[assignIdx]!));
    }

    [Fact]
    public async Task DryRun_lists_packages_and_touches_no_network()
    {
        var api = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(TieredOrder), null),
        });

        SyncResult result = await service.SyncStreamsAsync(new SyncOptions { DryRun = true });

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.Packages.Count);
        Assert.All(result.Packages, p => Assert.True(p.DryRun));
        Assert.All(result.Packages, p => Assert.Equal(1, p.AssignedStreams));
        Assert.Empty(api.Requests);

        StreamPlan plan = service.Plan().Single();
        Assert.Equal(new[] { "standard", "premium" }, plan.Packages.ToArray());
    }

    [Fact]
    public async Task ApplyPackageAsync_creates_a_package_directly()
    {
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "pkg_new", key = "premium", name = "Premium", status = "Active" }));
        QueueyService service = WaasTestHost.Build(apiStub: api);

        PackageApplyResult r = await service.ApplyPackageAsync("Premium", "Premium tier");

        Assert.True(r.Succeeded);
        Assert.Equal("pkg_new", r.PublicId);

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("https://api.example/waas/packages", req.RequestUri!.ToString());
        using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("ten_abc", doc.RootElement.GetProperty("producerTenantPublicId").GetString());
        Assert.Equal("Premium", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("Premium tier", doc.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Package_apply_failure_is_reported_and_fails_the_sync()
    {
        var api = new StubHttpMessageHandler((_, req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path.EndsWith("/waas/streams"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "cat_1", key = "tiered-orders", status = "Published" });
            if (req.Method == HttpMethod.Put && path.EndsWith("/waas/packages"))
                return StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "forbidden", message = "nope" } });
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(TieredOrder), null),
        });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => service.SyncStreamsAsync());
        SyncResult result = ex.Streams!;

        Assert.False(result.AllSucceeded);              // a package failed
        Assert.Equal(1, result.Succeeded);              // the stream itself applied
        Assert.All(result.Packages, p => Assert.False(p.Succeeded));
        Assert.All(result.Packages, p => Assert.IsType<QueueyForbiddenException>(p.Error));
    }
}
