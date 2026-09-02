using System;
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
                new { publicId = "que_plain", displayName = "plain", mode = "LogOnly", hasDeliveryTarget = true },
                new { publicId = "que_legacy", displayName = "Legacy Queue", mode = "Deliver", hasDeliveryTarget = true },
            });

        if (path.EndsWith("/queues/que_orders/config", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                delivery = new { baseUrl = "/orders", authMode = "None", hasCredential = false, timeoutMs = 30000 },
                policy = new { idempotent = true, dlqEnabled = true, dlqAfterAttempts = 5, maxAttempts = 8, retentionDays = 30, ordering = "bykey" },
                inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = false },
            });

        // Inherits everything — the interesting case.
        return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            delivery = new { baseUrl = (string?)null, authMode = "ApiKey", hasCredential = true, credentialRef = "cred_01", timeoutMs = 30000 },
            policy = new { idempotent = false, dlqEnabled = false, dlqAfterAttempts = (int?)null, maxAttempts = 3, retentionDays = 7, ordering = "fifo" },
            inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = true },
        });
    }

    private static QueueyService Build() =>
        WaasTestHost.Build(apiStub: new StubHttpMessageHandler(Route));

    [Fact]
    public async Task Pulls_the_workspace_with_the_credential_as_a_name()
    {
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");

        Assert.Equal("ten_abc", file.Tenant);
        Assert.Equal("https://hooks.example.com", file.Workspace!.BaseUrl);
        Assert.Equal("ApiKey", file.Workspace.AuthMode);

        // The name, not cred_01 — ids are minted per workspace, so a file carrying one could only
        // ever apply where it was written.
        Assert.Equal("partner-key", file.Workspace.CredentialRef);
    }

    [Fact]
    public async Task An_overriding_queue_is_written_out_in_full()
    {
        DeploymentFile file = await Build().PullDeploymentAsync("ten_abc");

        DeploymentQueue orders = file.Queues["orders"];
        Assert.Equal("bykey", orders.Ordering);
        Assert.Equal(8, orders.MaxAttempts);
        Assert.Equal(30, orders.RetentionDays);

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
        Assert.Null(plain.MaxAttempts);
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
    public async Task What_pull_writes_is_what_apply_reads()
    {
        // The round trip is the whole promise: configure once, pull, commit, converge elsewhere.
        DeploymentFile pulled = await Build().PullDeploymentAsync("ten_abc");

        DeploymentFile reparsed = DeploymentFile.Parse(pulled.ToJson());
        var plans = reparsed.Resolve();

        Assert.Equal("https://hooks.example.com", reparsed.Workspace!.BaseUrl);
        Assert.Equal(new[] { "orders", "plain" }, plans.Select(p => p.Definition.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("bykey", plans.Single(p => p.Definition.Name == "orders").Definition.Policy.Ordering);
        Assert.Null(plans.Single(p => p.Definition.Name == "plain").Delivery);
    }
}
