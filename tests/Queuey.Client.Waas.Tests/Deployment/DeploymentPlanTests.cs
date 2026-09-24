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
/// <c>queuey apply --plan</c> (2026-09-23): hver skriving apply ville sendt, sendes som dry-run, og
/// svaret er det Queuey ville godtatt og endret. En server fra før dry-run ignorerer parameteren og
/// skriver, så planen beviser først at serveren planlegger, med en probe som ikke endrer noe.
/// </summary>
public class DeploymentPlanTests
{
    private static object Plan(string target, params (string Path, object? From, object? To)[] changes) => new
    {
        dryRun = true,
        target,
        changes = changes.Select(c => new { path = c.Path, from = c.From, to = c.To }),
        notes = Array.Empty<string>(),
    };

    private sealed class Server
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
        public List<object> Queues { get; } = new();
        public StubHttpMessageHandler Stub { get; }

        public Server() => Stub = new StubHttpMessageHandler(req =>
        {
            string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";
            if (Routes.TryGetValue(key, out var handler))
                return handler(req);
            if (key.StartsWith("GET /tenants/", StringComparison.Ordinal) && key.EndsWith("/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Queues);
            // Proben og alt annet som ikke er satt opp: en tom plan.
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, Plan("workspace ten_abc"));
        });

        public IEnumerable<HttpRequestMessage> Writes => Stub.Requests.Where(r => r.Method != HttpMethod.Get);
    }

    private static Task<DeploymentPlan> PlanAsync(Server server, string json)
        => WaasTestHost.Build(apiStub: server.Stub).PlanDeploymentAsync(DeploymentFile.Parse(json));

    [Fact]
    public async Task A_server_that_does_not_plan_is_found_out_before_anything_else_is_sent()
    {
        var server = new Server();
        server.Routes["PATCH /tenants/ten_abc/policy"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var ex = await Assert.ThrowsAsync<QueueyException>(() => PlanAsync(server, """
        { "tenant": "ten_abc", "workspace": { "retentionDays": 30 }, "queues": { "orders": { "maxAttempts": 5 } } }
        """));

        Assert.Equal("dry_run_unsupported", ex.ErrorCode);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Contains("--check", ex.SuggestedAction);

        // Bare proben ble sendt, og den var en tom patch.
        HttpRequestMessage probe = Assert.Single(server.Stub.Requests);
        Assert.Equal("?dryRun=true", probe.RequestUri!.Query);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(server.Stub.Bodies[0]!));
    }

