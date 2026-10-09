using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Planer Queuey lagrer, fra kommandolinjen (Queuey F3.11, beslutning 6 i plan-approval-f311). queuey plan lagrer ingenting uten
/// --store, som forsegler planen, eller --submit, som også sender den til innboksen når en person skal godkjenne den (BØR 2 fra
/// reviewen av #64); queuey apply lager den selv når Queuey krever den,
/// applyer den med en gang når policyen kjører den, og avslutter med 5 mens den venter på en person. apply --plan applyer en
/// godkjent plan, --wait venter på godkjenningen, og plan --local er planen fra før, uten id hos Queuey.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class StoredPlanCommandTests : IDisposable
{
    private const string PlanId = "plan_7Hk2mQ9xLr4Ts1Va";
    private const string Token = "apply-token-1";
    private static readonly string Hash = string.Concat(Enumerable.Repeat("cd", 32));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-stored-plan-cli-tests", Guid.NewGuid().ToString("N"));
    private readonly TimeSpan _poll = StoredPlanText.PollInterval;

    public StoredPlanCommandTests()
    {
        Directory.CreateDirectory(_dir);
        StoredPlanText.PollInterval = TimeSpan.FromMilliseconds(10);
    }

    public void Dispose()
    {
        StoredPlanText.PollInterval = _poll;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile()
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 } } }""");
        return path;
    }

    /// <summary>
    /// ten_abc med køen orders, og planene: policyen gir <see cref="Decision"/>, og lesingen av planen gir statusene i
    /// <see cref="Statuses"/> etter tur (den siste blir stående). <see cref="StartWithoutPlan"/> er svaret på en apply uten plan.
    /// </summary>
    private sealed class Server
    {
        public string Decision { get; init; } = "execute";
        public Queue<string> Statuses { get; } = new();
        public Func<HttpResponseMessage>? StartWithoutPlan { get; init; }
        public Func<RecordedRequest, HttpResponseMessage?>? Override { get; init; }
        public string? WarningOnStart { get; init; }
        private string _status = "approved";

        public RecordingHandler Handler => _handler ??= new RecordingHandler(Respond) { AnswersManagement = true, AnswersPlans = true };
        private RecordingHandler? _handler;

        private HttpResponseMessage Respond(RecordedRequest req)
        {
            if (Override?.Invoke(req) is { } overridden)
                return overridden;
            bool dryRun = req.Uri.Query.Contains("dryRun=true", StringComparison.Ordinal);
            switch (req.Key)
            {
                case "GET /tenants/ten_abc/queues":
                    return RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } });
                case "GET /tenants/ten_abc/credentials":
                    return RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
                case "POST /tenants/ten_abc/deployment/plans":
                    return RecordingHandler.Json(HttpStatusCode.Created, new
                    {
                        planId = PlanId, workspace = "ten_abc", status = "proposed", version = 1, decision = "undecided",
                        executes = "client", adopt = Array.Empty<string>(), stepCount = 0, expiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
                    });
                case "POST /tenants/ten_abc/deployment/plans/" + PlanId + "/seal":
                    return RecordingHandler.Json(HttpStatusCode.OK, new
                    {
                        planId = PlanId, version = 1, hash = Hash, status = "proposed", decision = Decision,
                        rule = Decision == "execute" ? "v1.key.nonprod.changes.execute" : Decision == "denied" ? "v1.key.never.denied" : "v1.key.prod.changes.requires_approval",
                        @class = "changes", stepCount = 2,
                        inboxUrl = Decision == "requires_approval" ? "https://app.queuey.test/approvals/" + PlanId : null,
                    });
                case "POST /tenants/ten_abc/deployment/plans/" + PlanId + "/submit":
                    _status = "pending_approval";
                    return RecordingHandler.Json(HttpStatusCode.Accepted, new
                    {
                        plan = PlanId, status = "pending_approval", approvalUrl = "https://app.queuey.test/approvals/" + PlanId,
                        expiresAt = DateTimeOffset.Parse("2026-10-10T12:00:00Z"), policyRule = "v1.key.prod.changes.requires_approval",
                    });
                case "GET /tenants/ten_abc/deployment/plans/" + PlanId:
                    if (Statuses.Count > 0)
                        _status = Statuses.Dequeue();
                    return RecordingHandler.Json(HttpStatusCode.OK, new
                    {
                        planId = PlanId, workspace = "ten_abc", status = _status, version = 1, decision = Decision,
                        rule = "v1.key.prod.changes.requires_approval", @class = "changes", hash = Hash, executes = "client",
                        adopt = Array.Empty<string>(), stepCount = 2, expiresAt = DateTimeOffset.Parse("2026-10-10T12:00:00Z"),
                        steps = Array.Empty<object>(),
                    });
                case "POST /tenants/ten_abc/deployment/applies":
                    if (!req.Json.TryGetProperty("planId", out _) && StartWithoutPlan is not null)
                        return StartWithoutPlan();
                    HttpResponseMessage started = RecordingHandler.Json(HttpStatusCode.OK, new
                    {
                        token = Token, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30), enforcement = "Warn", workspace = (object?)null,
                    });
                    if (WarningOnStart is not null)
                        started.Headers.Add("X-Queuey-Warning", WarningOnStart);
                    return started;
                case "PUT /queues":
                {
                    HttpResponseMessage queue = RecordingHandler.Json(HttpStatusCode.OK,
                        new { dryRun, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true });
                    if (dryRun) queue.Headers.Add("X-Queuey-Plan-Step", "0");
                    return queue;
                }
            }

            if (dryRun)
            {
                HttpResponseMessage plan = RecordingHandler.Json(HttpStatusCode.OK, new
                {
                    dryRun = true, target = "queue que_orders", changes = new[] { new { path = "policy.retentionDays", from = 7, to = 5 } },
                    notes = Array.Empty<string>(), stateHash = "sha256:" + Hash,
                });
                plan.Headers.Add("X-Queuey-Plan-Step", "1");
                return plan;
            }

            if (req.Method == HttpMethod.Get)
                throw new InvalidOperationException(req.Key);
            return RecordingHandler.NoContent();
        }
    }

    private static HttpResponseMessage PlanRequired() => RecordingHandler.Error(HttpStatusCode.Forbidden, "plan_required",
        "An API key applies to workspace ten_abc, which is prod, only through a configuration plan. Nothing was started.",
        "Make a plan with POST /tenants/{tenantPublicId}/deployment/plans … queuey apply does it for you.");

    private static IEnumerable<RecordedRequest> Starts(RecordingHandler api) => api.Requests.Where(r => r.Key == "POST /tenants/ten_abc/deployment/applies");

    private static IEnumerable<RecordedRequest> RealWrites(RecordingHandler api)
        => api.Writes.Where(w => w.Uri.Query.Length == 0 && !w.Path.Contains("/deployment/", StringComparison.Ordinal));

    // ── queuey plan ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_store_stores_the_plan_and_exits_0_when_the_policy_runs_it()
    {
        var server = new Server();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--store")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains($"  {PlanId}  stored in Queuey, hash {Hash} (version 1)", run.Stdout);
        Assert.Contains("Decision: execute by policy rule v1.key.nonprod.changes.execute (class changes), proposed.", run.Stdout);
        Assert.Contains($"The policy runs it: queuey apply --plan {PlanId} applies it.", run.Stdout);
        Assert.Contains("~ policy.retentionDays: 7 → 5", run.Stdout);
        // Ingenting skrives, og ingen apply startes: dry runs med planen, forseglingen og ingenting mer.
        Assert.Empty(RealWrites(server.Handler));
        Assert.Empty(Starts(server.Handler));
        Assert.All(server.Handler.Writes.Where(w => w.Uri.Query.Length > 0), w =>
            Assert.Equal(PlanId, server.Handler.Headers[server.Handler.Requests.IndexOf(w)]["X-Queuey-Plan"]));
    }

    [Fact]
    public async Task Plan_submit_that_a_person_approves_goes_to_the_inbox_and_exits_5_with_where_it_is_approved()
    {
        var server = new Server { Decision = "requires_approval" };

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--submit")), server.Handler);

        Assert.Equal(ExitCodes.PendingApproval, human.Exit);
        Assert.Equal(5, ExitCodes.PendingApproval);
        Assert.Single(server.Handler.Requests, r => r.Key == $"POST /tenants/ten_abc/deployment/plans/{PlanId}/submit");
        Assert.Contains($"It waits for a person's approval in Queuey's inbox: https://app.queuey.test/approvals/{PlanId}", human.Stdout);
        Assert.Contains($"Apply it once approved: queuey apply --plan {PlanId}", human.Stdout);

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--submit", "--json")),
            new Server { Decision = "requires_approval" }.Handler);

        Assert.Equal(ExitCodes.PendingApproval, json.Exit);
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal(PlanCommand.StoredJsonSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PlanId, root.GetProperty("planId").GetString());
        Assert.Equal(Hash, root.GetProperty("planHash").GetString());
        Assert.True(root.GetProperty("stored").GetBoolean());
        Assert.Equal("pending_approval", root.GetProperty("status").GetString());
        Assert.Equal("requires_approval", root.GetProperty("decision").GetString());
        Assert.Equal("v1.key.prod.changes.requires_approval", root.GetProperty("rule").GetString());
        Assert.Equal("changes", root.GetProperty("class").GetString());
        Assert.Equal($"https://app.queuey.test/approvals/{PlanId}", root.GetProperty("approvalUrl").GetString());
    }

    [Fact]
    public async Task Plan_stores_nothing_unless_asked_so_a_pull_request_job_fills_no_inbox()
    {
        // BØR 2 fra reviewen av #64: queuey plan i en PR-jobb skal ikke lagre planer eller sende dem til innboksen.
        var server = new Server { Decision = "requires_approval" };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain(server.Handler.Requests, r => r.Path.Contains("/deployment/plans", StringComparison.Ordinal));
        Assert.Contains("  local plan, not stored in Queuey  sha256:", run.Stdout);
    }

    [Fact]
    public async Task Plan_store_seals_a_plan_for_a_person_without_sending_it_and_exits_0()
    {
        var server = new Server { Decision = "requires_approval" };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--store")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain(server.Handler.Requests, r => r.Path.EndsWith("/submit", StringComparison.Ordinal));
        Assert.Contains($"A person approves it: queuey apply --plan {PlanId} sends it to Queuey's inbox", run.Stdout);
    }

    [Theory]
    [InlineData("--store")]
    [InlineData("--submit")]
    public async Task Plan_local_with_store_or_submit_is_a_usage_error(string option)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--local", option)));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--local makes the plan here and stores nothing", run.Stderr);
    }

    [Fact]
    public async Task A_stored_plan_from_a_pull_request_in_github_actions_says_its_branch_workflow_and_number()
    {
        var server = new Server();
        var env = new Dictionary<string, string>
        {
            ["GITHUB_REF"] = "refs/pull/12/merge",
            ["GITHUB_HEAD_REF"] = "feature/retention",
            ["GITHUB_WORKFLOW_REF"] = "acme/app/.github/workflows/plan.yml@refs/pull/12/merge",
            ["QUEUEY_USER_CONFIG"] = CliHarness.NoUserConfig,
        };

        CliRun run = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--store")), server.Handler, env);

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement source = server.Handler.Requests.Single(r => r.Key == "POST /tenants/ten_abc/deployment/plans").Json.GetProperty("source");
        Assert.Equal("refs/heads/feature/retention", source.GetProperty("ref").GetString());
        Assert.Equal("12", source.GetProperty("pullRequest").GetString());
        Assert.Equal("acme/app/.github/workflows/plan.yml@refs/pull/12/merge", source.GetProperty("workflow").GetString());
    }

    [Fact]
    public async Task A_pull_request_target_job_names_the_pull_requests_branch_and_number_from_the_event_not_the_base_ref()
    {
        // pull_request_target kjører med base-branchen i GITHUB_REF; kilden skal ikke se ut som main.
        string eventFile = Path.Combine(_dir, "event.json");
        File.WriteAllText(eventFile, """{ "pull_request": { "number": 34 } }""");
        var server = new Server();
        var env = new Dictionary<string, string>
        {
            ["GITHUB_REF"] = "refs/heads/main",
            ["GITHUB_HEAD_REF"] = "feature/retention",
            ["GITHUB_EVENT_PATH"] = eventFile,
            ["QUEUEY_USER_CONFIG"] = CliHarness.NoUserConfig,
        };

        await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--store")), server.Handler, env);

        JsonElement source = server.Handler.Requests.Single(r => r.Key == "POST /tenants/ten_abc/deployment/plans").Json.GetProperty("source");
        Assert.Equal("refs/heads/feature/retention", source.GetProperty("ref").GetString());
        Assert.Equal("34", source.GetProperty("pullRequest").GetString());
    }

    [Fact]
    public async Task Plan_the_policy_refuses_exits_1()
    {
        var server = new Server { Decision = "denied" };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--submit")), server.Handler);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("The policy refuses it, so nothing applies it.", run.Stdout);
        Assert.DoesNotContain(server.Handler.Requests, r => r.Path.EndsWith("/submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_local_stores_nothing_and_shows_the_clients_hash_without_a_plan_id()
    {
        var server = new Server();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--local", "--json")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain(server.Handler.Requests, r => r.Path.Contains("/deployment/plans", StringComparison.Ordinal));
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(PlanCommand.JsonSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        // Hashen v1, regnet her med de felles testvektorenes regler (DeploymentPlanHashTests), og ingen plan_-id: den er ikke lagret.
        Assert.Matches("^sha256:[0-9a-f]{64}$", root.GetProperty("planHash").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("planId").ValueKind);

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--local")), new Server().Handler);
        Assert.Contains("  local plan, not stored in Queuey  " + root.GetProperty("planHash").GetString(), human.Stdout);
        Assert.DoesNotContain("plan_", human.Stdout);
    }

    // ── queuey apply ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_that_queuey_requires_a_plan_for_makes_one_and_applies_it_at_once_when_the_policy_runs_it()
    {
        var server = new Server { StartWithoutPlan = PlanRequired };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("Queuey applies to this workspace from an API key only through a configuration plan", run.Stdout);
        Assert.Contains($"  plan {PlanId}, run by the policy", run.Stdout);
        Assert.Contains("✓ orders", run.Stdout);

        // Først en start uten plan (403), så planen, så en start med planId og planHash alene, og skrivingene med tokenet.
        RecordedRequest[] starts = Starts(server.Handler).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.False(starts[0].Json.TryGetProperty("planId", out _));
        Assert.Equal(new[] { "planId", "planHash" }, starts[1].Json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Hash, starts[1].Json.GetProperty("planHash").GetString());
        Assert.Equal(new[] { "PUT /queues", "PATCH /queues/que_orders/policy" }, RealWrites(server.Handler).Select(w => w.Key).ToArray());
        Assert.All(RealWrites(server.Handler), w => Assert.Equal(Token, server.Handler.Headers[server.Handler.Requests.IndexOf(w)]["X-Queuey-Apply"]));
    }

    [Fact]
    public async Task Apply_whose_plan_waits_for_a_person_writes_nothing_and_exits_5()
    {
        var server = new Server { StartWithoutPlan = PlanRequired, Decision = "requires_approval" };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git", "--json")), server.Handler);

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Empty(RealWrites(server.Handler));
        Assert.Single(Starts(server.Handler));
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.True(root.GetProperty("pendingApproval").GetBoolean());
        Assert.Equal(PlanId, root.GetProperty("plan").GetProperty("planId").GetString());
        Assert.Equal($"https://app.queuey.test/approvals/{PlanId}", root.GetProperty("plan").GetProperty("approvalUrl").GetString());
    }

    [Fact]
    public async Task Apply_wait_waits_for_the_approval_then_applies_the_plan()
    {
        var server = new Server { StartWithoutPlan = PlanRequired, Decision = "requires_approval" };
        server.Statuses.Enqueue("pending_approval");
        server.Statuses.Enqueue("pending_approval");
        server.Statuses.Enqueue("approved");

        CliRun run = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git", "--wait", "--timeout", "30")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains($"Waiting up to 30 s for a person to approve {PlanId}", run.Stdout);
        Assert.Contains($"  plan {PlanId}, approved by a person", run.Stdout);
        Assert.Equal(new[] { "PUT /queues", "PATCH /queues/que_orders/policy" }, RealWrites(server.Handler).Select(w => w.Key).ToArray());
        Assert.Equal(PlanId, Starts(server.Handler).Last().Json.GetProperty("planId").GetString());
    }

    [Fact]
    public async Task Apply_wait_that_runs_out_of_time_writes_nothing_and_exits_5()
    {
        var server = new Server { StartWithoutPlan = PlanRequired, Decision = "requires_approval" };

        CliRun run = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git", "--wait", "--timeout", "1")), server.Handler);

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Contains("Nothing was applied: the plan waits for a person's approval.", run.Stdout);
        Assert.Empty(RealWrites(server.Handler));
    }

    [Fact]
    public async Task Apply_plan_applies_an_approved_plan_by_its_id_without_making_another()
    {
        var server = new Server { Decision = "requires_approval" };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--plan", PlanId, "--json")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain(server.Handler.Requests, r => r.Key == "POST /tenants/ten_abc/deployment/plans");
        RecordedRequest start = Assert.Single(Starts(server.Handler));
        Assert.Equal(PlanId, start.Json.GetProperty("planId").GetString());
        Assert.False(start.Json.TryGetProperty("source", out _));
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(PlanId, root.GetProperty("plan").GetProperty("planId").GetString());
        Assert.Equal(2, RealWrites(server.Handler).Count());
    }

    [Fact]
    public async Task Apply_plan_that_was_sealed_for_a_person_and_never_sent_is_sent_to_the_inbox_and_exits_5()
    {
        var server = new Server { Decision = "requires_approval" };
        server.Statuses.Enqueue("proposed");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--plan", PlanId)), server.Handler);

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Single(server.Handler.Requests, r => r.Path.EndsWith("/submit", StringComparison.Ordinal));
        Assert.Empty(Starts(server.Handler));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("expired")]
    [InlineData("plan_stale")]
    public async Task Apply_plan_that_cannot_be_applied_any_more_exits_1_and_says_plan_again(string status)
    {
        var server = new Server { Decision = "requires_approval" };
        server.Statuses.Enqueue(status);

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--plan", PlanId)), server.Handler);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains($"Plan {PlanId} is {status}, so it is not applied.", run.Stderr);
        Assert.Contains("Plan again", run.Stderr);
        Assert.Empty(Starts(server.Handler));
    }

    [Fact]
    public async Task A_plan_that_went_stale_during_the_apply_exits_1_and_says_plan_again()
    {
        var server = new Server
        {
            Decision = "requires_approval",
            Override = req => req.Key == "PATCH /queues/que_orders/policy" && req.Uri.Query.Length == 0
                ? RecordingHandler.Error(HttpStatusCode.Conflict, "plan_stale", "Plan is stale: the policy of que_orders moved.", "Plan again: a new plan shows what is left to change.")
                : null,
        };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--plan", PlanId)), server.Handler);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("plan_stale", run.Stdout);
        Assert.Contains(StoredPlanText.PlanAgain, run.Stdout);
    }

    [Theory]
    [InlineData("--adopt", "orders")]
    [InlineData("--repo", "acme/app")]
    [InlineData("--dry-run", null)]
    public async Task Apply_plan_takes_its_source_and_adopt_from_the_plan_so_the_flags_for_them_are_usage_errors(string option, string? value)
    {
        string[] args = value is null
            ? CliHarness.With("apply", "--file", DeployFile(), "--plan", PlanId, option)
            : CliHarness.With("apply", "--file", DeployFile(), "--plan", PlanId, option, value);

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains($"so {option} does not go with it", run.Stderr);
    }

    [Theory]
    [InlineData("op_7Hk2mQ9x")]
    [InlineData("plan_")]
    [InlineData("plan_a b")]
    [InlineData("plan_../../tenants")]
    public async Task Apply_plan_with_something_that_is_not_a_plan_id_sends_nothing(string id)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--plan", id)));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--plan takes the id of a plan Queuey stores, plan_…", run.Stderr);
    }

    [Fact]
    public async Task Apply_timeout_without_wait_is_a_usage_error()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--timeout", "60")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--timeout says how long --wait waits", run.Stderr);
    }

    // ── would_require_approval og en eldre Queuey ───────────────────────────

    [Fact]
    public async Task An_apply_queuey_will_soon_refuse_without_a_plan_goes_through_with_a_clear_warning_on_stderr()
    {
        const string warning = "would_require_approval: Workspace ten_abc is prod. Once Queuey enforces plans, an API key applies there only through a configuration plan. This apply runs as before.";
        var server = new Server { WarningOnStart = warning };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git", "--json")), server.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("Warning from Queuey: " + warning, run.Stderr);
        Assert.Contains("`queuey plan` stores one in Queuey", run.Stderr);
        // stdout er bare JSON, og advarselen står i den også.
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(warning, Assert.Single(root.GetProperty("serverWarnings").EnumerateArray()).GetString());
        Assert.Equal(2, RealWrites(server.Handler).Count());
    }

    [Fact]
    public async Task Against_a_Queuey_without_plans_plan_and_apply_work_as_before()
    {
        // Ingen AnswersPlans: POST …/deployment/plans svarer 404, som en Queuey fra før F3.11.
        RecordingHandler Old() => new(req => req.Key switch
        {
            "POST /tenants/ten_abc/deployment/applies" => RecordingHandler.Json(HttpStatusCode.OK,
                new { token = Token, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30), enforcement = "Warn", workspace = (object?)null }),
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = req.Uri.Query.Length > 0, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
            _ when req.Uri.Query.Length > 0 => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                dryRun = true, target = "queue", changes = new[] { new { path = "policy.retentionDays", from = 7, to = 5 } }, notes = Array.Empty<string>(),
            }),
            _ => RecordingHandler.NoContent(),
        }) { AnswersManagement = true };

        RecordingHandler planServer = Old();
        CliRun plan = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--no-git", "--store")), planServer);

        Assert.Equal(ExitCodes.Success, plan.Exit);
        Assert.Contains("local plan, not stored in Queuey  sha256:", plan.Stdout);
        Assert.Contains("! plans_unsupported: This Queuey stores no configuration plans", plan.Stdout);
        Assert.Single(planServer.ManagementRequests, r => r.Key == "POST /tenants/ten_abc/deployment/plans");
        Assert.All(planServer.Writes.Where(w => w.Uri.Query.Length > 0), w =>
            Assert.False(planServer.Headers[planServer.Requests.IndexOf(w)].ContainsKey("X-Queuey-Plan")));

        RecordingHandler applyServer = Old();
        CliRun apply = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git")), applyServer);

        Assert.Equal(ExitCodes.Success, apply.Exit);
        Assert.Empty(applyServer.ManagementRequests);
        Assert.Equal(new[] { "POST /tenants/ten_abc/deployment/applies", "PUT /queues", "PATCH /queues/que_orders/policy" },
            applyServer.Writes.Select(w => w.Key).ToArray());
        Assert.Equal(string.Empty, apply.Stderr);
    }
}
