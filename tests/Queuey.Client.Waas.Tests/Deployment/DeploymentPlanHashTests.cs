using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Planens id og hash (Queuey F2.3, 2026-10-06): hashen dekker alt apply ville endret og tilstanden på serveren det hviler
/// på, og ingenting som ikke endrer hva apply gjør. Den kanoniske formen er den samme som Queuey sin, låst med de samme
/// vektorene (CanonicalJsonTests i Queuey), fordi F3.11 regner hashen på serveren når planen blir et godkjenningsobjekt.
/// </summary>
public class DeploymentPlanHashTests
{
    // ── den kanoniske formen, delt med Queuey ─────────────────────────────

    public static readonly TheoryData<string, string, string> Vectors = new()
    {
        {
            """{"b":2,"a":[3,1,{"z":null,"y":"x"}],"c":"q\"u\\o\nte","d":1.50,"e":-0,"f":true,"g":null}""",
            """{"a":[1,3,{"y":"x"}],"b":2,"c":"q\"u\\o\nte","d":1.5,"e":0,"f":true}""",
            "sha256:dc5cf7fedfcfeb6fddde1a58ef27ad58ce23f6e02af16d86238520bb1164cd1b"
        },
        {
            "{\"ø\":\"æ\\u0001\",\"A\":[]}",
            "{\"A\":[],\"ø\":\"æ\\u0001\"}",
            "sha256:103ef8c5b081ac36bc2af655f8e0fe878d6771f4468ed33e3420d231c4063f7e"
        },
        {
            """{"x":[{"b":1,"a":2},{"a":1}]}""",
            """{"x":[{"a":1},{"a":2,"b":1}]}""",
            "sha256:8a809cb89b4ca5f54646f907227015a8f06c72c7cd2cd4854ed2932ada74682f"
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void The_shared_vectors_give_the_same_text_and_hash_as_in_queuey(string input, string canonical, string hash)
    {
        using JsonDocument doc = JsonDocument.Parse(input);

        Assert.Equal(canonical, CanonicalJson.Write(doc.RootElement));
        Assert.Equal(hash, CanonicalJson.Hash(doc.RootElement));
    }

    // ── planen ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A server that plans: both queues exist, each policy dry run answers with a change and the state it started from,
    /// and the local-forward dry run answers with the kind it would set.
    /// </summary>
    private sealed class Server
    {
        public string OrdersState { get; set; } = "sha256:" + new string('a', 64);
        public string OrdersNote { get; set; } = "The workspace has 2 queues.";
        public int OrdersRetentionTo { get; set; } = 5;
        public object[]? OrdersFilterFrom { get; set; }

        public StubHttpMessageHandler Stub { get; }

        public Server() => Stub = new StubHttpMessageHandler((_, req, body) =>
        {
            string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";
            if (key == "PUT /queues")
            {
                string name = JsonDocument.Parse(body!).RootElement.GetProperty("displayName").GetString()!;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_" + name, displayName = name, created = false, hasDeliveryTarget = true });
            }

            return key switch
            {
                "GET /tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new object[]
                {
                    new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true },
                    new { publicId = "que_refunds", displayName = "refunds", mode = "Deliver", hasDeliveryTarget = true },
                }),
                "GET /tenants/ten_abc/credentials" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
                "PATCH /queues/que_orders/policy" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    dryRun = true, target = "queue que_orders", stateHash = OrdersState, notes = new[] { OrdersNote },
                    changes = OrdersFilterFrom is null
                        ? new object[] { new { path = "policy.retentionDays", from = 7, to = OrdersRetentionTo } }
                        : new object[]
                        {
                            new { path = "policy.retentionDays", from = 7, to = OrdersRetentionTo },
                            new { path = "policy.filter.conditions", from = OrdersFilterFrom, to = Enumerable.Reverse(OrdersFilterFrom).ToArray() },
                        },
                }),
                "PATCH /queues/que_refunds/policy" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    dryRun = true, target = "queue que_refunds", stateHash = "sha256:" + new string('b', 64), notes = Array.Empty<string>(),
                    changes = new object[] { new { path = "policy.retentionDays", from = 7, to = 3 } },
                }),
                "PATCH /queues/que_refunds/local-forward" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    dryRun = true, target = "queue que_refunds", stateHash = "sha256:" + new string('b', 64),
                    notes = new[] { "Its events go to a connected queuey listen session instead of its endpoint." },
                    changes = new object[] { new { path = "delivery.kind", from = "Http", to = "LocalForward" } },
                }),
                _ => throw new InvalidOperationException("unexpected " + key),
            };
        });
    }

    private const string File = """
    { "tenant": "ten_abc", "queues": {
        "orders":  { "retentionDays": 5 },
        "refunds": { "retentionDays": 3, "delivery": { "kind": "localForward" } } } }
    """;

    // Samme fil med køene i omvendt rekkefølge, andre mellomrom og andre store og små bokstaver i en verdi som normaliseres.
    private const string SameFileReordered = """
    {"tenant":"ten_abc","queues":{"refunds":{"delivery":{"kind":"LocalForward"},"retentionDays":3},"orders":{"retentionDays":5}}}
    """;

    private static Task<DeploymentPlan> PlanAsync(Server server, string json)
        => WaasTestHost.Build(apiStub: server.Stub).PlanDeploymentAsync(DeploymentFile.Parse(json));

    [Fact]
    public async Task The_plan_has_an_id_and_a_hash_and_the_same_file_against_the_same_state_gives_the_same()
    {
        DeploymentPlan first = await PlanAsync(new Server(), File);
        DeploymentPlan again = await PlanAsync(new Server(), File);

        Assert.Matches("^sha256:[0-9a-f]{64}$", first.PlanHash);
        Assert.Equal("plan_" + first.PlanHash.Substring(7, 24), first.PlanId);
        Assert.Equal(first.PlanHash, again.PlanHash);
        Assert.Equal(first.PlanId, again.PlanId);
    }

    [Fact]
    public async Task The_order_of_the_queues_and_the_files_formatting_do_not_count()
        => Assert.Equal((await PlanAsync(new Server(), File)).PlanHash, (await PlanAsync(new Server(), SameFileReordered)).PlanHash);

    [Fact]
    public async Task A_change_in_the_server_state_or_in_what_apply_would_change_gives_another_hash()
    {
        string hash = (await PlanAsync(new Server(), File)).PlanHash;

        Assert.NotEqual(hash, (await PlanAsync(new Server { OrdersState = "sha256:" + new string('c', 64) }, File)).PlanHash);
        Assert.NotEqual(hash, (await PlanAsync(new Server { OrdersRetentionTo = 4 }, File.Replace("\"retentionDays\": 5", "\"retentionDays\": 4"))).PlanHash);
    }

    [Fact]
    public async Task Notes_and_a_change_that_only_reorders_a_set_do_not_count()
    {
        string hash = (await PlanAsync(new Server(), File)).PlanHash;

        Assert.Equal(hash, (await PlanAsync(new Server { OrdersNote = "Worded another way by a newer Queuey." }, File)).PlanHash);
        Assert.Equal(hash, (await PlanAsync(new Server
        {
            OrdersFilterFrom = new object[] { new { field = "type", op = "eq", value = "a" }, new { field = "amount", op = "gt", value = "1" } },
        }, File)).PlanHash);
    }

    [Fact]
    public async Task The_plan_shows_each_queues_ingress_url_and_the_state_each_step_rests_on()
    {
        DeploymentPlan plan = await PlanAsync(new Server(), File);

        DeploymentPlanQueue orders = plan.Queues.Single(q => q.Name == "orders");
        Assert.Equal("https://ingress.example/events/ten_abc/orders", orders.IngressUrl);
        Assert.Equal("que_orders", orders.PublicId);

        DeploymentPlanStep kind = plan.Steps.Single(s => s.Target == "queues.refunds" && s.Aspect == "kind");
        Assert.Equal("delivery.kind", Assert.Single(kind.Changes).Path);
        Assert.Equal("sha256:" + new string('b', 64), kind.State);
        Assert.Contains(kind.Notes, n => n.Contains("queuey listen"));
    }

    [Fact]
    public async Task A_queue_apply_would_create_is_hashed_by_what_apply_would_send_it()
    {
        // Ingen dry run kan svare for en kø som ikke finnes; hashen dekker det apply sender etter at den er opprettet.
        var stub = new StubHttpMessageHandler(req => $"{req.Method.Method} {req.RequestUri!.AbsolutePath}" switch
        {
            "GET /tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/credentials" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PATCH /tenants/ten_abc/policy" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
            "PUT /queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = (string?)null, displayName = "stripe", created = true, hasDeliveryTarget = false }),
            string other => throw new InvalidOperationException("unexpected " + other),
        });
        const string stripe = """
        { "tenant": "ten_abc", "queues": { "stripe": {
            "ingress": { "authMode": "SignedRequest", "signedRequest": { "template": "stripe", "credentialRef": "stripe-whsec" } },
            "delivery": { "url": "/api/stripe", "kind": "localForward" } } } }
        """;

        DeploymentPlan plan = await WaasTestHost.Build(apiStub: stub).PlanDeploymentAsync(DeploymentFile.Parse(stripe));
        DeploymentPlan otherKind = await WaasTestHost.Build(apiStub: stub).PlanDeploymentAsync(DeploymentFile.Parse(stripe.Replace("localForward", "http")));

        DeploymentPlanStep create = Assert.Single(plan.Steps, s => s.Creates);
        Assert.Equal("localForward", create.Desired!.Value.GetProperty("kind").GetString());
        Assert.Equal("stripe-whsec", create.Desired.Value.GetProperty("ingress").GetProperty("signedRequest").GetProperty("credentialRef").GetString());
        Assert.Contains(create.Notes, n => n.Contains("deliver to a local listener"));
        Assert.Contains(create.Notes, n => n.Contains("No credential named 'stripe-whsec' is stored in this workspace yet"));
        Assert.Null(create.Error);
        Assert.Null(Assert.Single(plan.Queues).PublicId);
        Assert.NotEqual(plan.PlanHash, otherKind.PlanHash);
    }
}
