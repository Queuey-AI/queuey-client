using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Planer Queuey lagrer (Queuey F3.11, beslutning 1 og 6 i plan-approval-f311). Planen bygges av de vanlige dry runs med
/// X-Queuey-Plan, forsegles, og sendes til innboksen når policyen gir den til en person. En apply bundet til planen følger
/// kontrakten fra Queuey #503: start med planId og planHash alene, kind som {"enabled":…} og mode som tall, hver del av
/// desired som ikke er null én gang, og stegene på ett mål ett om gangen. Formene speiler Queueys
/// PlanBuildingEndToEndTests og PlanApplyBindingEndToEndTests (integration/agents @ 87604ed0).
/// </summary>
public class StoredPlanTests
{
    private const string PlanId = "plan_7Hk2mQ9xLr4Ts1Va";
    private static readonly string Hash = string.Concat(Enumerable.Repeat("ab", 32));

    /// <summary>
    /// En Queuey med planer, som svarer slik backend gjør: en tom plan, dry runs med steget i X-Queuey-Plan-Step,
    /// forseglingen med <see cref="Decision"/>, innsendingen, lesingen av planen og en apply bundet til den.
    /// </summary>
    private sealed class PlansServer
    {
        private int _nextStep;

        public PlansServer(params string[] existingQueues)
        {
            Existing = new HashSet<string>(existingQueues, StringComparer.Ordinal);
            Stub = new StubHttpMessageHandler((_, req, body) => Respond(req, body)) { AnswersManagement = true, AnswersPlans = true };
        }

        public HashSet<string> Existing { get; }
        public string Decision { get; set; } = "execute";
        public string ReadStatus { get; set; } = "approved";
        public List<object> ReadSteps { get; } = new();
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> ApplyWarnings { get; } = new();
        public StubHttpMessageHandler Stub { get; }

        public IEnumerable<(HttpRequestMessage Request, string? Body)> Sent
            => Stub.Requests.Select((r, i) => (r, Stub.Bodies[i] is { Length: > 0 } b ? Encoding.UTF8.GetString(b) : null));

        public IEnumerable<(HttpRequestMessage Request, string? Body)> DryRuns => Sent.Where(s => s.Request.RequestUri!.Query.Contains("dryRun=true"));

        public IEnumerable<(HttpRequestMessage Request, string? Body)> Writes
            => Sent.Where(s => s.Request.Method != HttpMethod.Get && !s.Request.RequestUri!.Query.Contains("dryRun=true")
                               && !s.Request.RequestUri.AbsolutePath.Contains("/deployment/"));

        public (HttpRequestMessage Request, string? Body) One(string method, string path)
            => Assert.Single(Sent, s => s.Request.Method.Method == method && s.Request.RequestUri!.AbsolutePath == path);

