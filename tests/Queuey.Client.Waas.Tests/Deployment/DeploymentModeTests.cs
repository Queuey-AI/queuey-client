using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Ønsket tilstand som dekker nok (2026-09-23): modus, retry og filter i deployment-fila, og at
/// apply faktisk gjør køen klar til å levere. Modus settes sist, fordi målet avgjør om køen kan
/// levere i det hele tatt; pause er en operatørs spak, og en deploy rører den aldri.
/// </summary>
public class DeploymentModeTests
{
    /// <summary>A queue listing row, as <c>GET /tenants/{ten}/queues</c> returns it.</summary>
    private static object Row(string name, string mode, bool hasTarget = true, bool held = false, bool ingressClosed = false) => new
    {
        publicId = "que_" + name,
        displayName = name,
        mode,
        hasDeliveryTarget = hasTarget,
        ingressClosed,
        deliveryHeld = held,
        suspended = false,
    };

    private sealed class Api
    {
        public bool Created { get; init; } = true;
        public bool HasTarget { get; init; } = true;
        public object[] Rows { get; init; } = Array.Empty<object>();
        public StubHttpMessageHandler Stub { get; }

        public Api() => Stub = new StubHttpMessageHandler((_, req, body) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Rows);
            if (req.Method == HttpMethod.Put && path == "/queues")
            {
                string name = JsonDocument.Parse(body!).RootElement.GetProperty("displayName").GetString()!;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_" + name, displayName = name, created = Created, hasDeliveryTarget = HasTarget });
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        public List<string> Paths => Stub.Requests.Select(r => $"{r.Method.Method} {r.RequestUri!.AbsolutePath}").ToList();

        public JsonElement Body(string methodAndPath)
        {
            int i = Paths.IndexOf(methodAndPath);
            Assert.True(i >= 0, $"expected {methodAndPath}, got: {string.Join(", ", Paths)}");
            return JsonDocument.Parse(Stub.Bodies[i]!).RootElement;
        }
    }

    private static Task<QueueSyncResult> Apply(Api api, string json)
        => WaasTestHost.Build(apiStub: api.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(json));

    // ── modus ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_queue_with_a_destination_delivers_and_the_mode_is_set_last()
    {
        var api = new Api { HasTarget = true };

        QueueSyncResult result = await Apply(api, """
        { "workspace": { "delivery": { "baseUrl": "https://hooks.example.com" } },
          "queues": { "orders": { "ordering": "fifo", "delivery": { "url": "/orders" } } } }
        """);

        QueueApplyResult orders = result.Applied.Single();
        Assert.Equal("deliver", orders.Mode);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, api.Body("PATCH /queues/que_orders/mode-change").GetProperty("mode").GetInt32());

        // Etter policy og mål — køen leverer aldri før den vet hvor.
        Assert.Equal("PATCH /queues/que_orders/mode-change", api.Paths[^1]);
        Assert.True(api.Paths.IndexOf("PATCH /queues/que_orders/delivery") < api.Paths.IndexOf("PATCH /queues/que_orders/mode-change"));
    }

    [Fact]
    public async Task A_new_queue_with_nowhere_to_deliver_logs_and_says_how_to_fix_it()
    {
        var api = new Api { HasTarget = false };

        QueueSyncResult result = await Apply(api, """{ "queues": { "orders": {} } }""");

        Assert.Equal("logOnly", result.Applied.Single().Mode);
        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/mode-change", StringComparison.Ordinal));
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("no delivery target", warning);
        Assert.Contains("workspace.delivery.baseUrl", warning);
    }

