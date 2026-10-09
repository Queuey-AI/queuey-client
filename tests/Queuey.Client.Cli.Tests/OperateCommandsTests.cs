using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Drifts- og feilsøkingskommandoene fra blindtest 2 (2026-10-09, funn 3): queue health, diagnose, events search, resume, unlock
/// og replay til mottakeren, mot REST-endepunktene som virker med en nøkkel eller en innlogging. En handling en person avgjør i
/// prod, gir exit 5 med lenken.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class OperateCommandsTests
{
    private static Task<CliRun> Run(RecordingHandler api, params string[] args)
        => CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(args)), api);

    private static HttpResponseMessage Ok(object body) => RecordingHandler.Json(HttpStatusCode.OK, body);

    private static object OneTarget => new[] { new { targetId = "tgt_1", targetName = "warehouse", state = "RequiresAction", consecutiveFailures = 4, lastFailureClass = "Http5xx" } };

    // ── queue health ────────────────────────────────────────────────────────

    [Fact]
    public async Task Queue_health_reads_the_snapshot_the_receivers_and_the_lanes_of_a_queue_found_by_name()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_1/queues" => FlowAnswers.Queues(),
            "GET /queues/que_orders/metrics/snapshot" => Ok(new { processState = "Locked", received = 12, delivered = 8, failed = 4, lockedReason = "receiver_down" }),
            "GET /queues/que_orders/targets" => Ok(OneTarget),
            "GET /events/que_orders/lanes" => Ok(new
            {
                counts = new { total = 3, active = 2, blocked = 1, held = 0 },
                problemLanes = new { items = new[] { new { partitionKey = "unit-7", status = "Blocked", blockingEventPublicId = "evt_9", pendingCount = 5 } } },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun human = await Run(api, "queue", "health", "orders", "--tenant", "ten_1");
        CliRun json = await Run(api, "queue", "health", "orders", "--tenant", "ten_1", "--json");

        Assert.True(human.Exit == ExitCodes.Success, human.Stdout + human.Stderr);
        Assert.Contains("Queue orders (que_orders)", human.Stdout);
        Assert.Contains("locked because: receiver_down", human.Stdout);
        Assert.Contains("warehouse: RequiresAction, 4 failure(s) in a row, last failure Http5xx (target tgt_1)", human.Stdout);
        Assert.Contains("unit-7: Blocked, blocked by evt_9, 5 waiting", human.Stdout);
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal("que_orders", root.GetProperty("queuePublicId").GetString());
        Assert.Equal("Locked", root.GetProperty("snapshot").GetProperty("processState").GetString());
        Assert.Equal("1", root.GetProperty("lanes").GetProperty("counts").GetProperty("blocked").GetRawText());
    }

    [Fact]
    public async Task Queue_health_without_event_read_shows_the_rest_and_says_why_the_lanes_are_missing()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /queues/que_1/metrics/snapshot" => Ok(new { processState = "Running" }),
            "GET /queues/que_1/targets" => Ok(Array.Empty<object>()),
            "GET /events/que_1/lanes" => RecordingHandler.Error(HttpStatusCode.Forbidden, "permission_denied", "The key lacks event.read."),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await Run(api, "queue", "health", "que_1", "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lanes").ValueKind);
        Assert.Equal("The key lacks event.read.", root.GetProperty("lanesUnavailable").GetString());
    }

    // ── diagnose ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diagnose_gives_the_incident_report_and_what_the_failed_attempts_share()
    {
        object Detail(int code) => new
        {
            attempts = new[] { new { responseCode = code, targetEndpoint = "https://shop.test/hook", errorMessage = "Service Unavailable" } },
        };
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /events/que_1/incident-report" => Ok(new
            {
                incidentType = "locked", severity = "high", lockedReason = "receiver_down",
                requiredActions = new[] { "Fix the receiver, then resume." },
            }),
            "GET /events/que_1" => Ok(new { items = new[] { new { publicId = "evt_1" }, new { publicId = "evt_2" } }, totalCount = 2 }),
            "GET /events/que_1/evt_1" => Ok(Detail(503)),
            "GET /events/que_1/evt_2" => Ok(Detail(503)),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun json = await Run(api, "diagnose", "que_1", "--take", "5", "--json");
        CliRun human = await Run(api, "diagnose", "que_1");

        Assert.True(json.Exit == ExitCodes.Success, json.Stdout + json.Stderr);
        Assert.Contains("hasFailures=true", api.Requests.First(r => r.Key == "GET /events/que_1").Uri.Query);
        Assert.Contains("pageSize=5", api.Requests.First(r => r.Key == "GET /events/que_1").Uri.Query);
        JsonElement summary = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("failedAttemptsInspected").GetInt32());
        Assert.Equal("503", summary.GetProperty("topResponseCodes")[0].GetProperty("value").GetString());
        Assert.Equal(2, summary.GetProperty("topResponseCodes")[0].GetProperty("count").GetInt32());
        Assert.Contains("Queue que_1: locked (high)", human.Stdout);
        Assert.Contains("required: Fix the receiver, then resume.", human.Stdout);
        Assert.Contains("response codes: 503 ×2", human.Stdout);
    }

    // ── events search ───────────────────────────────────────────────────────

    [Fact]
    public async Task Events_search_filters_by_status_and_idempotency_key_and_names_the_statuses()
    {
        var api = new RecordingHandler(req => req.Key == "GET /events/que_1"
            ? Ok(new
            {
                items = new[] { new { publicId = "evt_1", status = 6, createdAtUtc = "2026-10-09T10:00:00Z", attemptCount = 3, deliveryKey = "order-10042", hasFailures = true } },
                page = 1, pageSize = 20, totalCount = 1,
            })
            : throw new InvalidOperationException(req.Key));

        CliRun json = await Run(api, "events", "search", "que_1", "--status", "dlq,failed", "--idempotency-key", "order-10042", "--from", "2026-10-09T00:00:00Z", "--json");
        CliRun human = await Run(api, "events", "search", "que_1", "--status", "dlq");

        Assert.True(json.Exit == ExitCodes.Success, json.Stdout + json.Stderr);
        string query = Uri.UnescapeDataString(api.Requests[0].Uri.Query);
        Assert.Contains("statuses=dlq&statuses=failed", query);
        Assert.Contains("deliveryKey=order-10042", query);
        Assert.Contains("createdFromUtc=2026-10-09T00:00:00.0000000Z", query);
        JsonElement item = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("items")[0];
        Assert.Equal("Dlq", item.GetProperty("status").GetString());
        Assert.Equal("order-10042", item.GetProperty("idempotencyKey").GetString());
        Assert.Contains("evt_1  Dlq  2026-10-09T10:00:00Z  3 attempt(s)  key order-10042", human.Stdout);
    }

    // ── resume ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_verifies_the_only_receiver_and_says_the_lock_is_lifted()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /queues/que_1/targets" => Ok(OneTarget),
            "POST /queues/que_1/targets/tgt_1/verify-and-resume" => Ok(new { ok = true, resumed = true, lockCleared = true, headEventPublicId = "evt_9", headEventDelivered = true }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await Run(api, "resume", "que_1", "--replay", "dlq");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("replay=dlq", api.Requests.Last().Uri.Query);
        Assert.Contains("Delivery to que_1 resumed: the receiver answered and the lock is lifted.", run.Stdout);
    }

    [Fact]
    public async Task A_resume_queuey_gives_to_a_person_exits_5_with_the_link()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "POST /queues/que_1/targets/tgt_1/verify-and-resume" => RecordingHandler.Json(HttpStatusCode.Accepted, new
            {
                operation = "op_1", status = "pending_approval", approvalUrl = "https://api.test/console/inbox/op_1?license=lic_1",
                expiresAt = "2026-10-10T10:00:00Z", policyRule = "v1.key.prod.releases.requires_approval",
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun json = await Run(api, "resume", "que_1", "--target", "tgt_1", "--json");
        CliRun human = await Run(api, "resume", "que_1", "--target", "tgt_1");

        Assert.Equal(ExitCodes.PendingApproval, json.Exit);
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal("pending_approval", root.GetProperty("status").GetString());
        Assert.Equal("https://api.test/console/inbox/op_1?license=lic_1", root.GetProperty("approvalUrl").GetString());
        Assert.Equal(ExitCodes.PendingApproval, human.Exit);
        Assert.Contains("They approve or reject it here: https://api.test/console/inbox/op_1?license=lic_1", human.Stdout);
    }

    [Fact]
    public async Task Approval_required_exits_5_with_the_console_page_queuey_names()
    {
        var api = new RecordingHandler(req => req.Key == "POST /queues/que_1/unlock"
            ? RecordingHandler.Json(HttpStatusCode.Forbidden, new
            {
                error = new
                {
                    code = "approval_required", message = "A key does not unlock a queue in a prod workspace.",
                    action = "A person unlocks it in the console.", consoleUrl = "https://api.test/console/t/ten_1/q/que_1",
                },
            })
            : throw new InvalidOperationException(req.Key));

        CliRun json = await Run(api, "unlock", "que_1", "--json");
        CliRun human = await Run(api, "unlock", "que_1");

        Assert.Equal(ExitCodes.PendingApproval, json.Exit);
        Assert.Equal("https://api.test/console/t/ten_1/q/que_1", JsonDocument.Parse(json.Stdout).RootElement.GetProperty("consoleUrl").GetString());
        Assert.Contains("In the console: https://api.test/console/t/ten_1/q/que_1", human.Stdout);
    }

    [Fact]
    public async Task Resume_on_a_queue_with_several_receivers_asks_which_and_sends_nothing()
    {
        var api = new RecordingHandler(req => req.Key == "GET /queues/que_1/targets"
            ? Ok(new[] { new { targetId = "tgt_1" }, new { targetId = "tgt_2" } })
            : throw new InvalidOperationException(req.Key));

        CliRun run = await Run(api, "resume", "que_1");

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--target", run.Stdout + run.Stderr);
        Assert.Single(api.Requests);
    }

    // ── unlock ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlock_lifts_the_lock_and_a_lock_that_remains_is_an_error()
    {
        var released = new RecordingHandler(req => req.Key == "POST /queues/que_1/unlock" ? RecordingHandler.NoContent() : throw new InvalidOperationException(req.Key));
        var remains = new RecordingHandler(req => req.Key == "POST /queues/que_1/unlock"
            ? RecordingHandler.Error(HttpStatusCode.Conflict, "lock_remains", "The receiver is still down, so the lock stays.")
            : throw new InvalidOperationException(req.Key));

        CliRun ok = await Run(released, "unlock", "que_1", "--json");
        CliRun refused = await Run(remains, "unlock", "que_1");

        Assert.True(ok.Exit == ExitCodes.Success, ok.Stdout + ok.Stderr);
        Assert.Equal("unlocked", JsonDocument.Parse(ok.Stdout).RootElement.GetProperty("status").GetString());
        Assert.Equal(ExitCodes.RuntimeError, refused.Exit);
        Assert.Contains("The receiver is still down, so the lock stays.", refused.Stderr);
    }

    // ── replay til mottakeren ───────────────────────────────────────────────

    [Fact]
    public async Task Replay_redeliver_sends_one_event_to_the_receiver_and_a_status_replay_sends_at_most_100_by_default()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "POST /events/que_1/evt_1/replay" => Ok(new { ok = true, eventPublicId = "evt_1", action = "replay" }),
            "POST /events/que_1/replay" => Ok(new { action = "replay", requested = 3, updated = 3, skipped = 0, items = Array.Empty<object>() }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun one = await Run(api, "replay", "evt_1", "--queue", "que_1", "--redeliver");
        CliRun many = await Run(api, "replay", "--queue", "que_1", "--status", "dlq", "--json");

        Assert.True(one.Exit == ExitCodes.Success, one.Stdout + one.Stderr);
        Assert.Contains("Queuey sends evt_1 to the receiver of que_1 again.", one.Stdout);
        Assert.True(many.Exit == ExitCodes.Success, many.Stdout + many.Stderr);
        JsonElement body = api.Requests.Last().Json;
        Assert.Equal("dlq", body.GetProperty("filter").GetProperty("statuses")[0].GetString());
        Assert.Equal(100, body.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task Replay_without_redeliver_still_goes_to_the_listener()
    {
        var api = new RecordingHandler(req => req.Key == "POST /queues/que_1/replay-to-listener/evt_1"
            ? Ok(new { delivered = true, listenerConnected = true, statusCode = 200, durationMs = 5 })
            : throw new InvalidOperationException(req.Key));

        CliRun run = await Run(api, "replay", "evt_1", "--queue", "que_1");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.DoesNotContain(api.Requests, r => r.Key.EndsWith("/evt_1/replay", StringComparison.Ordinal));
    }

    // ── security-review av #71 ──────────────────────────────────────────────

    [Theory]
    [InlineData("replay evt_1 --queue que_1 --dry-run")]                 // B1: ingen tørrkjøring for én hendelse
    [InlineData("replay evt_1 --queue que_1 --redeliver --dry-run")]
    [InlineData("replay evt_1 --queue que_1 --redeliver --max 5")]
    [InlineData("replay --queue que_1 --max 5")]                           // --max uten --status
    [InlineData("replay --queue que_1 --dry-run")]
    [InlineData("replay --queue que_1 --redeliver")]                       // --redeliver uten hendelse
    [InlineData("replay evt_1 --queue que_1 --status ,")]                  // R1: --status som blir tom
    [InlineData("replay evt_1 --queue que_1 --status ,,")]
    [InlineData("replay --queue que_1 --status ,")]
    [InlineData("replay ../x --queue que_1 --redeliver")]                  // K1
    [InlineData("replay evt_1/.. --queue que_1 --redeliver")]
    [InlineData("resume que_1 --target ..")]
    [InlineData("resume que_1 --target a/b")]
    public async Task A_replay_or_resume_that_could_reach_the_receiver_by_mistake_is_refused_and_sends_nothing(string command)
    {
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is sent: " + req.Key));

        CliRun run = await Run(api, command.Split(' '));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
    }

    // ── B2: mottakerens URL, redigert ───────────────────────────────────────

    private const string SecretUrl = "https://ops:hunter2@shop.test/hooks/s3cr3t-T0ken9/orders?sig=abc123";

    [Theory]
    [InlineData(SecretUrl, "https://shop.test/hooks/…/orders")]
    [InlineData("https://hooks.slack.com/services/T01/B02/xoxbSECRET", "https://hooks.slack.com/services/T01/B02/…")]
    [InlineData("not a url", "…")]
    public void A_receiver_url_keeps_its_host_and_plain_path_words_only(string url, string shown)
        => Assert.Equal(shown, TargetUrlRedaction.Redact(url));

    [Fact]
    public void A_url_in_text_and_a_refused_redirect_are_redacted()
    {
        Assert.Equal("POST https://shop.test/hooks/…/orders failed.", TargetUrlRedaction.RedactUrlsIn($"POST {SecretUrl} failed."));
        Assert.Equal("redirect_not_allowed: HTTP 302 → /login", TargetUrlRedaction.RedactUrlsIn("redirect_not_allowed: HTTP 302 → /login?session=abc123"));
    }

    [Fact]
    public async Task No_operator_answer_shows_a_receivers_secret()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /queues/que_1/metrics/snapshot" => Ok(new { processState = "Locked" }),
            "GET /queues/que_1/targets" => Ok(new[] { new { targetId = "tgt_1", targetName = "warehouse", state = "Down", effectiveUrl = SecretUrl } }),
            "GET /events/que_1/lanes" => Ok(new { counts = new { total = 0 } }),
            "GET /events/que_1/incident-report" => Ok(new
            {
                incidentType = "locked",
                lastFailedEvent = new { publicId = "evt_1", targetEndpoint = SecretUrl, responseCode = 503, errorMessage = $"POST {SecretUrl} answered 503" },
            }),
            "GET /events/que_1" => Ok(new { items = new[] { new { publicId = "evt_1" } } }),
            "GET /events/que_1/evt_1" => Ok(new { attempts = new[] { new { responseCode = 503, targetEndpoint = SecretUrl, errorMessage = $"POST {SecretUrl} answered 503" } } }),
            "POST /queues/que_1/targets/tgt_1/verify-and-resume" => Ok(new
            {
                ok = false, probe = new { success = false, statusCode = 503, targetUrl = SecretUrl, error = $"GET {SecretUrl}: 503" },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        var runs = new List<CliRun>();
        foreach (string[] command in new[]
        {
            new[] { "queue", "health", "que_1" }, new[] { "queue", "health", "que_1", "--json" },
            new[] { "diagnose", "que_1" }, new[] { "diagnose", "que_1", "--json" },
            new[] { "resume", "que_1", "--target", "tgt_1" }, new[] { "resume", "que_1", "--target", "tgt_1", "--json" },
        })
            runs.Add(await Run(api, command));

        string all = string.Concat(runs.Select(r => r.Stdout + r.Stderr));
        foreach (string secret in new[] { "hunter2", "s3cr3t", "sig=", "abc123" })
            Assert.DoesNotContain(secret, all);
        Assert.Contains("https://shop.test/hooks/…/orders", all);
    }

    [Fact]
    public async Task Events_get_shows_the_attempts_endpoint_redacted()
    {
        var api = new RecordingHandler(req => req.Key == "GET /events/que_1/evt_1"
            ? Ok(new
            {
                publicId = "evt_1", queuePublicId = "que_1", status = 4,
                attempts = new[] { new { attemptNumber = 1, responseCode = 503, targetEndpoint = SecretUrl, errorMessage = $"POST {SecretUrl} answered 503" } },
            })
            : throw new InvalidOperationException(req.Key));

        CliRun json = await Run(api, "events", "get", "evt_1", "--queue", "que_1", "--json");
        CliRun human = await Run(api, "events", "get", "evt_1", "--queue", "que_1");

        Assert.True(json.Exit == ExitCodes.Success, json.Stdout + json.Stderr);
        foreach (string secret in new[] { "hunter2", "s3cr3t", "sig=", "abc123" })
            Assert.DoesNotContain(secret, json.Stdout + human.Stdout + human.Stderr);
    }

    // ── B3: lenker fra svaret ───────────────────────────────────────────────

    [Theory]
    [InlineData("https://evil.test/console/inbox/op_1", true)]               // en annen vert
    [InlineData("http://api.test/console/inbox/op_1", true)]                 // http utenfor maskinen
    [InlineData("https://api.test/console/inbox/op_1", false)]
    public async Task An_approval_link_to_another_host_or_over_plain_http_is_withheld(string url, bool withheld)
    {
        var api = new RecordingHandler(req => req.Key == "POST /queues/que_1/unlock"
            ? RecordingHandler.Json(HttpStatusCode.Accepted, new { operation = "op_1", status = "pending_approval", approvalUrl = url })
            : throw new InvalidOperationException(req.Key));

        CliRun json = await Run(api, "unlock", "que_1", "--json");
        CliRun human = await Run(api, "unlock", "que_1");

        Assert.Equal(ExitCodes.PendingApproval, json.Exit);
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal(withheld ? null : url, root.GetProperty("approvalUrl").GetString());
        Assert.Equal(withheld, root.GetProperty("linkWithheld").ValueKind == JsonValueKind.String);
        Assert.Equal(withheld, human.Stdout.Contains("a link to another host", StringComparison.Ordinal));
        if (withheld)
            Assert.DoesNotContain(url, json.Stdout + human.Stdout);
    }

    [Fact]
    public async Task A_console_link_with_user_info_is_dropped_already_in_the_sdk()
    {
        var api = new RecordingHandler(req => req.Key == "POST /queues/que_1/unlock"
            ? RecordingHandler.Json(HttpStatusCode.Forbidden, new
            {
                error = new { code = "approval_required", message = "A person unlocks it.", consoleUrl = "https://api.test@evil.test/console" },
            })
            : throw new InvalidOperationException(req.Key));

        CliRun run = await Run(api, "unlock", "que_1", "--json");

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("consoleUrl").ValueKind);
        Assert.DoesNotContain("evil.test", run.Stdout + run.Stderr);
    }

    // ── B4, K3, K4 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Counts_that_are_not_numbers_show_as_a_question_mark_and_never_crash_or_reach_the_terminal()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /events/que_1" => Ok(new { items = new[] { new { publicId = "evt_1", status = 4, attemptCount = "\u001b[2J" } }, totalCount = "\u001b]0;x\u0007" }),
            "POST /events/que_1/replay" => Ok(new { requested = "\u001b[2J", updated = 2, skipped = "x", nextOlderThanEvent = "evt_0" }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun search = await Run(api, "events", "search", "que_1");
        CliRun searchJson = await Run(api, "events", "search", "que_1", "--json");
        CliRun replay = await Run(api, "replay", "--queue", "que_1", "--status", "dlq");

        Assert.True(search.Exit == ExitCodes.Success, search.Stdout + search.Stderr);
        Assert.True(searchJson.Exit == ExitCodes.Success, searchJson.Stdout + searchJson.Stderr);
        Assert.Contains("1 of ? event(s)", search.Stdout);
        Assert.Contains("? attempt(s)", search.Stdout);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(searchJson.Stdout).RootElement.GetProperty("totalCount").ValueKind);
        Assert.Contains("Sending 2 of ? event(s) again; ? skipped.", replay.Stdout);
        Assert.DoesNotContain('\u001b', search.Stdout + replay.Stdout);
        // K3: et døgnbudsjett, ingen oppfordring til å kjøre igjen i løkke.
        Assert.Contains("per day", replay.Stdout);
        Assert.DoesNotContain("run it again", replay.Stdout);
    }

    [Fact]
    public async Task The_json_says_where_the_workspace_came_from_for_replay_unlock_and_resume()
    {
        string dir = Path.Combine(Path.GetTempPath(), "queuey-operate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "queuey.deploy.json"), """{ "tenant": "ten_abc", "queues": {} }""");
        string before = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            var api = new RecordingHandler(req => req.Key switch
            {
                "GET /tenants/ten_abc/queues" => FlowAnswers.Queues(),
                "POST /events/que_orders/evt_1/replay" => Ok(new { ok = true }),
                "POST /queues/que_orders/unlock" => RecordingHandler.NoContent(),
                "POST /queues/que_orders/targets/tgt_1/verify-and-resume" => Ok(new { ok = true }),
                _ => throw new InvalidOperationException(req.Key),
            });
            string[][] commands =
            {
                new[] { "replay", "evt_1", "--queue", "orders", "--redeliver", "--json" },
                new[] { "unlock", "orders", "--json" },
                new[] { "resume", "orders", "--target", "tgt_1", "--json" },
            };
            foreach (string[] command in commands)
            {
                CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(command.Concat(new[]
                {
                    "--api-key", "qak_kid.secret", "--license", "lic_1", "--api-base", "https://api.test",
                    "--config", Path.Combine(dir, "none.json"),
                }).ToArray()), api);
                Assert.True(run.Exit == ExitCodes.Success, string.Join(' ', command) + run.Stdout + run.Stderr);
                Assert.Equal("queuey.deploy.json", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("workspaceFrom").GetString());
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task One_event_never_reaches_the_receiver_without_redeliver_even_with_a_blank_status()
    {
        // Security-review av #71 runde 2 (R1).
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is sent: " + req.Key));

        CliRun blank = await Run(api, "replay", "evt_1", "--queue", "que_1", "--status", " ");
        CliRun spaces = await Run(api, "replay", "evt_1", "--queue", "que_1", "--status", " , ");

        Assert.Equal(ExitCodes.Usage, blank.Exit);
        Assert.Equal(ExitCodes.Usage, spaces.Exit);
        Assert.Empty(api.Requests);
    }

    // ── runde 2: K-b, K-c, K-d, K-e ─────────────────────────────────────────

    private static RecordingHandler PendingUnlock(string url) => new(req => req.Key == "POST /queues/que_1/unlock"
        ? RecordingHandler.Json(HttpStatusCode.Accepted, new { operation = "op_1", status = "pending_approval", approvalUrl = url })
        : throw new InvalidOperationException(req.Key));

    [Fact]
    public async Task A_link_on_an_api_host_that_queuey_json_chose_is_withheld()
    {
        // K-b: queuey.json ligger i repoet, så verten den velger, gjør ikke en lenke trygg.
        string dir = Path.Combine(Path.GetTempPath(), "queuey-operate-link", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "queuey.json");
        File.WriteAllText(file, """{ "apiKey": "qak_kid.secret", "license": "lic_1", "apiBase": "https://api.test" }""");
        try
        {
            CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "unlock", "que_1", "--config", file, "--json" }),
                PendingUnlock("https://api.test/console/inbox/op_1"));

            Assert.Equal(ExitCodes.PendingApproval, run.Exit);
            JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("approvalUrl").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("linkWithheld").ValueKind);

            // Queueys egen konsollvert er alltid til å stole på.
            CliRun own = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "unlock", "que_1", "--config", file, "--json" }),
                PendingUnlock("https://app.queuey.ai/console/inbox/op_1"));
            Assert.Equal("https://app.queuey.ai/console/inbox/op_1", JsonDocument.Parse(own.Stdout).RootElement.GetProperty("approvalUrl").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Keys_mint_and_credentials_rotate_withhold_a_link_to_another_host()
    {
        // K-c.
        var mint = new RecordingHandler(req => req.Key == "POST /hmacclients/queues/que_1"
            ? RecordingHandler.Json(HttpStatusCode.Accepted, new { status = "pending_approval", approvalUrl = "https://evil.test/inbox/op_1", message = "A person approves it." })
            : throw new InvalidOperationException(req.Key));
        var rotate = new RecordingHandler(req => req.Key == "POST /tenants/ten_1/credentials/rotate"
            ? RecordingHandler.Json(HttpStatusCode.Accepted, new { status = "pending_approval", credentialRequest = "creq_1", approvalUrl = "https://evil.test/creq_1", message = "A person pastes it." })
            : throw new InvalidOperationException(req.Key));

        string folder = Directory.CreateTempSubdirectory("queuey-operate-mint-").FullName;
        CliRun minted = await Run(mint, "keys", "mint", "--queue", "que_1", "--write", Path.Combine(folder, ".env"), "--json");
        // --from-env leser prosessens miljø, som i CredentialsRotateTests.
        string variable = "QUEUEY_OPERATE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "whsec_new_value");
        CliRun rotated;
        try
        {
            rotated = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
                "credentials", "rotate", "--name", "stripe-whsec", "--from-env", variable, "--tenant", "ten_1", "--json")), rotate);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        foreach (CliRun run in new[] { minted, rotated })
        {
            Assert.True(run.Exit == ExitCodes.PendingApproval, run.Stdout + run.Stderr);
            JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("approvalUrl").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("linkWithheld").ValueKind);
            Assert.DoesNotContain("evil.test", run.Stdout + run.Stderr);
        }
    }

    [Fact]
    public async Task A_console_link_deep_in_an_answer_is_checked_too()
    {
        // K-d.
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /queues/que_1/metrics/snapshot" => Ok(new { processState = "Locked", help = new { consoleUrl = "https://evil.test/x" } }),
            "GET /queues/que_1/targets" => Ok(new[] { new { targetId = "tgt_1", consoleUrl = "https://api.test/console/q/que_1" } }),
            "GET /events/que_1/lanes" => Ok(new { counts = new { total = 0 } }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await Run(api, "queue", "health", "que_1", "--json");

        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        JsonElement help = root.GetProperty("snapshot").GetProperty("help");
        Assert.Equal(JsonValueKind.Null, help.GetProperty("consoleUrl").ValueKind);
        Assert.Equal(JsonValueKind.String, help.GetProperty("linkWithheld").ValueKind);
        Assert.Equal("https://api.test/console/q/que_1", root.GetProperty("targets")[0].GetProperty("consoleUrl").GetString());
        Assert.DoesNotContain("evil.test", run.Stdout);
    }

    [Fact]
    public async Task Diagnose_reads_only_event_ids_from_the_answer_as_path_segments()
    {
        // K-e.
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /events/que_1/incident-report" => Ok(new { incidentType = "degraded" }),
            "GET /events/que_1" => Ok(new { items = new[] { new { publicId = "../../admin" }, new { publicId = "evt_ok" } } }),
            "GET /events/que_1/evt_ok" => Ok(new { attempts = Array.Empty<object>() }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await Run(api, "diagnose", "que_1", "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(new[] { "GET /events/que_1/incident-report", "GET /events/que_1", "GET /events/que_1/evt_ok" }, api.Requests.Select(r => r.Key));
    }
}