        private HttpResponseMessage Respond(HttpRequestMessage req, byte[]? body)
        {
            string path = req.RequestUri!.AbsolutePath;
            string key = $"{req.Method.Method} {path}";
            bool dryRun = req.RequestUri.Query.Contains("dryRun=true", StringComparison.Ordinal);
            if (Routes.TryGetValue(key, out var route))
                return route(req);

            switch (key)
            {
                case "GET /tenants/ten_abc/queues":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, Existing.Select(n => new
                    {
                        publicId = "que_" + n, displayName = n, mode = "Deliver", hasDeliveryTarget = true,
                    }));
                case "GET /tenants/ten_abc/credentials":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
                case "GET /tenants/ten_abc":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { deployment = (object?)null });
                case "POST /tenants/ten_abc/deployment/plans":
                    return StubHttpMessageHandler.Json(HttpStatusCode.Created, new
                    {
                        planId = PlanId, workspace = "ten_abc", status = "proposed", version = 1, decision = "undecided",
                        executes = "client", adopt = Array.Empty<string>(), stepCount = 0, expiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
                    });
                case "POST /tenants/ten_abc/deployment/plans/" + PlanId + "/seal":
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        planId = PlanId, version = 1, hash = Hash, status = "proposed", decision = Decision,
                        rule = Decision == "execute" ? "v1.key.nonprod.changes.execute" : "v1.key.prod.changes.requires_approval",
                        @class = "changes", stepCount = _nextStep,
                        inboxUrl = Decision == "requires_approval" ? "https://app.queuey.test/inbox/" + PlanId : null,
                    });
                case "POST /tenants/ten_abc/deployment/plans/" + PlanId + "/submit":
                    return StubHttpMessageHandler.Json(HttpStatusCode.Accepted, new
                    {
                        plan = PlanId, status = "pending_approval", approvalUrl = "https://app.queuey.test/inbox/" + PlanId,
                        expiresAt = DateTimeOffset.UtcNow.AddHours(24), policyRule = "v1.key.prod.changes.requires_approval",
                    });
                case "GET /tenants/ten_abc/deployment/plans/" + PlanId:
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        planId = PlanId, workspace = "ten_abc", status = ReadStatus, version = 1, decision = "requires_approval",
                        rule = "v1.key.prod.changes.requires_approval", @class = "changes", hash = Hash, executes = "client",
                        adopt = Array.Empty<string>(), stepCount = ReadSteps.Count, expiresAt = DateTimeOffset.UtcNow.AddHours(24),
                        steps = ReadSteps,
                    });
                case "POST /tenants/ten_abc/deployment/applies":
                {
                    HttpResponseMessage started = StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        token = StubHttpMessageHandler.ApplyToken, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                        enforcement = "Warn", workspace = (object?)null,
                        planId = JsonDocument.Parse(body!).RootElement.TryGetProperty("planId", out JsonElement p) ? p.GetString() : null,
                    });
                    foreach (string warning in ApplyWarnings)
                        started.Headers.Add("X-Queuey-Warning", warning);
                    return started;
                }
            }

            if (path.StartsWith("/tenants/ten_abc/deployment/plans/" + PlanId + "/steps/", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { index = 0 });

            if (key == "PUT /queues")
            {
                string name = JsonDocument.Parse(body!).RootElement.GetProperty("displayName").GetString()!;
                bool exists = Existing.Contains(name);
                HttpResponseMessage queue = StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    dryRun, publicId = exists || !dryRun ? "que_" + name : null, displayName = name, created = !exists, hasDeliveryTarget = true,
                });
                return dryRun ? WithStep(queue) : queue;
            }

            if (dryRun)
                return WithStep(StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    dryRun = true, target = path, changes = new[] { new { path = "policy.retentionDays", from = 7, to = 5 } },
                    notes = Array.Empty<string>(), stateHash = "sha256:" + Hash,
                }));

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private HttpResponseMessage WithStep(HttpResponseMessage response)
        {
            response.Headers.Add("X-Queuey-Plan-Step", (_nextStep++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }
    }

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;

    private static Task<DeploymentPlan> StoreAsync(PlansServer server, string json, SyncOptions? options = null)
        => WaasTestHost.Build(apiStub: server.Stub).StorePlanAsync(DeploymentFile.Parse(json), options);

    private const string ExistingOrders = """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 } } }""";

    // ── byggingen ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_stored_plan_is_built_from_dry_runs_that_carry_the_plan_and_never_the_apply_token_then_sealed()
    {
        var server = new PlansServer("orders");

        DeploymentPlan plan = await StoreAsync(server, ExistingOrders, new SyncOptions
        {
            Source = new DeploymentFileSource { Repo = "https://ghp_secret@github.com/acme/app.git?x=1", Path = "./queuey.deploy.json", Commit = "ABC1234" },
            Adopt = new[] { "orders" },
        });

        // Planen startes med kilden renset og det den tar tilbake, som en apply (F2.4), og ingen apply startes.
        JsonElement created = JsonDocument.Parse(server.One("POST", "/tenants/ten_abc/deployment/plans").Body!).RootElement;
        Assert.Equal("https://github.com/acme/app", created.GetProperty("source").GetProperty("repo").GetString());
        Assert.Equal("queuey.deploy.json", created.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal("abc1234", created.GetProperty("source").GetProperty("commit").GetString());
        Assert.Equal("orders", Assert.Single(created.GetProperty("adopt").GetProperty("queues").EnumerateArray()).GetString());
        Assert.DoesNotContain(server.Sent, s => s.Request.RequestUri!.AbsolutePath.EndsWith("/deployment/applies", StringComparison.Ordinal));
        Assert.DoesNotContain("ghp_secret", string.Join("\n", server.Sent.Select(s => s.Body)));

        // Hver dry run har planen og aldri apply-tokenet: Queuey nekter de to sammen. Ingen probe: den tomme policy-patchen
        // ville blitt et steg apply aldri sender.
        Assert.Equal(new[] { "PUT /queues", "PATCH /queues/que_orders/policy" },
            server.DryRuns.Select(d => $"{d.Request.Method.Method} {d.Request.RequestUri!.AbsolutePath}").ToArray());
        Assert.All(server.DryRuns, d =>
        {
            Assert.Equal(PlanId, Header(d.Request, "X-Queuey-Plan"));
            Assert.Null(Header(d.Request, "X-Queuey-Apply"));
        });

        // Forseglet, og policyen kjører den: den sendes ikke til innboksen.
        server.One("POST", $"/tenants/ten_abc/deployment/plans/{PlanId}/seal");
        Assert.DoesNotContain(server.Sent, s => s.Request.RequestUri!.AbsolutePath.EndsWith("/submit", StringComparison.Ordinal));
        StoredPlan stored = Assert.IsType<StoredPlan>(plan.Stored);
        Assert.Equal(PlanId, stored.PlanId);
        Assert.Equal(Hash, stored.Hash);
        Assert.Equal("execute", stored.Decision);
        Assert.True(stored.CanBeApplied);
        Assert.False(stored.IsPendingApproval);

        // Klientens egen hash (v1) er uendret ved siden av: den er det plan --local viser.
        Assert.Matches("^sha256:[0-9a-f]{64}$", plan.PlanHash);
    }

    [Fact]
    public async Task A_queue_the_plan_creates_gets_what_apply_sends_on_its_step_with_the_mode_apply_would_give_it_and_no_null_parts()
    {
        var server = new PlansServer();

        await StoreAsync(server, """
            { "tenant": "ten_abc", "queues": { "orders": {
                "retentionDays": 3, "delivery": { "url": "https://hooks.example.com/orders", "kind": "http" } } } }
            """);

        // PUT /queues ble steg 0. Det apply sender når køen finnes, settes på det: kind og mode som tekst, og modusen apply gir en
        // ny kø med et sted å levere, deliver, selv om fila ikke sier den.
        (HttpRequestMessage request, string? body) = server.One("PUT", $"/tenants/ten_abc/deployment/plans/{PlanId}/steps/0/desired");
        JsonElement desired = JsonDocument.Parse(body!).RootElement;
        Assert.Equal(new[] { "policy", "delivery", "kind", "mode" }, desired.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(3, desired.GetProperty("policy").GetProperty("retentionDays").GetInt32());
        Assert.Equal("https://hooks.example.com/orders", desired.GetProperty("delivery").GetProperty("url").GetString());
        Assert.Equal("http", desired.GetProperty("kind").GetString());
        Assert.Equal("deliver", desired.GetProperty("mode").GetString());
        Assert.Null(Header(request, "X-Queuey-Apply"));

        // desired settes før forseglingen, som regner hashen over det.
        int desiredAt = server.Sent.ToList().FindIndex(s => s.Request.RequestUri!.AbsolutePath.EndsWith("/desired", StringComparison.Ordinal));
        int sealAt = server.Sent.ToList().FindIndex(s => s.Request.RequestUri!.AbsolutePath.EndsWith("/seal", StringComparison.Ordinal));
        Assert.True(desiredAt < sealAt);
    }

    [Fact]
    public async Task A_plan_the_policy_gives_to_a_person_is_sent_to_the_inbox_and_says_where_it_is_approved()
    {
        var server = new PlansServer("orders") { Decision = "requires_approval" };

        DeploymentPlan plan = await StoreAsync(server, ExistingOrders);

        server.One("POST", $"/tenants/ten_abc/deployment/plans/{PlanId}/submit");
        StoredPlan stored = plan.Stored!;
        Assert.True(stored.IsPendingApproval);
        Assert.False(stored.CanBeApplied);
        Assert.Equal("https://app.queuey.test/inbox/" + PlanId, stored.ApprovalUrl);
        Assert.Equal("v1.key.prod.changes.requires_approval", stored.Rule);
    }

    [Fact]
    public async Task A_plan_with_a_refused_dry_run_is_not_sealed_and_its_step_says_why()
    {
        var server = new PlansServer("orders");
        server.Routes["PATCH /queues/que_orders/policy"] = _ =>
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, new { error = new { code = "retention_too_long", message = "At most 3 days." } });

        DeploymentPlan plan = await StoreAsync(server, ExistingOrders);

        Assert.False(plan.WouldSucceed);
        Assert.Equal("retention_too_long", plan.Steps.Single(s => s.Error is not null).Error!.ErrorCode);
        Assert.DoesNotContain(server.Sent, s => s.Request.RequestUri!.AbsolutePath.EndsWith("/seal", StringComparison.Ordinal));
        Assert.False(plan.Stored!.IsSealed);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null, "plans_unsupported")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "plans_unavailable", "plans_unavailable")]
    public async Task Against_a_Queuey_that_stores_no_plans_the_plan_is_made_here_as_before_and_says_so(
        HttpStatusCode status, string? code, string warning)
    {
        var server = new PlansServer("orders");
        server.Routes["POST /tenants/ten_abc/deployment/plans"] = _ => code is null
            ? new HttpResponseMessage(status)
            : StubHttpMessageHandler.Json(status, new { error = new { code, message = "This server has no key for plans." } });

        DeploymentPlan plan = await StoreAsync(server, ExistingOrders);

        Assert.Null(plan.Stored);
        Assert.StartsWith(warning + ":", plan.Warnings[0], StringComparison.Ordinal);
        // Som før: en apply startes for dry runs, uten planens header.
        Assert.All(server.DryRuns, d => Assert.Null(Header(d.Request, "X-Queuey-Plan")));
        Assert.True(plan.WouldSucceed);
    }

    // ── applyen bundet til planen (kontrakten fra Queuey #503) ─────────────

    private const string NewQueueFile = """
        { "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 3, "mode": "logOnly", "delivery": { "kind": "localForward" } } } }
        """;

    private static object CreateStep(object desired) => new
    {
        index = 0, target = "orders", aspect = "queue", creates = true, @class = "creates", state = (string?)null,
        changes = Array.Empty<object>(), desired, refusal = (string?)null,
    };

    private static Task<QueueSyncResult> ApplyBoundAsync(PlansServer server, string json)
        => WaasTestHost.Build(apiStub: server.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(json), new SyncOptions
        {
            Plan = new StoredPlan { PlanId = PlanId, Tenant = "ten_abc", Status = "approved", Decision = "requires_approval", Hash = Hash },
        });

    [Fact]
    public async Task An_apply_bound_to_a_plan_starts_with_its_id_and_hash_alone_and_sends_each_part_of_desired_once_in_the_endpoints_form()
    {
        var server = new PlansServer();
        server.ReadSteps.Add(CreateStep(new { policy = new { retentionDays = 3 }, kind = "localForward", mode = "logOnly" }));

        QueueSyncResult result = await ApplyBoundAsync(server, NewQueueFile);

        Assert.True(result.AllSucceeded);
        Assert.Equal(PlanId, result.PlanId);

        // Starten har planId og planHash og ikke noe annet: kilden og adopt er planens (400 ellers).
        JsonElement start = JsonDocument.Parse(server.One("POST", "/tenants/ten_abc/deployment/applies").Body!).RootElement;
        Assert.Equal(new[] { "planId", "planHash" }, start.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(PlanId, start.GetProperty("planId").GetString());
        Assert.Equal(Hash, start.GetProperty("planHash").GetString());

        // Hver del av desired én gang, i rekkefølge, med tokenet: kind som {"enabled":…} og mode som tallet, også logOnly på en
        // kø som nettopp ble opprettet og alt er logOnly.
        Assert.Equal(new[]
        {
            "PUT /queues {\"tenantPublicId\":\"ten_abc\",\"displayName\":\"orders\"}",
            "PATCH /queues/que_orders/policy {\"retentionDays\":3}",
            "PATCH /queues/que_orders/local-forward {\"enabled\":true}",
            "PATCH /queues/que_orders/mode-change {\"mode\":1}",
        }, server.Writes.Select(w => $"{w.Request.Method.Method} {w.Request.RequestUri!.AbsolutePath} {w.Body}").ToArray());
        Assert.All(server.Writes, w => Assert.Equal(StubHttpMessageHandler.ApplyToken, Header(w.Request, "X-Queuey-Apply")));
    }

    [Fact]
    public async Task A_queue_the_plan_creates_without_a_declared_mode_gets_the_mode_its_plan_shows()
    {
        var server = new PlansServer();
        server.ReadSteps.Add(CreateStep(new { policy = new { retentionDays = 3 }, mode = "deliver" }));

        QueueSyncResult result = await ApplyBoundAsync(server, """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 3 } } }""");

        Assert.True(result.AllSucceeded);
        Assert.Contains(server.Writes, w => w.Request.RequestUri!.AbsolutePath == "/queues/que_orders/mode-change" && w.Body == "{\"mode\":3}");
        Assert.Equal("deliver", Assert.Single(result.Applied).Mode);
    }

    [Fact]
    public async Task A_write_queuey_says_is_already_written_counts_as_written()
    {
        var server = new PlansServer("orders");
        server.Routes["PATCH /queues/que_orders/policy"] = _ => StubHttpMessageHandler.Json(HttpStatusCode.Conflict,
            new { error = new { code = "step_already_applied", message = "Step 1 of plan is already written.", action = "Count the step as written and go on with the next." } });

        QueueSyncResult result = await ApplyBoundAsync(server, ExistingOrders);

        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task A_write_whose_answer_was_lost_is_sent_once_more_and_already_written_counts_as_written()
    {
        var server = new PlansServer("orders");
        int attempts = 0;
        server.Routes["PATCH /queues/que_orders/policy"] = _ => ++attempts == 1
            ? throw new HttpRequestException("The connection was reset.")
            : StubHttpMessageHandler.Json(HttpStatusCode.Conflict, new { error = new { code = "step_already_applied", message = "Already written." } });

        QueueSyncResult result = await ApplyBoundAsync(server, ExistingOrders);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_lost_answer_outside_a_plan_is_not_sent_again()
    {
        var server = new PlansServer("orders");
        int attempts = 0;
        server.Routes["PATCH /queues/que_orders/policy"] = _ =>
        {
            attempts++;
            throw new HttpRequestException("The connection was reset.");
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            WaasTestHost.Build(apiStub: server.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(ExistingOrders)));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_queue_whose_put_queuey_says_is_already_written_is_read_from_the_list()
    {
        var server = new PlansServer();
        server.ReadSteps.Add(CreateStep(new { policy = new { retentionDays = 3 } }));
        server.Routes["PUT /queues"] = _ =>
        {
            server.Existing.Add("orders");
            return StubHttpMessageHandler.Json(HttpStatusCode.Conflict, new { error = new { code = "step_already_applied", message = "Already written." } });
        };

        QueueSyncResult result = await ApplyBoundAsync(server, """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 3 } } }""");

        QueueApplyResult queue = Assert.Single(result.Applied);
        Assert.True(queue.Succeeded);
        Assert.True(queue.Created);
        Assert.Equal("que_orders", queue.PublicId);
        Assert.Contains(server.Writes, w => w.Request.RequestUri!.AbsolutePath == "/queues/que_orders/policy");
    }

    [Fact]
    public async Task A_stale_plan_fails_the_queue_with_queueys_answer()
    {
        var server = new PlansServer("orders");
        server.Routes["PATCH /queues/que_orders/policy"] = _ => StubHttpMessageHandler.Json(HttpStatusCode.Conflict,
            new { error = new { code = "plan_stale", message = "Plan is stale: the policy of que_orders moved.", action = "Plan again." } });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => ApplyBoundAsync(server, ExistingOrders));

        Assert.Equal("plan_stale", Assert.Single(ex.Queues!.Applied).Error!.ErrorCode);
    }

    [Fact]
    public async Task A_plan_for_another_workspace_is_refused_before_anything_is_sent()
    {
        var server = new PlansServer("orders");

        await Assert.ThrowsAsync<QueueyConfigurationException>(() => WaasTestHost.Build(apiStub: server.Stub).ApplyDeploymentAsync(
            DeploymentFile.Parse(ExistingOrders),
            new SyncOptions { Plan = new StoredPlan { PlanId = PlanId, Tenant = "ten_other", Status = "approved", Hash = Hash } }));

        Assert.Empty(server.Writes);
        Assert.DoesNotContain(server.Sent, s => s.Request.RequestUri!.AbsolutePath.EndsWith("/deployment/applies", StringComparison.Ordinal));
    }

    // ── plan_required og would_require_approval ─────────────────────────────

    private static HttpResponseMessage PlanRequired() => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, new
    {
        error = new
        {
            code = "plan_required",
            message = "An API key applies to workspace ten_abc, which is prod, only through a configuration plan. Nothing was started.",
            action = "Make a plan with POST /tenants/{tenantPublicId}/deployment/plans … queuey apply does it for you.",
        },
    });

    [Fact]
    public async Task An_apply_queuey_refuses_without_a_plan_throws_plan_required_before_it_writes_anything()
    {
        var server = new PlansServer("orders");
        server.Routes["POST /tenants/ten_abc/deployment/applies"] = _ => PlanRequired();

        QueueyPlanRequiredException ex = await Assert.ThrowsAsync<QueueyPlanRequiredException>(() =>
            WaasTestHost.Build(apiStub: server.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(ExistingOrders)));

        Assert.Equal("plan_required", ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("queuey apply does it for you", ex.SuggestedAction);
        Assert.Empty(server.Writes);
    }

    [Fact]
    public async Task A_local_plan_where_queuey_requires_a_stored_one_is_made_outside_an_apply_and_says_so()
    {
        var server = new PlansServer("orders");
        server.Routes["POST /tenants/ten_abc/deployment/applies"] = _ => PlanRequired();

        DeploymentPlan plan = await WaasTestHost.Build(apiStub: server.Stub).PlanDeploymentAsync(DeploymentFile.Parse(ExistingOrders), new SyncOptions());

        Assert.True(plan.ApplyRequiresPlan);
        Assert.False(plan.ApplyStarted);
        Assert.StartsWith("plan_required:", plan.Warnings[0], StringComparison.Ordinal);
        Assert.All(server.DryRuns, d => Assert.Null(Header(d.Request, "X-Queuey-Apply")));
        Assert.True(plan.WouldSucceed);
    }

    [Fact]
    public async Task An_apply_queuey_warns_will_need_a_plan_goes_through_and_carries_the_warning()
    {
        var server = new PlansServer("orders");
        server.ApplyWarnings.Add("would_require_approval: Workspace ten_abc is prod. This apply runs as before.");

        QueueSyncResult result = await WaasTestHost.Build(apiStub: server.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(ExistingOrders));

        Assert.True(result.AllSucceeded);
        Assert.Equal("would_require_approval: Workspace ten_abc is prod. This apply runs as before.", Assert.Single(result.ServerWarnings));
        Assert.Contains(server.Writes, w => w.Request.RequestUri!.AbsolutePath == "/queues/que_orders/policy");
    }

    // ── synken fra kode ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_sync_from_code_goes_on_past_a_queue_that_needs_a_plan_says_what_to_do_and_does_not_throw()
    {
        var server = new PlansServer("orders", "invoices");
        server.Routes["PATCH /queues/que_orders/policy"] = _ => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, new
        {
            error = new
            {
                code = "plan_required",
                message = "An API key makes this write (Queues.PatchPolicy) only through a configuration plan. Nothing was changed.",
                action = "Make a plan with POST /tenants/{tenantPublicId}/deployment/plans … queuey plan and queuey apply do it for you.",
            },
        });
        QueueyService service = WaasTestHost.Build(apiStub: server.Stub, queues: new[]
        {
            new QueueDefinition { Name = "orders", Policy = new QueuePolicy { RetentionDays = 5 } },
            new QueueDefinition { Name = "invoices", Policy = new QueuePolicy { RetentionDays = 5 } },
        });

        QueueSyncResult result = await service.SyncQueuesAsync();

        QueueApplyResult orders = result.Applied.Single(r => r.Name == "orders");
        Assert.True(orders.NeedsPlan);
        Assert.False(orders.Succeeded);
        Assert.Equal("plan_required", orders.Error!.ErrorCode);
        Assert.Contains("queuey plan and queuey apply do it for you", orders.Error.SuggestedAction);
        Assert.Contains(orders.Warnings, w => w.Contains("went on with the other queues", StringComparison.Ordinal)
                                              && w.Contains("run queuey apply", StringComparison.Ordinal));

        // Den andre køen er synket, og ingenting er en feil: appen som synker ved oppstart, starter.
        Assert.True(result.Applied.Single(r => r.Name == "invoices").Succeeded);
        Assert.Contains(server.Writes, w => w.Request.RequestUri!.AbsolutePath == "/queues/que_invoices/policy");
        Assert.Equal(0, result.Failed);
        Assert.Equal("orders", Assert.Single(result.PlanRequired).Name);
        Assert.False(result.AllSucceeded);
    }

    [Fact]
    public async Task A_sync_from_code_still_throws_for_a_real_failure_next_to_one_that_needs_a_plan()
    {
        var server = new PlansServer("orders", "invoices");
        server.Routes["PATCH /queues/que_orders/policy"] = _ => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
            new { error = new { code = "plan_required", message = "Needs a plan." } });
        server.Routes["PATCH /queues/que_invoices/policy"] = _ => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            new { error = new { code = "retention_too_long", message = "At most 3 days." } });
        QueueyService service = WaasTestHost.Build(apiStub: server.Stub, queues: new[]
        {
            new QueueDefinition { Name = "orders", Policy = new QueuePolicy { RetentionDays = 5 } },
            new QueueDefinition { Name = "invoices", Policy = new QueuePolicy { RetentionDays = 5 } },
        });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => service.SyncQueuesAsync());

        Assert.Equal(1, ex.Queues!.Failed);
        Assert.Contains("invoices", ex.Message);
        Assert.DoesNotContain("orders", ex.Message);
    }
}
