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

        /// <summary>Om køen eier noe av policyen: <c>inherited.behavior</c> er det motsatte. Null sender ingen flagg.</summary>
        public bool? QueueOwnsPolicy { get; init; } = true;

        public HttpStatusCode ConfigStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>Et API fra før 2026-09-23 sender ikke backoff i config.</summary>
        public bool ConfigWithoutBackoff { get; init; }

        /// <summary>Det køen lagrer selv i <c>overrides.retry.backoff.baseDelayMs</c> (GET /queues/{que}); null arver.</summary>
        public int? StoredBaseDelayMs { get; init; }

        public HttpStatusCode StoredStatus { get; init; } = HttpStatusCode.OK;

        public StubHttpMessageHandler Stub { get; }

        public Api() => Stub = new StubHttpMessageHandler((_, req, body) =>
        {
            string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";
            if (key.EndsWith("/config", StringComparison.Ordinal) && ConfigStatus != HttpStatusCode.OK)
                return StubHttpMessageHandler.Json(ConfigStatus, new { error = new { code = "refused", message = "Not here." } });

            object workspacePolicy = ConfigWithoutBackoff ? new { ordering = "fifo" } : new { ordering = "fifo", backoff = WorkspaceBackoff };
            switch (key)
            {
                case "GET /tenants/ten_abc/queues":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, Rows);
                case "GET /tenants/ten_abc/config":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy = workspacePolicy });
                case "GET /queues/que_orders/config":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        policy = ConfigWithoutBackoff ? new { ordering = "fifo" } : (object)new { ordering = "fifo", backoff = QueueBackoff },
                        inherited = QueueOwnsPolicy is { } owns ? new { destination = true, auth = true, signing = true, rateLimit = true, behavior = !owns } : null,
                        tenantBaseline = new { policy = workspacePolicy },
                    });
                case "GET /queues/que_orders" when StoredStatus != HttpStatusCode.OK:
                    return StubHttpMessageHandler.Json(StoredStatus, new { error = new { code = "refused", message = "Not here." } });
                case "GET /queues/que_orders":
                    // De rå overstyringene, med enumene som tall slik API-et sender dem.
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        publicId = "que_orders",
                        displayName = "orders",
                        overrides = new
                        {
                            retention = new { days = 30 },
                            retry = new { backoff = StoredBaseDelayMs is { } own ? (object)new { baseDelayMs = own, jitter = 0 } : new { jitter = 0 } },
                        },
                        mode = 3,
                    });
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
        // Køen har to timer selv, satt før taket, og pull skriver backoff hel inn i fila. En uendret apply av den
        // fila endrer ingenting, og serveren godtar den (Queuey#391), så klienten gjør det også.
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

    [Fact]
    public async Task An_unchanged_longer_wait_on_the_workspace_is_applied()
    {
        // Workspacet fikk to timer før taket. En pull skriver dem i workspace-delen, og en uendret apply godtas.
        var api = new Api { WorkspaceBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = Day, jitter = "full" } };

        QueueSyncResult result = await Apply(api, $$"""{ "workspace": { "backoff": { "baseDelayMs": {{2 * Hour}} } }, "queues": {} }""");

        Assert.True(result.AllSucceeded);
        Assert.Contains("PATCH /tenants/ten_abc/policy", api.Writes);
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
            Assert.Contains($"would change it from the {fileWorkspace ?? workspaceNow} the queue inherits from the workspace", ex.Message);
            Assert.Empty(api.Writes);
        }
    }

    [Fact]
    public async Task A_queue_that_inherits_the_wait_is_compared_with_the_workspace_this_apply_leaves()
    {
        // Review 2026-10-05: workspacet har to timer fra før taket, og køen eier ingen policy, så den arver dem. Fila
        // senker workspacet til en halvtime og gir køen to timer. Serveren sjekker køen etter at workspacet er skrevet:
        // før er en halvtime, etter to timer, og køen avvises. Før ble det oppdaget først der, midt i kjøringen.
        var api = new Api
        {
            Rows = OrdersExists,
            QueueOwnsPolicy = false,
            WorkspaceBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = Day, jitter = "full" },
            QueueBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = Day, jitter = "full" },
        };

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => Apply(api, $$"""
        { "workspace": { "backoff": { "baseDelayMs": 1800000 } },
          "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}} } } } }
        """));

        Assert.Contains($"queues.orders.backoff.baseDelayMs is {2 * Hour}, above the {Hour} (one hour) a first wait may be, "
                        + "and would change it from the 1800000 the queue inherits from the workspace", ex.Message);
        Assert.Empty(api.Writes);
    }

    [Theory]
    [InlineData(2 * Hour, true)]    // det køen har: uendret, selv om fila endrer workspacet
    [InlineData(3 * Hour, false)]   // en annen lang ventetid: endrer køens egen
    public async Task A_queue_that_owns_its_wait_is_compared_with_its_own_when_the_workspace_changes(int declared, bool applies)
    {
        // Ulik workspacets er ventetiden køens egen, for en arvet verdi er workspacets. Det fila gjør med workspacet,
        // endrer den ikke, så den er det en deklarert ventetid erstatter.
        var api = new Api
        {
            Rows = OrdersExists,
            WorkspaceBackoff = new { baseDelayMs = 1_000, maxDelayMs = Day, jitter = "full" },
            QueueBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = Day, jitter = "full" },
        };

        Task<QueueSyncResult> apply = Apply(api, $$"""
        { "workspace": { "backoff": { "baseDelayMs": 2000 } },
          "queues": { "orders": { "backoff": { "baseDelayMs": {{declared}} } } } }
        """);

        if (applies)
        {
            Assert.True((await apply).AllSucceeded);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => apply);
            Assert.Contains($"queues.orders.backoff.baseDelayMs is {declared}, above the {Hour} (one hour) a first wait may be, and would change it from {2 * Hour}", ex.Message);
            Assert.Empty(api.Writes);
        }
    }

    [Fact]
    public async Task A_wait_unlike_both_candidates_is_refused_without_reading_what_the_queue_stores()
    {
        // Re-review 2026-10-05: køen arver ventetiden, men eier retentionDays, så inherited.behavior er false, og
        // ventetiden er lik workspacets. Fila setter workspacet til en halvtime og køen til to timer. Serveren har
        // «før» lik ett sekund hvis køen har ventetiden selv, eller en halvtime hvis den arver den: to timer er ulik
        // begge, så serveren avviser uansett, og det gjør klienten også, før noe er skrevet.
        var api = new Api
        {
            Rows = OrdersExists,
            QueueOwnsPolicy = true,
            WorkspaceBackoff = new { baseDelayMs = 1_000, maxDelayMs = Day, jitter = "full" },
            QueueBackoff = new { baseDelayMs = 1_000, maxDelayMs = Day, jitter = "full" },
        };

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => Apply(api, $$"""
        { "workspace": { "backoff": { "baseDelayMs": 1800000 } },
          "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}} } } } }
        """));

        Assert.Contains($"queues.orders.backoff.baseDelayMs is {2 * Hour}, above the {Hour} (one hour) a first wait may be, "
                        + "and would change the wait whether the queue has its own 1000 or inherits the workspace's 1800000", ex.Message);
        Assert.Empty(api.Writes);
        Assert.DoesNotContain("GET /queues/que_orders", api.Paths);
    }

    // Saken fra review 2026-10-05 der config-lesingen ikke kan avgjøre: køen eier en annen del av policyen
    // (inherited.behavior er false), ventetiden er lik workspacets to timer fra før taket, og fila senker workspacet
    // til en halvtime mens køen beholder to timer. Har køen ventetiden selv, er den uendret og godtas; arver den,
    // avviser serveren køen etter at workspacet er skrevet. Det køen lagrer selv (GET /queues/{que}), avgjør.
    [Theory]
    [InlineData(2 * Hour, HttpStatusCode.OK, true)]          // køen lagrer to timer selv: uendret
    [InlineData(null, HttpStatusCode.OK, false)]             // køen lagrer ingen: den arver, og får en halvtime
    [InlineData(null, HttpStatusCode.Forbidden, true)]       // lesingen nektes: serveren avgjør
    public async Task What_the_queue_stores_decides_whether_it_owns_the_wait(int? stored, HttpStatusCode storedStatus, bool applies)
    {
        var api = new Api
        {
            Rows = OrdersExists,
            QueueOwnsPolicy = true,
            WorkspaceBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = Day, jitter = "full" },
            QueueBackoff = new { baseDelayMs = 2 * Hour, maxDelayMs = Day, jitter = "none" },
            StoredBaseDelayMs = stored,
            StoredStatus = storedStatus,
        };

        Task<QueueSyncResult> apply = Apply(api, $$"""
        { "workspace": { "backoff": { "baseDelayMs": 1800000 } },
          "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}}, "maxDelayMs": {{Day}}, "jitter": "none" } } } }
        """);

        if (applies)
        {
            Assert.True((await apply).AllSucceeded);
            Assert.True(api.Writes.IndexOf("PATCH /tenants/ten_abc/policy") < api.Writes.IndexOf("PATCH /queues/que_orders/policy"));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => apply);
            Assert.Contains($"queues.orders.backoff.baseDelayMs is {2 * Hour}, above the {Hour} (one hour) a first wait may be, "
                            + "and would change it from the 1800000 the queue inherits from the workspace", ex.Message);
            Assert.Empty(api.Writes);
        }

        Assert.Single(api.Paths, p => p == "GET /queues/que_orders");
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

    // Sjekken er en forhåndsvisning av serverens regel. Kan den ikke lese det som gjelder nå, avgjør serveren, som den
    // alltid gjør: en apply skal ikke feile fordi forhåndsvisningen manglet en tillatelse, en kø eller et felt.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, false)]   // nøkkelen kan ikke lese config
    [InlineData(HttpStatusCode.NotFound, false)]    // køen er borte siden den ble listet
    [InlineData(HttpStatusCode.OK, true)]           // et API fra før 2026-09-23, uten backoff i config
    public async Task What_the_preflight_cannot_read_is_left_to_Queuey(HttpStatusCode configStatus, bool configWithoutBackoff)
    {
        var api = new Api { Rows = OrdersExists, ConfigStatus = configStatus, ConfigWithoutBackoff = configWithoutBackoff };

        QueueSyncResult result = await Apply(api, $$"""{ "queues": { "orders": { "backoff": { "baseDelayMs": {{2 * Hour}} } } } }""");

        Assert.True(result.AllSucceeded);
        Assert.Contains("GET /queues/que_orders/config", api.Paths);
        Assert.Contains("PATCH /queues/que_orders/policy", api.Writes);
    }
}
