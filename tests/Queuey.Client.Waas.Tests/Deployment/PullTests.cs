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
/// <c>pull</c> — reading a workspace back into a deployment file. Two properties carry the feature:
/// the output can never contain a secret (Queuey's read surfaces do not return them), and it is
/// inherit-aware, so it says what the workspace actually owns instead of freezing today's defaults
/// as permanent per-queue overrides.
/// </summary>
public class PullTests
{
    private static HttpResponseMessage Route(HttpRequestMessage req)
    {
        string path = req.RequestUri!.AbsolutePath;

        if (path.EndsWith("/credentials", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "cred_01", name = "partner-key", type = "Secret", keyId = (string?)null },
            });

        if (path.EndsWith("/tenants/ten_abc/config", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                delivery = new
                {
                    baseUrl = "https://hooks.example.com",
                    authMode = "ApiKey",
                    hasCredential = true,
                    credentialRef = "cred_01",
                    authHeaderName = "X-Api-Key",
                    method = "POST",
                    timeoutMs = 30000,
                },
            });

        if (path.EndsWith("/tenants/ten_abc/queues", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true },
                new { publicId = "que_plain", displayName = "plain", mode = "Deliver", hasDeliveryTarget = true },
                new { publicId = "que_legacy", displayName = "Legacy Queue", mode = "Deliver", hasDeliveryTarget = true },
            });

        if (path.EndsWith("/queues/que_orders/config", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                delivery = new { baseUrl = "/orders", authMode = "None", hasCredential = false, timeoutMs = 30000 },
                // Overrides ordering + retentionDays; everything else equals the workspace below.
                policy = new { idempotent = false, dlqEnabled = true, retentionDays = 30, ordering = "bykey" },
                inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = false },
                tenantBaseline = new { policy = Baseline },
            });

        // Inherits everything — the interesting case.
        return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            delivery = new { baseUrl = (string?)null, authMode = "ApiKey", hasCredential = true, credentialRef = "cred_01", timeoutMs = 30000 },
            policy = Baseline,
            inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = true },
            tenantBaseline = new { policy = Baseline },
        });
    }

    /// <summary>The workspace's effective policy — what an inheriting queue resolves to.</summary>
    private static object Baseline => new
    {
        idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo",
    };

    private static QueueyService Build() =>
        WaasTestHost.Build(apiStub: new StubHttpMessageHandler(Route));

    [Fact]
    public async Task Pulls_the_workspace_with_the_credential_as_a_name()
    {
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");

        Assert.Equal("ten_abc", file.Tenant);
        Assert.Equal("https://hooks.example.com", file.Workspace!.Delivery!.BaseUrl);
        Assert.Equal("ApiKey", file.Workspace.Delivery!.AuthMode);

        // The name, not cred_01 — ids are minted per workspace, so a file carrying one could only
        // ever apply where it was written.
        Assert.Equal("partner-key", file.Workspace.Delivery!.CredentialRef);
    }

    [Fact]
    public async Task An_overriding_queue_is_written_out_in_full()
    {
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");

        DeploymentQueue orders = file.Queues["orders"];
        Assert.Equal("bykey", orders.Ordering);
        Assert.Equal(30, orders.RetentionDays);

        // Fields that merely EQUAL the workspace are not written out, even though the server reports
        // the whole policy block as owned. Emitting them would freeze today's defaults as permanent
        // per-queue overrides on the next apply — the very thing inherit-awareness exists to prevent.
        Assert.Null(orders.DlqEnabled);
        Assert.Null(orders.Idempotent);

        // The raw override, not the resolved absolute — so it round-trips as an append.
        Assert.Equal("/orders", orders.Delivery!.Url);
    }

    [Fact]
    public async Task An_inheriting_queue_contributes_nothing()
    {
        // The property that makes pull safe to apply back: writing out every inherited value would
        // turn today's workspace defaults into permanent per-queue overrides on the first apply.
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");

        DeploymentQueue plain = file.Queues["plain"];
        Assert.Null(plain.Ordering);
        Assert.Null(plain.RetentionDays);
        Assert.Null(plain.RetentionDays);
        Assert.Null(plain.Delivery);
    }

    [Fact]
    public async Task A_name_the_apply_path_would_reject_is_skipped()
    {
        // Queues predating the server's name validator still exist and still route, but they cannot
        // be expressed in a file that apply would accept — emitting one would produce a file that
        // fails the moment anyone ran it.
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");

        Assert.DoesNotContain("Legacy Queue", file.Queues.Keys);
        Assert.Equal(new[] { "orders", "plain" }, file.Queues.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task The_rendered_file_holds_no_secret_and_omits_what_is_not_set()
    {
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");
        string json = file.ToJson();

        Assert.DoesNotContain("hasCredential", json);
        Assert.DoesNotContain("cred_01", json);
        Assert.Contains("partner-key", json);

        // Null properties are omitted, so an inheriting queue renders as an empty object rather than
        // a wall of nulls that would read as declarations.
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement plain = doc.RootElement.GetProperty("queues").GetProperty("plain");
        Assert.Equal(0, plain.EnumerateObject().Count());
    }

    [Fact]
    public async Task Check_reports_drift_against_the_live_workspace()
    {
        // The CI gate end to end: declare something the workspace does not hold, and the check says so
        // without writing anything.
        DeploymentFile declared = DeploymentFile.Parse("""
        { "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 99 } } }
        """);

        var api = new StubHttpMessageHandler(Route);
        QueueyService service = WaasTestHost.Build(apiStub: api);

        IReadOnlyList<DriftItem> drift = await service.CheckDeploymentAsync(declared);

        DriftItem item = Assert.Single(drift);
        Assert.Equal("queues.orders.retentionDays", item.Path);
        Assert.Equal("99", item.Declared);
        Assert.Equal("30", item.Actual);

        // Read-only: nothing was written on the way to the answer.
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Check_is_silent_when_the_file_matches()
    {
        DeploymentFile declared = DeploymentFile.Parse("""
        { "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 30, "ordering": "bykey" } } }
        """);

        QueueyService service = WaasTestHost.Build(apiStub: new StubHttpMessageHandler(Route));

        Assert.Empty(await service.CheckDeploymentAsync(declared));
    }

    [Fact]
    public async Task What_pull_writes_is_what_apply_reads()
    {
        // The round trip is the whole promise: configure once, pull, commit, converge elsewhere.
        DeploymentFile pulled = await Build().PullDeploymentAsync("ten_abc");

        DeploymentFile reparsed = DeploymentFile.Parse(pulled.ToJson());
        var plans = reparsed.Resolve();

        Assert.Equal("https://hooks.example.com", reparsed.Workspace!.Delivery!.BaseUrl);
        Assert.Equal(new[] { "orders", "plain" }, plans.Select(p => p.Definition.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("bykey", plans.Single(p => p.Definition.Name == "orders").Definition.Policy.Ordering);
        Assert.Null(plans.Single(p => p.Definition.Name == "plain").Delivery);
    }
}

/// <summary>What a pull writes for the fields a deployment file gained 2026-09-23.</summary>
public class PullDesiredStateTests
{
    private static StubHttpMessageHandler Api(string mode, bool hasTarget, object queuePolicy, int successStatusCode = 202)
        => new(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            if (path.EndsWith("/tenants/ten_abc/config", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy = Baseline });
            if (path.EndsWith("/tenants/ten_abc/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode, hasDeliveryTarget = hasTarget } });
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                policy = queuePolicy,
                inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = false },
                tenantBaseline = new { policy = Baseline, ingress = new { authMode = "None", successStatusCode = 202 } },
                ingress = new { authMode = "None", successStatusCode },
            });
        });

    private static object Baseline => new
    {
        idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo", maxAttempts = 8, dlqAfterAttempts = (int?)null,
        backoff = new { baseDelayMs = 1000, maxDelayMs = 60000, jitter = "full" },
    };

    private static Task<DeploymentFile> Pull(StubHttpMessageHandler api) => WaasTestHost.Build(apiStub: api).PullDeploymentAsync("ten_abc");

    [Theory]
    [InlineData("LogOnly", true, "logOnly")]   // logger selv om den har et mål: det må stå i fila
    [InlineData("LogOnly", false, null)]       // uten mål logger den uansett
    [InlineData("Deliver", true, null)]        // standarden gir det samme
    [InlineData("Paused", true, null)]         // ingen modus en fil setter
    public async Task The_mode_is_written_only_where_the_default_would_get_it_wrong(string mode, bool hasTarget, string? expected)
    {
        DeploymentFile file = await Pull(Api(mode, hasTarget, Baseline));

        Assert.Equal(expected, file.Queues["orders"].Mode);
    }

    [Fact]
    public async Task Retry_and_filter_are_written_when_they_differ_from_the_workspace()
    {
        object policy = new
        {
            idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo", maxAttempts = 3, dlqAfterAttempts = 2,
            backoff = new { baseDelayMs = 1000, maxDelayMs = 60000, jitter = "full" },
            filter = new { match = "any", conditions = new[] { new { field = "type", op = "eq", value = "order.created" } } },
            // En server fra før Queuey#345 lagret disse, men workeren leste dem aldri. Pull skriver dem
            // ikke: en fil med dem avvises som ukjent felt.
            retryOnNetworkErrors = false, retryOnTimeouts = true,
        };

        DeploymentFile pulled = await Pull(Api("Deliver", true, policy));
        DeploymentQueue orders = pulled.Queues["orders"];

        Assert.Equal(3, orders.MaxAttempts);
        Assert.Equal(2, orders.DlqAfterAttempts);
        Assert.Equal("any: type eq order.created", orders.Filter!.ToString());

        // Likt workspacet: utelatt, så det fortsetter å arve.
        Assert.Null(orders.Backoff);

        // Og det pull skriver, godtar apply.
        string json = pulled.ToJson();
        Assert.DoesNotContain("retryOn", json, StringComparison.Ordinal);
        Assert.Single(DeploymentFile.Parse(json).Resolve());
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(202, null)]
    public async Task A_success_status_other_than_the_default_is_written(int actual, int? expected)
    {
        DeploymentFile file = await Pull(Api("Deliver", true, Baseline, successStatusCode: actual));

        Assert.Equal(expected, file.Queues["orders"].Ingress?.SuccessStatusCode);
    }
}
