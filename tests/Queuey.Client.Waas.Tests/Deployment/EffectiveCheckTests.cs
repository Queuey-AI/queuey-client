using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Drift-sjekken sammenligner med det køen faktisk kjører med. Før 2026-09-23 sammenlignet den med
/// den arvebevisste fila en pull skriver, som utelater en verdi som er lik workspacets — så en fil
/// som deklarerte den, meldte drift rett etter en ren apply. Og ingress-statusen ble aldri lest.
/// </summary>
public class EffectiveCheckTests
{
    private static object Policy(string ordering = "fifo", int maxAttempts = 8, object? filter = null) => new
    {
        idempotent = false, dlqEnabled = true, retentionDays = 7, ordering, maxAttempts,
        dlqAfterAttempts = 6,
        backoff = new { baseDelayMs = 1000, maxDelayMs = 60000, jitter = "full" },
        filter,
    };

    private static StubHttpMessageHandler Workspace(string mode = "Deliver", object? queuePolicy = null, int successStatusCode = 202, bool ownsBehavior = true)
        => new(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            if (path.EndsWith("/tenants/ten_abc/config", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy = Policy(), ingress = new { authMode = "None", successStatusCode = 202 } });
            if (path.EndsWith("/tenants/ten_abc/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode, hasDeliveryTarget = true } });

            // Køen: samme policy som workspacet (arvet eller eid, likt), og sin egen ingress-status.
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                delivery = new { baseUrl = "/orders", authMode = "None", hasCredential = false, timeoutMs = 30000 },
                policy = queuePolicy ?? Policy(),
                inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = !ownsBehavior },
                tenantBaseline = new { policy = Policy(), ingress = new { authMode = "None", successStatusCode = 202 } },
                ingress = new { authMode = "None", successStatusCode },
            });
        });

    private static Task<IReadOnlyList<DriftItem>> Check(StubHttpMessageHandler api, string json)
        => WaasTestHost.Build(apiStub: api).CheckDeploymentAsync(DeploymentFile.Parse(json));

    [Fact]
    public async Task A_queue_value_equal_to_the_workspace_is_in_sync_right_after_apply()
    {
        // Fila eier ordering=fifo og maxAttempts=8 på køen; workspacet har de samme verdiene.
        IReadOnlyList<DriftItem> drift = await Check(Workspace(), """
        { "tenant": "ten_abc", "queues": { "orders": {
            "ordering": "fifo", "maxAttempts": 8, "dlqAfterAttempts": 6,
            "backoff": { "baseDelayMs": 1000, "jitter": "full" } } } }
        """);

        Assert.Empty(drift);
    }

    [Fact]
    public async Task Retry_that_differs_is_drift_field_by_field()
    {
        IReadOnlyList<DriftItem> drift = await Check(Workspace(), """
        { "tenant": "ten_abc", "queues": { "orders": { "maxAttempts": 5, "backoff": { "maxDelayMs": 1000 } } } }
        """);

        Assert.Equal(new[] { "queues.orders.maxAttempts", "queues.orders.backoff.maxDelayMs" }, drift.Select(d => d.Path).ToArray());
        Assert.Equal("8", drift[0].Actual);
    }

    [Theory]
    [InlineData("deliver", "Deliver", false)]
    [InlineData("logOnly", "LogOnly", false)]
    [InlineData("deliver", "LogOnly", true)]
    [InlineData("deliver", "Paused", true)]
    public async Task The_mode_is_compared_in_the_files_words(string declared, string actual, bool drifts)
    {
        IReadOnlyList<DriftItem> drift = await Check(Workspace(mode: actual),
            $$"""{ "tenant": "ten_abc", "queues": { "orders": { "mode": "{{declared}}" } } }""");

        Assert.Equal(drifts, drift.Any(d => d.Path == "queues.orders.mode"));
    }

    [Fact]
    public async Task A_declared_filter_is_compared_and_no_filter_equals_an_empty_one()
    {
        object filtered = Policy(filter: new { match = "all", conditions = new[] { new { field = "type", op = "eq", value = "order.created" } } });

        Assert.Empty(await Check(Workspace(queuePolicy: filtered), """
        { "tenant": "ten_abc", "queues": { "orders": { "filter": { "conditions": [ { "field": "type", "op": "eq", "value": "order.created" } ] } } } }
        """));

        DriftItem changed = Assert.Single(await Check(Workspace(queuePolicy: filtered), """
        { "tenant": "ten_abc", "queues": { "orders": { "filter": { "conditions": [ { "field": "type", "op": "eq", "value": "order.paid" } ] } } } }
        """));
        Assert.Equal("queues.orders.filter", changed.Path);
        Assert.Equal("all: type eq order.created", changed.Actual);

        // Fila fjerner filteret med en tom liste; uten filter i workspacet er det i synk.
        Assert.Empty(await Check(Workspace(), """{ "tenant": "ten_abc", "queues": { "orders": { "filter": { "conditions": [] } } } }"""));
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(202, true)]
    public async Task The_ingress_success_status_is_read_back(int actual, bool drifts)
    {
        IReadOnlyList<DriftItem> drift = await Check(Workspace(successStatusCode: actual),
            """{ "tenant": "ten_abc", "queues": { "orders": { "ingress": { "successStatusCode": 200 } } } }""");

        Assert.Equal(drifts, drift.Any(d => d.Path == "queues.orders.ingress.successStatusCode"));
    }
}
