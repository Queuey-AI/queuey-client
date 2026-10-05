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
/// Backoff-takene (Queuey#391, 2026-10-04): en skriving kan gjøre første ventetid høyst en time og den lengste høyst
/// et døgn, men bare når den endrer ventetiden køen eller workspacet kjører med. Apply sjekker det før første
/// skriving, så en ventetid Queuey ville avvist, ikke feiler midt i en kjøring. Og en lengre ventetid som sto fra før
/// taket, går gjennom uendret, slik den gjør på serveren: en pull skriver den inn i fila.
/// </summary>
public class BackoffCeilingTests
{
    private const int Hour = 3_600_000;
    private const int Day = 86_400_000;

    private sealed class Api
    {
        public object[] Rows { get; init; } = Array.Empty<object>();
        public object WorkspaceBackoff { get; init; } = new { baseDelayMs = 500, maxDelayMs = 60000, jitter = "full" };
        public object QueueBackoff { get; init; } = new { baseDelayMs = 500, maxDelayMs = 60000, jitter = "full" };
        public HttpStatusCode ConfigStatus { get; init; } = HttpStatusCode.OK;

        public StubHttpMessageHandler Stub { get; }

        public Api() => Stub = new StubHttpMessageHandler((_, req, body) =>
        {
            string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";
            if (key.EndsWith("/config", StringComparison.Ordinal) && ConfigStatus != HttpStatusCode.OK)
                return StubHttpMessageHandler.Json(ConfigStatus, new { error = new { code = "forbidden", message = "Missing permission." } });

            switch (key)
            {
                case "GET /tenants/ten_abc/queues":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, Rows);
                case "GET /tenants/ten_abc/config":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy = new { backoff = WorkspaceBackoff } });
                case "GET /queues/que_orders/config":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy = new { backoff = QueueBackoff } });
                case "PUT /queues":
                    string name = JsonDocument.Parse(body!).RootElement.GetProperty("displayName").GetString()!;
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_" + name, displayName = name, created = Rows.Length == 0, hasDeliveryTarget = true });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        });

        public List<string> Paths => Stub.Requests.Select(r => $"{r.Method.Method} {r.RequestUri!.AbsolutePath}").ToList();

        public List<string> Writes => Paths.Where(p => !p.StartsWith("GET ", StringComparison.Ordinal)).ToList();
    }

    private static readonly object[] OrdersExists = { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } };

    private static Task<QueueSyncResult> Apply(Api api, string json)
        => WaasTestHost.Build(apiStub: api.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(json));

    [Fact]
    public async Task A_wait_above_a_ceiling_that_would_change_the_queue_is_refused_before_anything_is_written()
    {
        var api = new Api { Rows = OrdersExists };

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => Apply(api, $$"""
        { "workspace": { "retentionDays": 30 },
          "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}}, "maxDelayMs": {{2 * Day}} } } } }
        """));

        Assert.Contains($"queues.orders.backoff.baseDelayMs is {2 * Hour}, above the {Hour} (one hour) a first wait may be, and would change it from 500", ex.Message);
        Assert.Contains($"queues.orders.backoff.maxDelayMs is {2 * Day}, above the {Day} (24 hours) the longest wait may be, and would change it from 60000", ex.Message);
        Assert.Contains("Nothing was changed.", ex.Message);
        Assert.Equal($"Declare at most {Hour} for backoff.baseDelayMs and {Day} for backoff.maxDelayMs, or leave the field out.", ex.SuggestedAction);

        // Ikke engang workspacet, som ellers skrives først.
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task A_longer_wait_already_in_place_is_applied_unchanged()
    {
        // Køen arver to timer fra et workspace som fikk dem før taket, og pull skriver backoff hel inn i fila. En
        // uendret apply av den fila endrer ingenting, og serveren godtar den (Queuey#391), så klienten gjør det også.
        var api = new Api
        {
            Rows = OrdersExists,
            QueueBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = 2 * Day, jitter = "full" },
        };

        QueueSyncResult result = await Apply(api, $$"""
        { "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}}, "maxDelayMs": {{2 * Day}}, "jitter": "none" } } } }
        """);

        Assert.True(result.AllSucceeded);
        Assert.Contains("PATCH /queues/que_orders/policy", api.Writes);
    }

    [Theory]
    [InlineData(2 * Hour, null, true)]          // workspacet har to timer, og den nye køen arver dem: uendret
    [InlineData(1_000, null, false)]            // workspacet har ett sekund: køen ville endret det
    [InlineData(2 * Hour, 1_800_000, false)]    // fila setter workspacet til en halvtime først, og det arver køen
    public async Task A_new_queue_compares_with_the_wait_the_workspace_will_have(int workspaceNow, int? fileWorkspace, bool applies)
    {
        var api = new Api { WorkspaceBackoff = new { baseDelayMs = workspaceNow, maxDelayMs = Day, jitter = "full" } };
        string workspace = fileWorkspace is { } w ? $$""" "workspace": { "backoff": { "baseDelayMs": {{w}} } }, """ : string.Empty;

        Task<QueueSyncResult> apply = Apply(api, $$"""{ {{workspace}} "queues": { "invoices": { "backoff": { "baseDelayMs": {{2 * Hour}} } } } }""");

        if (applies)
        {
            Assert.True((await apply).AllSucceeded);
            Assert.Contains("PATCH /queues/que_invoices/policy", api.Writes);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => apply);
            Assert.Contains($"queues.invoices.backoff.baseDelayMs is {2 * Hour}", ex.Message);
            Assert.Contains($"would change it from {fileWorkspace ?? workspaceNow}", ex.Message);
            Assert.Empty(api.Writes);
        }
    }

    [Fact]
    public async Task A_workspace_wait_above_a_ceiling_is_refused_when_it_changes()
    {
        var api = new Api();

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => Apply(api, $$"""
        { "workspace": { "backoff": { "maxDelayMs": {{2 * Day}} } }, "queues": {} }
        """));

        Assert.Contains($"workspace.backoff.maxDelayMs is {2 * Day}, above the {Day} (24 hours) the longest wait may be, and would change it from 60000", ex.Message);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task A_file_within_the_ceilings_reads_no_config()
    {
        // Den vanlige fila koster ingen ekstra kall: takene leses bare for en ventetid over dem.
        var api = new Api { Rows = OrdersExists };

        await Apply(api, $$"""
        { "workspace": { "backoff": { "baseDelayMs": {{Hour}}, "maxDelayMs": {{Day}} } },
          "queues": { "orders": { "backoff": { "baseDelayMs": 1000 } } } }
        """);

        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/config", StringComparison.Ordinal));
        Assert.Contains("PATCH /tenants/ten_abc/policy", api.Writes);
    }

    [Fact]
    public async Task A_key_that_cannot_read_the_config_leaves_the_ceiling_to_Queuey()
    {
        // Sjekken er en forhåndsvisning av serverens regel. Kan den ikke lese det som gjelder nå, avgjør serveren,
        // som den alltid gjør; en apply skal ikke feile på at forhåndsvisningen manglet en tillatelse.
        var api = new Api { Rows = OrdersExists, ConfigStatus = HttpStatusCode.Forbidden };

        QueueSyncResult result = await Apply(api, $$"""{ "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}} } } } }""");

        Assert.True(result.AllSucceeded);
        Assert.Contains("GET /queues/que_orders/config", api.Paths);
        Assert.Contains("PATCH /queues/que_orders/policy", api.Writes);
    }
}