    [Fact]
    public async Task Declaring_deliver_with_nowhere_to_deliver_fails_the_queue()
    {
        var api = new Api { HasTarget = false };

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(
            () => Apply(api, """{ "queues": { "orders": { "mode": "deliver" } } }"""));

        QueueyException error = ex.Queues!.Applied.Single().Error!;
        Assert.Equal("deliver_without_destination", error.ErrorCode);
        Assert.Contains("delivery.url", error.Message);
        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/mode-change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_existing_queue_keeps_its_mode_when_the_file_does_not_declare_one()
    {
        // Modusen til en kø som finnes, er noens valg; fila eier den bare når den sier noe.
        var api = new Api { Created = false, HasTarget = true, Rows = new[] { Row("orders", "LogOnly") } };

        QueueSyncResult result = await Apply(api, """{ "queues": { "orders": {} } }""");

        Assert.Equal("logOnly", result.Applied.Single().Mode);
        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/mode-change", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("has a destination but is in logOnly mode") && w.Contains("\"mode\": \"deliver\""));
    }

    [Fact]
    public async Task A_declared_mode_is_converged_on_an_existing_queue_and_not_rewritten_when_it_matches()
    {
        var logging = new Api { Created = false, Rows = new[] { Row("orders", "LogOnly") } };
        await Apply(logging, """{ "queues": { "orders": { "mode": "deliver" } } }""");
        Assert.Equal(3, logging.Body("PATCH /queues/que_orders/mode-change").GetProperty("mode").GetInt32());

        var delivering = new Api { Created = false, Rows = new[] { Row("orders", "Deliver") } };
        QueueSyncResult result = await Apply(delivering, """{ "queues": { "orders": { "mode": "Deliver" } } }""");
        Assert.DoesNotContain(delivering.Paths, p => p.EndsWith("/mode-change", StringComparison.Ordinal));
        Assert.Equal("deliver", result.Applied.Single().Mode);

        var quiet = new Api { Created = false, Rows = new[] { Row("orders", "Deliver") } };
        await Apply(quiet, """{ "queues": { "orders": { "mode": "logOnly" } } }""");
        Assert.Equal(1, quiet.Body("PATCH /queues/que_orders/mode-change").GetProperty("mode").GetInt32());
    }

    [Fact]
    public async Task The_old_paused_mode_is_never_changed_because_changing_it_resumes_the_queue()
    {
        var api = new Api { Created = false, Rows = new[] { Row("orders", "Paused") } };

        QueueSyncResult result = await Apply(api, """{ "queues": { "orders": { "mode": "deliver" } } }""");

        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/mode-change", StringComparison.Ordinal));
        Assert.Equal("paused", result.Applied.Single().Mode);
        Assert.Contains(result.Warnings, w => w.Contains("old Paused mode") && w.Contains("resume it in the Queuey console"));
    }

    [Fact]
    public async Task A_held_or_closed_queue_is_reported_and_left_as_it_is()
    {
        var api = new Api { Created = false, Rows = new[] { Row("orders", "Deliver", held: true, ingressClosed: true) } };

        QueueSyncResult result = await Apply(api, """{ "queues": { "orders": { "mode": "deliver" } } }""");

        Assert.Contains(result.Warnings, w => w.Contains("Delivery is held on queue 'orders'") && w.Contains("never resumes"));
        Assert.Contains(result.Warnings, w => w.Contains("does not accept new events") && w.Contains("never reopens"));
        Assert.DoesNotContain(api.Paths, p => p.Contains("/flow", StringComparison.Ordinal) || p.EndsWith("/mode-change", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sometimes", "must be one of deliver, logOnly")]
    [InlineData("paused", "not a mode a deployment file sets")]
    public void A_mode_the_file_cannot_set_fails_before_anything_is_sent(string mode, string expected)
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "queues": { "orders": { "mode": "{{mode}}" } } }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains("queues.orders.mode", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    // ── tenant og ingress (feil funnet i gap-analysen) ───────────────────────

    [Fact]
    public async Task The_queues_go_to_the_workspace_the_file_names()
    {
        // Før 2026-09-23 gikk workspacet til fila sin tenant og køene til den konfigurerte.
        var api = new Api();

        await Apply(api, """{ "tenant": "ten_file", "queues": { "orders": {} } }""");

        Assert.Equal("ten_file", api.Body("PUT /queues").GetProperty("tenantPublicId").GetString());
        Assert.Contains("GET /tenants/ten_file/queues", api.Paths);
    }

    [Fact]
    public async Task A_queue_ingress_reaches_the_api()
    {
        // Expand kopierte ikke ingress på kø-nivå, så apply, check og dry-run droppet den stille.
        var api = new Api();

        await Apply(api, """{ "queues": { "orders": { "ingress": { "successStatusCode": 200, "eventType": { "from": "body", "name": "type" } } } } }""");

        JsonElement ingress = api.Body("PATCH /queues/que_orders/ingress");
        Assert.Equal(200, ingress.GetProperty("successStatusCode").GetInt32());
        Assert.Equal("type", ingress.GetProperty("eventType").GetProperty("name").GetString());
    }

    // ── retry og filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_and_filter_reach_the_queue_policy_patch()
    {
        var api = new Api();

        await Apply(api, """
        { "queues": { "orders": {
            "maxAttempts": 8, "dlqAfterAttempts": 6,
            "backoff": { "baseDelayMs": 500, "maxDelayMs": 60000, "jitter": "full" },
            "filter": { "match": "any", "conditions": [
              { "field": "type", "op": "eq", "value": "order.created" },
              { "field": "priority", "op": "exists" } ] } } } }
        """);

        JsonElement policy = api.Body("PATCH /queues/que_orders/policy");
        Assert.Equal(8, policy.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(6, policy.GetProperty("dlqAfterAttempts").GetInt32());
        Assert.Equal(500, policy.GetProperty("backoff").GetProperty("baseDelayMs").GetInt32());
        Assert.Equal("full", policy.GetProperty("backoff").GetProperty("jitter").GetString());

        JsonElement filter = policy.GetProperty("filter");
        Assert.Equal("any", filter.GetProperty("match").GetString());
        JsonElement exists = filter.GetProperty("conditions")[1];
        Assert.Equal("exists", exists.GetProperty("op").GetString());
        Assert.False(exists.TryGetProperty("value", out _));   // udeklarert er fraværende, ikke null

        // Ikke deklarert: ikke med i patchen, så verdien arves videre.
        Assert.False(policy.TryGetProperty("ordering", out _));
    }

    [Fact]
    public async Task A_filter_without_conditions_is_sent_so_the_old_filter_is_removed()
    {
        var api = new Api();

        await Apply(api, """{ "queues": { "orders": { "filter": { "conditions": [] } } } }""");

        Assert.Equal(0, api.Body("PATCH /queues/que_orders/policy").GetProperty("filter").GetProperty("conditions").GetArrayLength());
    }

    [Fact]
    public async Task Workspace_retry_reaches_the_workspace_policy_patch()
    {
        var api = new Api();

        await Apply(api, """{ "tenant": "ten_abc", "workspace": { "maxAttempts": 5, "backoff": { "baseDelayMs": 1000 } }, "queues": {} }""");

        JsonElement policy = api.Body("PATCH /tenants/ten_abc/policy");
        Assert.Equal(5, policy.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(1000, policy.GetProperty("backoff").GetProperty("baseDelayMs").GetInt32());
        Assert.False(policy.GetProperty("backoff").TryGetProperty("jitter", out _));
    }

    [Theory]
    [InlineData("""{ "maxAttempts": 5, "dlqAfterAttempts": 5 }""", "must be below MaxAttempts")]
    [InlineData("""{ "backoff": { "jitter": "some" } }""", "Jitter must be one of none, full")]
    [InlineData("""{ "backoff": { "baseDelayMs": 2000, "maxDelayMs": 1000 } }""", "cannot be below BaseDelayMs")]
    [InlineData("""{ "filter": { "match": "most", "conditions": [] } }""", "Match must be one of all, any")]
    [InlineData("""{ "filter": { "conditions": [ { "field": "type", "op": "like", "value": "x" } ] } }""", "must be one of eq, ne")]
    [InlineData("""{ "filter": { "conditions": [ { "field": "type", "op": "eq" } ] } }""", "needs a value")]
    public void A_retry_or_filter_mistake_fails_locally_and_names_what_works(string queue, string expected)
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "queues": { "orders": {{queue}} } }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_workspace_retry_mistake_fails_locally_too()
    {
        DeploymentFile file = DeploymentFile.Parse("""{ "workspace": { "maxAttempts": 0 }, "queues": {} }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains("workspace", ex.Message);
        Assert.Contains("MaxAttempts must be at least 1", ex.Message);
    }

    [Fact]
    public void The_schema_key_is_accepted_and_ignored()
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "$schema": "{{DeploymentFile.SchemaUrl}}", "queues": { "orders": {} } }""");

        Assert.Equal(DeploymentFile.SchemaUrl, file.Schema);
        Assert.Single(file.Resolve());
    }
}
