using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

public class ManagementTests
{
    [Fact]
    public async Task CreateTenant_posts_to_tenants_and_returns_the_tenant()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            new { publicId = "ten_new", displayName = "Acme", status = "Active", kind = "ProducerSystem", queues = new object[0] }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        TenantResult r = await svc.Management.CreateTenantAsync("Acme", asProducer: true, withDefaultQueue: true);

        Assert.Equal("ten_new", r.PublicId);
        Assert.Equal("ProducerSystem", r.Kind);

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example/tenants", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("Acme", doc.RootElement.GetProperty("displayName").GetString());
        Assert.True(doc.RootElement.GetProperty("createAsProducer").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("createDefaultQueue").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("licenseId").GetInt64()); // header supplies the license
    }

    [Fact]
    public async Task CreateQueue_posts_to_queues_under_the_tenant()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            new { publicId = "que_new", tenantPublicId = "ten_abc", displayName = "orders" }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        QueueResult r = await svc.Management.CreateQueueAsync("ten_abc", "orders");

        Assert.Equal("que_new", r.PublicId);
        Assert.Equal("orders", r.DisplayName);

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example/queues", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("ten_abc", doc.RootElement.GetProperty("tenantPublicId").GetString());
        Assert.Equal("orders", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task CreateTenant_fails_fast_without_a_license()
    {
        QueueyService svc = WaasTestHost.Build(configure: o => o.LicensePublicId = null);
        await Assert.ThrowsAsync<QueueyConfigurationException>(() => svc.Management.CreateTenantAsync("Acme"));
    }
}
