using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Sperrehake (review 2026-09-24): en plan skal aldri sende en skriving uten <c>?dryRun=true</c>. Fila
/// bruker hvert aspekt en plan kan skrive — workspace policy, delivery og ingress, og kø policy,
/// delivery, ingress, modus og filter — mot en server som nekter enhver skriving som ikke er dry-run.
/// Kommer det et nytt slags kall, feiler testen til det står i lista over det planen sender.
/// </summary>
public class DeploymentPlanDryRunRatchetTests
{
    private const string EveryAspect = """
    {
      "tenant": "ten_abc",
      "workspace": {
        "ordering": "bykey", "retentionDays": 30, "maxAttempts": 8, "backoff": { "baseDelayMs": 1000 },
        "ingress": { "authMode": "ApiKey", "eventType": { "from": "body", "name": "type" }, "groupKey": { "from": "body", "name": "customerId" } },
        "delivery": {
          "baseUrl": "https://hooks.example.com", "authMode": "ApiKey", "credentialRef": "partner-key", "authHeaderName": "X-Api-Key",
          "signing": { "enabled": true, "credentialRef": "signing-key" }, "rateLimit": { "maxRequests": 10, "perSeconds": 1 }
        }
      },
      "queues": {
        "orders": {
          "mode": "deliver",
          "maxAttempts": 5, "dlqAfterAttempts": 3,
          "filter": { "match": "any", "conditions": [ { "field": "type", "op": "eq", "value": "order.created" } ] },
          "ingress": { "successStatusCode": 200 },
          "delivery": { "url": "/orders", "credentialRef": "orders-key" }
        }
      }
    }
    """;

    private static readonly string[] Writes =
    {
        "PATCH /tenants/ten_abc/ingress",
        "PATCH /tenants/ten_abc/policy",
        "PATCH /tenants/ten_abc/delivery",
        "PUT /queues",
        "PATCH /queues/que_orders/ingress",
        "PATCH /queues/que_orders/policy",
        "PATCH /queues/que_orders/delivery",
        "PATCH /queues/que_orders/mode-change",
    };

    [Fact]
    public async Task Every_write_a_plan_sends_is_a_dry_run()
    {
        var notDryRun = new List<string>();
        var sent = new List<(string Key, string? Body)>();

        var api = new StubHttpMessageHandler((_, req, body) =>
        {
            string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";

            if (req.Method == HttpMethod.Get)
            {
                return req.RequestUri.AbsolutePath switch
                {
                    "/tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
                    {
                        new { publicId = "que_orders", displayName = "orders", mode = "LogOnly", hasDeliveryTarget = true },
                    }),
                    "/tenants/ten_abc/credentials" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
                    {
                        new { publicId = "cred_partner", name = "partner-key", type = "ApiKeyHeader" },
                        new { publicId = "cred_signing", name = "signing-key", type = "HmacSigning" },
                        new { publicId = "cred_orders", name = "orders-key", type = "ApiKeyHeader" },
                    }),
                    string other => throw new InvalidOperationException("unexpected read " + other),
                };
            }

            sent.Add((key, body is null ? null : Encoding.UTF8.GetString(body)));
            if (req.RequestUri.Query != "?dryRun=true")
            {
                // En ekte skriving: nektet, og husket, så testen sier hvilken.
                notDryRun.Add(key + req.RequestUri.Query);
                return StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, new { error = new { code = "not_a_dry_run", message = key } });
            }

            return key == "PUT /queues"
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true })
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = key, changes = Array.Empty<object>(), notes = Array.Empty<string>() });
        });

        DeploymentPlan plan = await WaasTestHost.Build(apiStub: api).PlanDeploymentAsync(DeploymentFile.Parse(EveryAspect));

        Assert.True(notDryRun.Count == 0, "sent without ?dryRun=true: " + string.Join(", ", notDryRun));
        Assert.True(plan.WouldSucceed, string.Join("; ", plan.Steps.Where(s => s.Error is not null).Select(s => $"{s.Target} · {s.Aspect}: {s.Error!.Message}")));

        // Hvert aspekt ble faktisk skrevet, så testen ikke kan bestå uten å ha prøvd dem — og ingenting annet.
        Assert.Equal(Writes.OrderBy(w => w, StringComparer.Ordinal), sent.Select(s => s.Key).Distinct().OrderBy(w => w, StringComparer.Ordinal));
        Assert.Contains("\"filter\"", sent.Single(s => s.Key == "PATCH /queues/que_orders/policy").Body);
        Assert.Contains("\"signing\"", sent.Single(s => s.Key == "PATCH /tenants/ten_abc/delivery").Body);
    }
}
