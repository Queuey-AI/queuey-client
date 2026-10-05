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
        public object[] Credentials { get; init; } = Array.Empty<object>();

        /// <summary>Svar som overstyrer standarden, nøklet på «METODE sti».</summary>
        public Dictionary<string, Func<HttpResponseMessage>> Answers { get; } = new(StringComparer.Ordinal);

        public StubHttpMessageHandler Stub { get; }

        public Api() => Stub = new StubHttpMessageHandler((_, req, body) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (Answers.TryGetValue($"{req.Method.Method} {path}", out Func<HttpResponseMessage>? answer))
                return answer();
            if (req.Method == HttpMethod.Get && path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Credentials);
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

        public List<string> Writes => Paths.Where(p => !p.StartsWith("GET ", StringComparison.Ordinal)).ToList();

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

    // ── en apply som feiler halvveis (review 2026-09-24) ─────────────────────

    [Fact]
    public async Task A_missing_credential_name_on_a_queue_fails_before_anything_is_sent()
    {
        // Før ble navnet slått opp etter PUT: køen ble opprettet, feilet og lå igjen i logOnly.
        var api = new Api { Credentials = new object[] { new { publicId = "cred_1", name = "partner-key", type = "ApiKeyHeader" } } };

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(() => Apply(api, """
        { "workspace": { "delivery": { "baseUrl": "https://hooks.example.com", "credentialRef": "partner-key" } },
          "queues": {
            "orders":   { "delivery": { "url": "/orders" } },
            "invoices": { "delivery": { "url": "/invoices", "credentialRef": "invoice-key",
                                        "signing": { "enabled": true, "credentialRef": "invoice-signing" } } } } }
        """));

        Assert.Empty(api.Writes);
        Assert.Contains("'invoice-key', 'invoice-signing'", ex.Message);
        Assert.Contains("queues.invoices.delivery.credentialRef", ex.Message);
        Assert.Contains("queues.invoices.delivery.signing.credentialRef", ex.Message);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Contains("Available: partner-key", ex.Message);
    }

    [Fact]
    public async Task Every_credential_name_is_resolved_before_the_first_write_and_reaches_its_queue()
    {
        var api = new Api { Credentials = new object[] { new { publicId = "cred_orders", name = "orders-key", type = "ApiKeyHeader" } } };

        await Apply(api, """{ "queues": { "orders": { "delivery": { "url": "/orders", "credentialRef": "orders-key" } } } }""");

        int credentials = api.Paths.IndexOf("GET /tenants/ten_abc/credentials");
        int firstWrite = api.Paths.FindIndex(p => !p.StartsWith("GET ", StringComparison.Ordinal));
        Assert.True(credentials >= 0 && credentials < firstWrite, string.Join(", ", api.Paths));
        Assert.Equal("cred_orders", api.Body("PATCH /queues/que_orders/delivery").GetProperty("credentialRef").GetString());
    }

    [Theory]
    [InlineData(null, "will not start delivering by itself")]
    [InlineData("deliver", "the next apply sets it once the error is fixed")]
    [InlineData("logOnly", null)]
    public async Task A_queue_created_before_its_apply_failed_is_reported_as_created_with_the_mode_it_got(string? mode, string? warning)
    {
        // Før mistet feilresultatet id og «opprettet», og neste apply lot en eksisterende kø beholde
        // logOnly og ga exit 0. Nå sier resultatet at køen finnes, og i hvilken modus.
        var api = new Api { Created = true, HasTarget = true };
        api.Answers["PATCH /queues/que_orders/policy"] = () => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            new { error = new { code = "retention_cap_exceeded", message = "Your plan keeps events for at most 7 days." } });
        string declared = mode is null ? "" : $"\"mode\": \"{mode}\", ";

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(
            () => Apply(api, $$"""{ "queues": { "orders": { {{declared}}"retentionDays": 3650 } } }"""));

        QueueApplyResult orders = ex.Queues!.Applied.Single();
        Assert.False(orders.Succeeded);
        Assert.True(orders.Created);
        Assert.Equal("que_orders", orders.PublicId);
        Assert.Equal("logOnly", orders.Mode);
        Assert.Equal("retention_cap_exceeded", orders.Error!.ErrorCode);
        Assert.Equal(1, ex.Queues.Created);
        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/mode-change", StringComparison.Ordinal));

        if (warning is null)
            Assert.Empty(orders.Warnings);
        else
            Assert.Contains(warning, Assert.Single(orders.Warnings));
    }

    [Fact]
    public async Task A_queue_that_existed_before_a_failed_apply_is_not_reported_as_created()
    {
        var api = new Api { Created = false, Rows = new[] { Row("orders", "Deliver") } };
        api.Answers["PATCH /queues/que_orders/policy"] = () => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            new { error = new { code = "invalid_policy", message = "no" } });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(
            () => Apply(api, """{ "queues": { "orders": { "retentionDays": 3 } } }"""));

        QueueApplyResult orders = ex.Queues!.Applied.Single();
        Assert.False(orders.Created);
        Assert.Equal("que_orders", orders.PublicId);
        Assert.Null(orders.Mode);
        Assert.Empty(orders.Warnings);
    }

    // ── backoff og filter ────────────────────────────────────────────────────

    [Fact]
    public async Task Backoff_and_filter_reach_the_queue_policy_patch()
    {
        var api = new Api();

        await Apply(api, """
        { "queues": { "orders": {
            "backoff": { "baseDelayMs": 500, "maxDelayMs": 60000, "jitter": "full" },
            "filter": { "match": "any", "conditions": [
              { "field": "type", "op": "eq", "value": "order.created" },
              { "field": "priority", "op": "exists" } ] } } } }
        """);

        JsonElement policy = api.Body("PATCH /queues/que_orders/policy");
        Assert.Equal(500, policy.GetProperty("backoff").GetProperty("baseDelayMs").GetInt32());
        Assert.Equal("full", policy.GetProperty("backoff").GetProperty("jitter").GetString());

        JsonElement filter = policy.GetProperty("filter");
        Assert.Equal("any", filter.GetProperty("match").GetString());
        JsonElement exists = filter.GetProperty("conditions")[1];
        Assert.Equal("exists", exists.GetProperty("op").GetString());
        Assert.False(exists.TryGetProperty("value", out _));   // udeklarert er fraværende, ikke null

        // Ikke deklarert: ikke med i patchen, så verdien arves videre. Forsøkene har patchen ikke noe felt for.
        Assert.False(policy.TryGetProperty("ordering", out _));
        Assert.Equal(new[] { "backoff", "filter" }, policy.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_filter_without_conditions_is_sent_so_the_old_filter_is_removed()
    {
        var api = new Api();

        await Apply(api, """{ "queues": { "orders": { "filter": { "conditions": [] } } } }""");

        Assert.Equal(0, api.Body("PATCH /queues/que_orders/policy").GetProperty("filter").GetProperty("conditions").GetArrayLength());
    }

    [Fact]
    public async Task Workspace_backoff_reaches_the_workspace_policy_patch()
    {
        var api = new Api();

        await Apply(api, """{ "tenant": "ten_abc", "workspace": { "backoff": { "baseDelayMs": 1000 } }, "queues": {} }""");

        JsonElement policy = api.Body("PATCH /tenants/ten_abc/policy");
        Assert.Equal(1000, policy.GetProperty("backoff").GetProperty("baseDelayMs").GetInt32());
        Assert.False(policy.GetProperty("backoff").TryGetProperty("jitter", out _));
        Assert.Equal(new[] { "backoff" }, policy.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData("""{ "backoff": { "jitter": "some" } }""", "Jitter must be one of none, full")]
    [InlineData("""{ "backoff": { "baseDelayMs": 2000, "maxDelayMs": 1000 } }""", "cannot be below BaseDelayMs")]
    [InlineData("""{ "filter": { "match": "most", "conditions": [] } }""", "Match must be one of all, any")]
    [InlineData("""{ "filter": { "conditions": [ { "field": "type", "op": "like", "value": "x" } ] } }""", "must be one of eq, ne")]
    [InlineData("""{ "filter": { "conditions": [ { "field": "type", "op": "eq" } ] } }""", "needs a value")]
    [InlineData("""{ "backoff": { "baseDelayMs": 0 } }""", "Backoff.BaseDelayMs must be above 0; got 0.")]
    [InlineData("""{ "backoff": { "maxDelayMs": 0 } }""", "Backoff.MaxDelayMs must be above 0; got 0.")]
    public void A_backoff_or_filter_mistake_fails_locally_and_names_what_works(string queue, string expected)
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "queues": { "orders": {{queue}} } }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains(expected, ex.Message);
    }

    private const string PlainNumber = "Write it like 1.5: a point for decimals, and no thousands separators, currency or parentheses.";

    // Det backenden avviser i et filter siden Queuey#391 (2026-10-04), avvist her før apply skriver noe. Før gikk
    // "1,5" gjennom og ble sammenlignet som 15, "exists" med en verdi leverte events med feltet, og "amount " traff aldri.
    [Theory]
    [InlineData("""{ "field": "amount", "op": "gt", "value": "1,5" }""", "'1,5' is not a number. " + PlainNumber)]
    [InlineData("""{ "field": "amount", "op": "gte", "value": "1,000" }""", "'1,000' is not a number.")]
    [InlineData("""{ "field": "amount", "op": "lt", "value": "$5" }""", "'$5' is not a number.")]
    [InlineData("""{ "field": "amount", "op": "lte", "value": "(5)" }""", "'(5)' is not a number.")]
    [InlineData("""{ "field": "amount", "op": "gt", "value": "5-" }""", "'5-' is not a number.")]
    [InlineData("""{ "field": "amount", "op": "gt", "value": "NaN" }""", "'NaN' is not a number.")]
    [InlineData("""{ "field": "amount", "op": "gt", "value": "1e309" }""", "Filter condition 'amount gt' compares numbers, and '1e309' is out of range.")]
    [InlineData("""{ "field": "priority", "op": "exists", "value": "false" }""", "Filter condition 'priority exists' takes no value: it matches every event that has the field, whatever the value. Leave the value out.")]
    [InlineData("""{ "field": "priority", "op": "exists", "value": "" }""", "takes no value")]
    [InlineData("""{ "field": "amount ", "op": "eq", "value": "5" }""", "Filter field 'amount ' has whitespace around it, so it never matches")]
    [InlineData("""{ "field": " type", "op": "exists" }""", "Write it as 'type'.")]
    public void A_filter_Queuey_would_refuse_is_refused_before_anything_is_sent(string condition, string expected)
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "queues": { "orders": { "filter": { "conditions": [ {{condition}} ] } } } }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("gt", "1.5")]
    [InlineData("lte", "-2")]
    [InlineData("gte", "1e3")]
    [InlineData("lt", "+.5")]
    public void A_plain_number_passes_the_comparison(string op, string value)
    {
        // Det NumberStyles.Float godtar, godtar backenden også, og workeren leser det som samme tall.
        DeploymentFile file = DeploymentFile.Parse($$"""{ "queues": { "orders": { "filter": { "conditions": [ { "field": "amount", "op": "{{op}}", "value": "{{value}}" } ] } } } }""");

        Assert.Single(file.Resolve());
    }

    [Fact]
    public void A_comma_is_only_refused_where_a_number_is_compared()
    {
        // eq og contains sammenligner tekst, så "1,5" er en verdi som alle andre der.
        DeploymentFile file = DeploymentFile.Parse("""
        { "queues": { "orders": { "filter": { "match": "any", "conditions": [
            { "field": "amount", "op": "eq", "value": "1,5" }, { "field": "note", "op": "contains", "value": "(5)" } ] } } } }
        """);

        Assert.Single(file.Resolve());
    }

    [Fact]
    public void A_workspace_backoff_mistake_fails_locally_too()
    {
        DeploymentFile file = DeploymentFile.Parse("""{ "workspace": { "backoff": { "baseDelayMs": -1 } }, "queues": {} }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Contains("workspace", ex.Message);
        Assert.Contains("Backoff.BaseDelayMs must be above 0; got -1.", ex.Message);
    }

    [Fact]
    public void Numbers_in_a_refusal_are_written_the_same_in_every_culture()
    {
        // Som i backenden (re-review 2026-10-05): med nb-NO ble -1 skrevet «−1» med U+2212, og en melding var ulik fra
        // maskin til maskin.
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
        try
        {
            DeploymentFile file = DeploymentFile.Parse("""{ "queues": { "orders": { "retentionDays": -5, "backoff": { "baseDelayMs": -1 } } } }""");

            var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
            Assert.Contains("RetentionDays cannot be negative; got -5.", ex.Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    private const string NeedsConditions = "A filter needs its conditions. To remove the filter, write \"conditions\": [].";

    // En manglende liste ble lest som tom, og en tom liste fjerner filteret (review 2026-10-05): "filter": {"match":
    // "any"} sendte "conditions": [], og køen leverte hvert event. null og [null] krasjet CLI-en.
    [Theory]
    [InlineData("""{ }""", NeedsConditions)]
    [InlineData("""{ "match": "any" }""", NeedsConditions)]
    [InlineData("""{ "match": "all", "conditions": null }""", NeedsConditions)]
    [InlineData("""{ "conditions": [ null ] }""", "A filter condition cannot be null.")]
    [InlineData("""{ "conditions": [ { "field": "type", "op": "eq", "value": "order.created" }, null ] }""", "A filter condition cannot be null.")]
    public async Task A_filter_without_its_conditions_is_refused_and_nothing_is_sent(string filter, string expected)
    {
        var api = new Api();

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => Apply(api, $$"""{ "queues": { "orders": { "filter": {{filter}} } } }"""));

        Assert.Contains(expected, ex.Message);
        Assert.Empty(api.Paths);
    }

    [Fact]
    public void A_filter_declared_in_code_needs_its_conditions_too()
    {
        // Samme regel kode-først: et filter uten liste ville fjernet køens filter ved neste sync.
        var ex = Assert.Throws<QueueyConfigurationException>(() => QueueDefinitionFactory.FromName("orders", new QueueOptions
        {
            Policy = { Filter = new DeliveryFilter { Match = "any" } },
        }));

        Assert.Contains(NeedsConditions, ex.Message);
    }

    [Fact]
    public void The_schema_key_is_accepted_and_ignored()
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "$schema": "{{DeploymentFile.SchemaUrl}}", "queues": { "orders": {} } }""");

        Assert.Equal(DeploymentFile.SchemaUrl, file.Schema);
        Assert.Single(file.Resolve());
    }
}
