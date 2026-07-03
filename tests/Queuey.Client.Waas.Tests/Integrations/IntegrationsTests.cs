using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

public class IntegrationsTests
{
    [Fact]
    public async Task Invite_posts_to_waas_integrations_with_producer_and_email()
    {
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "int_1", invitedEmail = "p@acme.io", status = "Invited" }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        IntegrationResult r = await svc.Integrations.InviteAsync("p@acme.io");

        Assert.Equal("int_1", r.PublicId);
        Assert.Equal("Invited", r.Status);

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example/waas/integrations", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("ten_abc", doc.RootElement.GetProperty("producerTenantPublicId").GetString());
        Assert.Equal("p@acme.io", doc.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task GrantPackage_posts_to_the_grants_route_with_the_integration()
    {
        var api = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        await svc.Integrations.GrantPackageAsync("int_1", "pkg_9");

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example/waas/producer/ten_abc/packages/pkg_9/grants", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("int_1", doc.RootElement.GetProperty("integrationPublicId").GetString());
    }

    [Fact]
    public async Task RevokePackage_deletes_the_grant()
    {
        var api = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        await svc.Integrations.RevokePackageAsync("int_1", "pkg_9");

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("https://api.example/waas/producer/ten_abc/packages/pkg_9/grants/int_1", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Activate_posts_to_activations_and_returns_the_activation()
    {
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "act_1", groupKey = "cust_42", status = "Active" }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        ActivationResult r = await svc.Integrations.ActivateAsync("int_1", "cust_42");

        Assert.Equal("act_1", r.PublicId);
        Assert.Equal("cust_42", r.GroupKey);

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example/waas/activations", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("ten_abc", doc.RootElement.GetProperty("producerTenantPublicId").GetString());
        Assert.Equal("cust_42", doc.RootElement.GetProperty("groupKey").GetString());
        Assert.Equal("int_1", doc.RootElement.GetProperty("integrationPublicId").GetString());
    }

    [Fact]
    public async Task Deactivate_deletes_activations_carrying_the_request_in_the_body()
    {
        var api = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        await svc.Integrations.DeactivateAsync("int_1", "cust_42");

        HttpRequestMessage req = api.LastRequest!;
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("https://api.example/waas/activations", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(api.LastBody!));
        Assert.Equal("cust_42", doc.RootElement.GetProperty("groupKey").GetString());
        Assert.Equal("int_1", doc.RootElement.GetProperty("integrationPublicId").GetString());
    }

    [Fact]
    public async Task Integration_ops_fail_fast_without_a_license()
    {
        QueueyService svc = WaasTestHost.Build(configure: o => o.LicensePublicId = null);
        await Assert.ThrowsAsync<QueueyConfigurationException>(() => svc.Integrations.InviteAsync("p@acme.io"));
    }
}
