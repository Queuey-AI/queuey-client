using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

public class PackageLifecycleTests
{
    [Fact]
    public async Task UpdatePackage_puts_name_and_description()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            new { publicId = "pkg_9", key = "premium", name = "Premium+", status = "Active" }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        PackageApplyResult r = await svc.UpdatePackageAsync("pkg_9", "Premium+", "Renamed");

        Assert.Equal("pkg_9", r.PublicId);
        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("https://api.example/waas/producer/ten_abc/packages/pkg_9", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("Premium+", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("Renamed", doc.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ArchivePackage_posts_to_archive()
    {
        var api = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        await svc.ArchivePackageAsync("pkg_9");

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example/waas/producer/ten_abc/packages/pkg_9/archive", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task AssignAndRemove_stream_hit_the_membership_routes()
    {
        var api = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        await svc.AssignStreamToPackageAsync("pkg_9", "cat_1");
        Assert.Equal(HttpMethod.Post, api.LastRequest!.Method);
        Assert.Equal("https://api.example/waas/producer/ten_abc/packages/pkg_9/streams", api.LastRequest!.RequestUri!.ToString());

        await svc.RemoveStreamFromPackageAsync("pkg_9", "cat_1");
        Assert.Equal(HttpMethod.Delete, api.LastRequest!.Method);
        Assert.Equal("https://api.example/waas/producer/ten_abc/packages/pkg_9/streams/cat_1", api.LastRequest!.RequestUri!.ToString());
    }
}