    [Fact]
    public async Task Every_write_goes_as_a_dry_run_and_the_changes_come_back()
    {
        var server = new Server();
        server.Queues.Add(new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true });
        int policyCalls = 0;
        server.Routes["PATCH /tenants/ten_abc/policy"] = _ => ++policyCalls == 1
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, Plan("workspace ten_abc"))   // proben
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, Plan("workspace ten_abc", ("policy.retentionDays", 7, 30)));
        server.Routes["PUT /queues"] = req => System.Text.Encoding.UTF8.GetString(req.Content!.ReadAsByteArrayAsync().Result).Contains("\"orders\"")
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true })
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = (string?)null, displayName = "invoices", created = true, hasDeliveryTarget = false });
        server.Routes["PATCH /queues/que_orders/policy"] = _ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Plan("queue que_orders", ("policy.maxAttempts", 8, 5)));

        DeploymentPlan plan = await PlanAsync(server, """
        { "tenant": "ten_abc",
          "workspace": { "retentionDays": 30 },
          "queues": { "orders": { "maxAttempts": 5 }, "invoices": { "retentionDays": 3 } } }
        """);

        Assert.True(plan.WouldSucceed);
        Assert.Equal(3, plan.ChangeCount);   // to verdier, og én kø som ville blitt opprettet
        Assert.All(server.Writes, r => Assert.Equal("?dryRun=true", r.RequestUri!.Query));

        DeploymentPlanStep workspace = plan.Steps.Single(s => s.Target == "workspace" && s.Aspect == "policy");
        Assert.Equal("policy.retentionDays: 7 → 30", Assert.Single(workspace.Changes).ToString());

        DeploymentPlanStep orders = plan.Steps.Single(s => s.Target == "queues.orders" && s.Aspect == "policy");
        Assert.Equal("5", Assert.Single(orders.Changes).To);

        DeploymentPlanStep invoices = plan.Steps.Single(s => s.Target == "queues.invoices");
        Assert.True(invoices.Creates);
        Assert.Contains(invoices.Notes, n => n.Contains("log events until it has a destination"));
        Assert.Contains(invoices.Notes, n => n.Contains("local validation"));
        Assert.DoesNotContain(server.Writes, r => r.RequestUri!.AbsolutePath.StartsWith("/queues/que_invoices", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_is_a_step_with_its_code_and_action_and_the_plan_goes_on()
    {
        var server = new Server();
        server.Queues.Add(new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true });
        server.Routes["PUT /queues"] = _ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true });
        server.Routes["PATCH /queues/que_orders/policy"] = _ => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            new { error = new { code = "retention_cap_exceeded", message = "Your plan keeps events for at most 7 days.", action = "Declare 7 or fewer." } });
        server.Routes["PATCH /queues/que_orders/ingress"] = _ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Plan("queue que_orders", ("ingress.successStatusCode", 202, 200)));

        DeploymentPlan plan = await PlanAsync(server, """
        { "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 3650, "ingress": { "successStatusCode": 200 } } } }
        """);

        Assert.False(plan.WouldSucceed);
        QueueyException error = plan.Steps.Single(s => s.Aspect == "policy").Error!;
        Assert.Equal("retention_cap_exceeded", error.ErrorCode);
        Assert.Equal("Declare 7 or fewer.", error.SuggestedAction);
        Assert.Single(plan.Steps.Single(s => s.Aspect == "ingress").Changes);
    }

    /// <summary>Svar på en 2xx som ikke er en plan. En server som ignorerer dryRun, kan ha skrevet.</summary>
    public static TheoryData<string> NotAPlan => new() { "204", "empty", "not json", "no dryRun" };

    private static HttpResponseMessage Answer(string kind) => kind switch
    {
        "204" => new HttpResponseMessage(HttpStatusCode.NoContent),
        "empty" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) },
        "not json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ok</html>", System.Text.Encoding.UTF8, "text/html") },
        _ => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { target = "queue que_orders", changes = Array.Empty<object>() }),
    };

    [Theory]
    [MemberData(nameof(NotAPlan))]
    public async Task A_dry_run_answered_with_anything_but_a_plan_stops_the_plan_and_names_the_write(string kind)
    {
        // Review 2026-09-24: en tom 2xx, en 204 eller tekst som ikke er JSON etter proben krasjet CLI-en
        // med JsonException og stacktrace. Nå er det DryRunIgnoredException, som sier hvilken skriving.
        var server = new Server();
        server.Queues.Add(new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true });
        server.Routes["PUT /queues"] = req => System.Text.Encoding.UTF8.GetString(req.Content!.ReadAsByteArrayAsync().Result).Contains("\"orders\"")
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true })
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = (string?)null, displayName = "invoices", created = true, hasDeliveryTarget = false });
        server.Routes["PATCH /queues/que_orders/policy"] = _ => Answer(kind);

        DryRunIgnoredException ex = await Assert.ThrowsAsync<DryRunIgnoredException>(() => PlanAsync(server, """
        { "tenant": "ten_abc", "queues": { "orders": { "maxAttempts": 5 }, "invoices": {} } }
        """));

        Assert.Equal("queues.orders", ex.Target);
        Assert.Equal("policy", ex.Aspect);
        Assert.Contains("queues.orders · policy", ex.Message);
        Assert.Equal("dry_run_ignored", ex.ErrorCode);

        // Ingen flere skrivinger etter svaret som ikke var en plan: invoices ble aldri spurt om.
        Assert.Equal("/queues/que_orders/policy", server.Writes.Last().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_declared_mode_on_an_existing_queue_is_planned_and_deliver_needs_a_destination()
    {
        var server = new Server();
        server.Queues.Add(new { publicId = "que_orders", displayName = "orders", mode = "LogOnly", hasDeliveryTarget = true });
        server.Queues.Add(new { publicId = "que_audit", displayName = "audit", mode = "LogOnly", hasDeliveryTarget = false });
        server.Routes["PUT /queues"] = req =>
        {
            string body = System.Text.Encoding.UTF8.GetString(req.Content!.ReadAsByteArrayAsync().Result);
            string name = body.Contains("\"orders\"") ? "orders" : "audit";
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_" + name, displayName = name, created = false, hasDeliveryTarget = name == "orders" });
        };
        server.Routes["PATCH /queues/que_orders/mode-change"] = _ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Plan("queue que_orders", ("mode", "LogOnly", "Deliver")));

        DeploymentPlan plan = await PlanAsync(server, """
        { "tenant": "ten_abc", "queues": { "orders": { "mode": "deliver" }, "audit": { "mode": "deliver" } } }
        """);

        DeploymentPlanStep orders = plan.Steps.Single(s => s.Target == "queues.orders" && s.Aspect == "mode");
        Assert.Equal("mode: LogOnly → Deliver", Assert.Single(orders.Changes).ToString());
        int modeCall = server.Stub.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/queues/que_orders/mode-change");
        Assert.Equal(3, JsonDocument.Parse(server.Stub.Bodies[modeCall]!).RootElement.GetProperty("mode").GetInt32());

        DeploymentPlanStep audit = plan.Steps.Single(s => s.Target == "queues.audit" && s.Aspect == "mode");
        Assert.Equal("deliver_without_destination", audit.Error!.ErrorCode);
        Assert.False(plan.WouldSucceed);
    }
}
