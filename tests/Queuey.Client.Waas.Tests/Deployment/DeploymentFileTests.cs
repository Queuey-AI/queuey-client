using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class DeploymentFileTests
{
    private const string Sample = """
    {
      "tenant": "ten_abc",
      "workspace": {
        "baseUrl": "https://hooks.example.com",
        "authMode": "ApiKey",
        "credentialRef": "partner-key",
        "authHeaderName": "X-Api-Key"
      },
      "queues": {
        "orders":   { "ordering": "bykey", "maxAttempts": 8, "delivery": { "url": "/orders" } },
        "invoices": { "retentionDays": 30 }
      }
    }
    """;

    private static object ApplyBody(string name) => new
    {
        publicId = "que_" + name,
        displayName = name,
        created = true,
        hasDeliveryTarget = true,
    };

    [Fact]
    public void Parses_the_workspace_and_the_queues()
    {
        DeploymentFile file = DeploymentFile.Parse(Sample);

        Assert.Equal("ten_abc", file.Tenant);
        Assert.Equal("https://hooks.example.com", file.Workspace!.BaseUrl);
        Assert.Equal("partner-key", file.Workspace.CredentialRef);
        Assert.Equal(new[] { "orders", "invoices" }, file.Queues.Keys.ToArray());
    }

    [Fact]
    public void Resolving_validates_names_and_policy_without_a_network()
    {
        var plans = DeploymentFile.Parse(Sample).Resolve();

        QueueDefinition orders = plans.Single(p => p.Definition.Name == "orders").Definition;
        Assert.Equal("bykey", orders.Policy.Ordering);
        Assert.Equal(8, orders.Policy.MaxAttempts);

        // A queue that declares no destination has none — it inherits the workspace, which is the
        // shape the docs lead with.
        Assert.Null(plans.Single(p => p.Definition.Name == "invoices").Delivery);
        Assert.Equal("/orders", plans.Single(p => p.Definition.Name == "orders").Delivery!.Url);
    }

    [Fact]
    public void A_misspelled_field_is_rejected_rather_than_ignored()
    {
        // The failure a declarative file must not have: reporting success while silently ignoring
        // what you wrote.
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => DeploymentFile.Parse("""{ "queues": { "orders": { "maxAttemps": 8 } } }"""));

        Assert.Contains("maxAttemps", ex.Message);
    }

    [Fact]
    public void An_invalid_queue_name_fails_when_the_file_is_resolved()
    {
        DeploymentFile file = DeploymentFile.Parse("""{ "queues": { "Order Events": {} } }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains("Did you mean 'order-events'?", ex.Message);
    }

    [Fact]
    public void An_invalid_ordering_fails_when_the_file_is_resolved()
    {
        DeploymentFile file = DeploymentFile.Parse("""{ "queues": { "orders": { "ordering": "sideways" } } }""");

        Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
    }

    /// <summary>Serves the credential listing (the file names one), then queue applies / patches.</summary>
    private static StubHttpMessageHandler ApplyStub() => new((_, req, _) =>
    {
        string path = req.RequestUri!.AbsolutePath;

        if (path.EndsWith("/credentials", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "cred_01", name = "partner-key", type = "Secret", keyId = (string?)null },
            });

        return path.EndsWith("/delivery", StringComparison.Ordinal) || path.EndsWith("/policy", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders"));
    });

    [Fact]
    public async Task Apply_converges_the_workspace_before_the_queues()
    {
        StubHttpMessageHandler api = ApplyStub();
        QueueyService service = WaasTestHost.Build(apiStub: api);

        await service.ApplyDeploymentAsync(DeploymentFile.Parse(Sample));

        var paths = api.Requests.Select(r => r.RequestUri!.AbsolutePath).ToArray();

        // The file names a credential, so the ids behind those names are resolved first.
        Assert.EndsWith("/tenants/ten_abc/credentials", paths[0]);

        // A queue that means to inherit needs something to inherit, so the workspace lands next.
        Assert.EndsWith("/tenants/ten_abc/delivery", paths[1]);
        Assert.Equal("PATCH", api.Requests[1].Method.Method);

        // Then per queue: apply, policy patch (when declared), delivery patch (when declared).
        Assert.Contains("/queues", paths);
        Assert.Contains("/queues/que_orders/policy", paths);
        Assert.Contains("/queues/que_orders/delivery", paths);
    }

    [Fact]
    public async Task A_credential_name_becomes_the_id_the_api_stores()
    {
        StubHttpMessageHandler api = ApplyStub();
        QueueyService service = WaasTestHost.Build(apiStub: api);

        await service.ApplyDeploymentAsync(DeploymentFile.Parse(Sample));

        int workspacePatch = api.Requests.ToList().FindIndex(
            r => r.RequestUri!.AbsolutePath.EndsWith("/tenants/ten_abc/delivery", StringComparison.Ordinal));
        using JsonDocument doc = JsonDocument.Parse(api.Bodies[workspacePatch]!);

        Assert.Equal("cred_01", doc.RootElement.GetProperty("credentialRef").GetString());
    }

    [Fact]
    public async Task An_unknown_credential_name_fails_with_what_to_do_about_it()
    {
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/credentials", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>())
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders")));

        QueueyService service = WaasTestHost.Build(apiStub: api);

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => service.ApplyDeploymentAsync(DeploymentFile.Parse(Sample)));

        Assert.Contains("No credential named 'partner-key'", ex.Message);
        Assert.Contains("queuey credentials set --name partner-key", ex.Message);
    }

    [Fact]
    public async Task The_workspace_patch_carries_only_what_the_file_declared()
    {
        StubHttpMessageHandler api = ApplyStub();
        QueueyService service = WaasTestHost.Build(apiStub: api);
        await service.ApplyDeploymentAsync(DeploymentFile.Parse(Sample));

        int workspacePatch = api.Requests.ToList().FindIndex(
            r => r.RequestUri!.AbsolutePath.EndsWith("/tenants/ten_abc/delivery", StringComparison.Ordinal));
        using JsonDocument doc = JsonDocument.Parse(api.Bodies[workspacePatch]!);
        Assert.Equal("https://hooks.example.com", doc.RootElement.GetProperty("baseUrl").GetString());
        Assert.Equal("ApiKey", doc.RootElement.GetProperty("authMode").GetString());

        // Fields the file never mentioned are absent, not null — "leave alone", unambiguously.
        Assert.False(doc.RootElement.TryGetProperty("timeoutMs", out _));
        Assert.False(doc.RootElement.TryGetProperty("signing", out _));
    }

    [Fact]
    public async Task A_queue_that_gets_its_own_destination_raises_no_readiness_warning()
    {
        // The apply response answered readiness BEFORE the delivery patch was sent, so warning here
        // would report a state that no longer exists by the time the run ends.
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/delivery", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = false,
                }));

        QueueyService service = WaasTestHost.Build(apiStub: api);

        QueueSyncResult result = await service.ApplyDeploymentAsync(DeploymentFile.Parse("""
        { "tenant": "ten_abc", "queues": { "orders": { "delivery": { "url": "https://x.example.com/in" } } } }
        """));

        Assert.True(result.AllSucceeded);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task A_failing_delivery_patch_fails_its_queue()
    {
        // Otherwise the queue reports "applied" while pointing nowhere.
        var api = new StubHttpMessageHandler((_, req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/delivery", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, new { error = new { code = "invalid_delivery", message = "bad url" } })
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, ApplyBody("orders")));

        QueueyService service = WaasTestHost.Build(apiStub: api);

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(
            () => service.ApplyDeploymentAsync(DeploymentFile.Parse("""
            { "tenant": "ten_abc", "queues": { "orders": { "delivery": { "url": "/orders" } } } }
            """)));

        Assert.Equal(0, ex.Queues!.Succeeded);
        Assert.Contains("bad url", ex.Queues.Applied.Single().Error!.Message);
    }
}
