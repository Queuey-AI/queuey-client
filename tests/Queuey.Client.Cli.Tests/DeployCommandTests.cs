using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Cli;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Tests;

/// <summary>Tester som bytter Console.Out, kjøres ikke samtidig — utskriften ville blandes.</summary>
[CollectionDefinition(ConsoleCollection.Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>
/// CLI-siden av ønsket tilstand (2026-09-23): skjemaet, dry-run som viser modus, retry og filter,
/// og verify som krever at du velger hva mottakeren får.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class DeployCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-deploy-cli-tests", Guid.NewGuid().ToString("N"));

    public DeployCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile(string json)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Schema_prints_the_json_schema_without_credentials()
    {
        CliRun run = await CliHarness.RunAsync(() => Task.FromResult(SchemaCommand.Run(Array.Empty<string>())));

        Assert.Equal(ExitCodes.Success, run.Exit);
        using JsonDocument doc = JsonDocument.Parse(run.Stdout);
        Assert.Equal(DeploymentFile.SchemaUrl, doc.RootElement.GetProperty("$id").GetString());
        Assert.Contains("logOnly", run.Stdout);
    }

    [Fact]
    public async Task A_dry_run_shows_mode_backoff_and_filter()
    {
        string path = DeployFile("""
        { "queues": {
            "orders": { "mode": "deliver", "backoff": { "baseDelayMs": 2000, "jitter": "full" },
                        "filter": { "conditions": [ { "field": "type", "op": "eq", "value": "order.created" } ] } },
            "audit": {} } }
        """);

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("orders\tmode=deliver inherits the workspace backoff.baseDelayMs=2000 backoff.jitter=full filter=(all: type eq order.created)", run.Stdout);
        Assert.Contains("audit\tmode=(deliver when it has a destination, if new)", run.Stdout);
    }

    [Fact]
    public async Task A_dry_run_as_json_carries_the_mode_and_the_filter()
    {
        string path = DeployFile("""{ "queues": { "orders": { "mode": "logOnly", "filter": { "match": "any", "conditions": [] } } } }""");

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;

        // Versjon 2 (Kenneth, 2026-10-05): et objekt med workspacet og køene. Versjon 1 var lista fra preview.8.
        Assert.Equal(new[] { "schemaVersion", "workspace", "queues" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("workspace").ValueKind);   // fila har ikke noe workspace

        JsonElement plan = Assert.Single(root.GetProperty("queues").EnumerateArray());
        Assert.Equal(new[] { "name", "mode", "policy", "delivery", "ingress", "notes" }, plan.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("orders", plan.GetProperty("name").GetString());
        Assert.Equal("logOnly", plan.GetProperty("mode").GetString());
        Assert.Equal("any", plan.GetProperty("policy").GetProperty("filter").GetProperty("match").GetString());
    }

    [Fact]
    public async Task A_dry_run_as_json_carries_delivery_and_ingress_field_for_field_as_the_file_declares_them()
    {
        // Sjekket mot serverens plan 2026-10-05: der er ingress.eventType { from, name }. Dry-run skrev bare navnet, og
        // timeoutMs, signing, rateLimit, authHeaderName, method og successStatusCode manglet, så en fil som satte dem,
        // så ut som en som lot dem stå.
        const string workspaceDelivery = """
            { "baseUrl": "https://hooks.example.com", "authMode": "ApiKey", "credentialRef": "partner-key", "authHeaderName": "X-Api-Key",
              "method": "PUT", "timeoutMs": 15000, "signing": { "enabled": true, "credentialRef": "signing-key", "templateKey": "queuey" },
              "rateLimit": { "maxRequests": 10, "perSeconds": 1 } }
            """;
        const string workspaceIngress = """
            { "authMode": "ApiKey", "eventType": { "from": "body", "name": "type" }, "groupKey": { "from": "query", "name": "customer" },
              "successStatusCode": 200 }
            """;
        const string queueDelivery = """
            { "url": "/orders", "inherit": false, "authMode": "Bearer", "credentialRef": "orders-token", "authHeaderName": "Authorization",
              "timeoutMs": 5000, "signing": { "enabled": false, "credentialRef": null, "templateKey": null },
              "rateLimit": { "maxRequests": 5, "perSeconds": 60 } }
            """;
        const string queueIngress = """
            { "authMode": "None", "eventType": { "from": "header", "name": "X-Event" }, "groupKey": null, "successStatusCode": null }
            """;
        string path = DeployFile($$"""
            { "workspace": { "ingress": {{workspaceIngress}}, "delivery": {{workspaceDelivery}} },
              "queues": { "orders": { "ingress": {{queueIngress}}, "delivery": {{queueDelivery}} } } }
            """);

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonNode root = JsonNode.Parse(run.Stdout)!;
        AssertSameJson(workspaceDelivery, root["workspace"]!["delivery"]);
        AssertSameJson(workspaceIngress, root["workspace"]!["ingress"]);
        AssertSameJson(queueDelivery, root["queues"]![0]!["delivery"]);
        AssertSameJson(queueIngress, root["queues"]![0]!["ingress"]);

        static void AssertSameJson(string declared, JsonNode? printed)
            => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(declared), printed),
                $"Declared {declared.Trim()}, but the dry run printed {printed?.ToJsonString()}.");
    }

    [Fact]
    public async Task A_dry_run_shows_a_variable_as_written_in_json_and_in_text()
    {
        // Review 2026-10-05: --json skrev de utvidede verdiene, også et token i en ?code=, mens teksten viste fila.
        Environment.SetEnvironmentVariable("QUEUEY_TEST_HOOK_TOKEN", "s3cr3t-token");
        try
        {
            string path = DeployFile("""
                { "workspace": { "delivery": { "baseUrl": "https://hooks.example.com/in?code=${QUEUEY_TEST_HOOK_TOKEN}" } },
                  "queues": { "orders": { "delivery": { "url": "https://other.example.com/orders?code=${QUEUEY_TEST_HOOK_TOKEN}" } } } }
                """);

            CliRun json = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));
            CliRun text = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));

            Assert.Equal(ExitCodes.Success, json.Exit);
            Assert.Equal(ExitCodes.Success, text.Exit);
            JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
            Assert.Equal("https://hooks.example.com/in?code=${QUEUEY_TEST_HOOK_TOKEN}",
                root.GetProperty("workspace").GetProperty("delivery").GetProperty("baseUrl").GetString());
            Assert.Equal("https://other.example.com/orders?code=${QUEUEY_TEST_HOOK_TOKEN}",
                root.GetProperty("queues")[0].GetProperty("delivery").GetProperty("url").GetString());
            Assert.DoesNotContain("s3cr3t-token", json.Stdout + text.Stdout);
            Assert.Contains("${QUEUEY_TEST_HOOK_TOKEN}", text.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUEUEY_TEST_HOOK_TOKEN", null);
        }
    }

    [Fact]
    public async Task A_dry_run_still_fails_on_a_variable_that_is_not_set()
    {
        string path = DeployFile("""{ "workspace": { "delivery": { "baseUrl": "${QUEUEY_TEST_NOT_SET_ANYWHERE}" } } }""");

        var ex = await Assert.ThrowsAsync<Queuey.Client.QueueyConfigurationException>(
            () => CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" })));

        Assert.Contains("QUEUEY_TEST_NOT_SET_ANYWHERE", ex.Message);
    }

    [Fact]
    public async Task A_dry_run_fails_on_a_mode_the_file_cannot_set()
    {
        string path = DeployFile("""{ "queues": { "orders": { "mode": "paused" } } }""");

        var ex = await Assert.ThrowsAsync<Queuey.Client.QueueyConfigurationException>(
            () => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));
        Assert.Contains("not a mode a deployment file sets", ex.Message);
    }

    // ── antall forsøk er ikke en innstilling (Queuey#391, 2026-10-04) ───────

    [Theory]
    [InlineData("apply", null)]
    [InlineData("plan", null)]
    [InlineData("apply", "--dry-run")]
    public async Task A_file_that_still_declares_attempts_is_refused_before_anything_is_sent(string command, string? flag)
    {
        // En fil skrevet for hånd, eller med en build av #40 fra før dette, kan ha forsøkene. Apply ville fått 400 på
        // første policy-patch, etter at workspacet var skrevet. Nå avvises fila før noe er sendt, med hva som skal bort.
        string path = DeployFile("""{ "tenant": "ten_abc", "queues": { "orders": { "maxAttempts": 8, "dlqAfterAttempts": 6 } } }""");
        var api = new RecordingHandler(_ => RecordingHandler.Error(HttpStatusCode.InternalServerError, "unexpected", "Nothing should reach the server."));
        string[] args = new[] { command, "--file", path, "--json" }.Concat(flag is null ? Array.Empty<string>() : new[] { flag }).ToArray();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(args)), api);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Empty(api.Requests);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("config_error", error.GetProperty("code").GetString());
        Assert.StartsWith($"{path}: The deployment file declares queues.orders.maxAttempts, queues.orders.dlqAfterAttempts, "
                          + "but the number of attempts is not a setting", error.GetProperty("message").GetString());
        Assert.Equal("Remove maxAttempts and dlqAfterAttempts from the file. backoff and filter stay as they are.",
            error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Without_json_the_attempts_refusal_says_what_to_remove_on_stderr()
    {
        string path = DeployFile("""{ "tenant": "ten_abc", "workspace": { "maxAttempts": 8 }, "queues": {} }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path)));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains($"Config error: {path}: The deployment file declares workspace.maxAttempts, but the number of attempts is not a setting.", run.Stderr);
        Assert.Contains("→ Remove maxAttempts and dlqAfterAttempts from the file.", run.Stderr);
    }

    [Fact]
    public async Task Pull_never_writes_attempts_and_apply_accepts_what_it_wrote()
    {
        // Et API fra før Queuey#391 sender fortsatt maxAttempts og dlqAfterAttempts i /config. Pull leser dem ikke,
        // så fila den skriver, kan apply-es mot det nye API-et, som avviser en patch med dem. Filteret er gyldig, så
        // pull advarer ikke.
        object workspacePolicy = new
        {
            idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo", maxAttempts = 8, dlqAfterAttempts = 6,
            backoff = new { baseDelayMs = 1000, maxDelayMs = 60000, jitter = "full" },
        };
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/config" => RecordingHandler.Json(HttpStatusCode.OK, new { policy = workspacePolicy }),
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            "GET /queues/que_orders/config" => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                policy = new
                {
                    idempotent = false, dlqEnabled = true, retentionDays = 30, ordering = "fifo", maxAttempts = 3, dlqAfterAttempts = 2,
                    backoff = new { baseDelayMs = 1000, maxDelayMs = 60000, jitter = "full" },
                },
                inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = false },
                tenantBaseline = new { policy = workspacePolicy },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });
        string path = Path.Combine(_dir, "pulled.deploy.json");

        CliRun pull = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("pull", "--file", path, "--tenant", "ten_abc")), api);

        Assert.Equal(ExitCodes.Success, pull.Exit);
        Assert.Equal(string.Empty, pull.Stderr);
        string written = File.ReadAllText(path);
        Assert.DoesNotContain("attempts", written, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(30, JsonDocument.Parse(written).RootElement.GetProperty("queues").GetProperty("orders").GetProperty("retentionDays").GetInt32());

        CliRun dryRun = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", path, "--dry-run" }));

        Assert.Equal(ExitCodes.Success, dryRun.Exit);
        Assert.Contains("1 queue(s) declared. Nothing was sent.", dryRun.Stdout);
    }

    // ── filter og ventetid (review 2026-10-05) ──────────────────────────────

    [Theory]
    [InlineData("""{ "conditions": null }""", "A filter needs its conditions. To remove the filter, write \"conditions\": [].")]
    [InlineData("""{ "conditions": [ null ] }""", "A filter condition cannot be null.")]
    public async Task A_filter_without_usable_conditions_is_a_config_error_and_not_a_crash(string filter, string expected)
    {
        // "conditions": null og [null] ga NullReferenceException, stack trace og exit 134, uten JSON.
        string path = DeployFile($$"""{ "tenant": "ten_abc", "queues": { "orders": { "filter": {{filter}} } } }""");
        var api = new RecordingHandler(_ => RecordingHandler.Error(HttpStatusCode.InternalServerError, "unexpected", "Nothing should reach the server."));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--json")), api);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Equal(string.Empty, run.Stderr);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("config_error", error.GetProperty("code").GetString());
        Assert.Contains(expected, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Pull_warns_about_each_filter_condition_apply_will_refuse()
    {
        // Rå overrides eller en deploy fra før Queuey#391 kan ha lagret "gt": "1,000" og exists med en verdi. Pull
        // skriver dem som de står, men sier fra per betingelse: apply, plan og --check avviser fila til de er rettet.
        object policy = new
        {
            idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo",
            filter = new
            {
                match = "all",
                conditions = new object[]
                {
                    new { field = "amount", op = "gt", value = "1,000" },
                    new { field = "type", op = "eq", value = "order.created" },
                    new { field = "priority", op = "exists", value = "false" },
                },
            },
        };
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/config" => RecordingHandler.Json(HttpStatusCode.OK, new { policy = new { idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo" } }),
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            "GET /queues/que_orders/config" => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                policy,
                inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = false },
                tenantBaseline = new { policy = new { idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo" } },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun pull = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("pull", "--stdout", "--tenant", "ten_abc")), api);

        Assert.Equal(ExitCodes.Success, pull.Exit);
        Assert.Contains("\"value\": \"1,000\"", pull.Stdout);   // fila viser det Queuey har
        string[] warnings = pull.Stderr.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "Warning: queues.orders.filter.conditions[0] (amount gt 1,000): Filter condition 'amount gt' compares numbers, and '1,000' is not a number. "
                + "Write it like 1.5: a point for decimals, and no thousands separators, currency or parentheses.",
            "Warning: queues.orders.filter.conditions[2] (priority exists false): Filter condition 'priority exists' takes no value: it matches every "
                + "event that has the field, whatever the value. Leave the value out.",
            "apply, plan and apply --check refuse the file until these are fixed in it.",
        }, warnings);
    }

    [Fact]
    public async Task A_dry_run_notes_a_wait_above_the_ceiling()
    {
        // En dry run sender ingenting, så den kan ikke vite om ventetiden allerede gjelder. Den sier hva apply gjør.
        string path = DeployFile("""
        { "workspace": { "backoff": { "maxDelayMs": 172800000 } },
          "queues": { "orders": { "backoff": { "baseDelayMs": 7200000 } }, "audit": { "backoff": { "baseDelayMs": 1000 } } } }
        """);

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        string stdout = run.Stdout.Replace("\r\n", "\n");
        Assert.Contains("\n    ! backoff.maxDelayMs=172800000 is above the 86400000 (24 hours) the longest wait may be: apply refuses it unless that wait is already in place.", stdout);
        Assert.Contains("orders\tmode=(deliver when it has a destination, if new) inherits the workspace backoff.baseDelayMs=7200000\n"
                        + "    ! backoff.baseDelayMs=7200000 is above the 3600000 (one hour) a first wait may be: apply refuses it unless that wait is already in place.", stdout);
        Assert.Equal(2, stdout.Split("    ! ").Length - 1);   // ingen merknad for audit

        CliRun json = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));

        // Merknaden om workspacet har sin egen plass (re-review 2026-10-05: det manglet), og køene står i fila sin
        // rekkefølge.
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        JsonElement[] queues = root.GetProperty("queues").EnumerateArray().ToArray();
        Assert.Equal(new[] { "orders", "audit" }, queues.Select(q => q.GetProperty("name").GetString()).ToArray());
        Assert.Contains("apply refuses it unless that wait is already in place", Assert.Single(queues[0].GetProperty("notes").EnumerateArray()).GetString());
        Assert.Empty(queues[1].GetProperty("notes").EnumerateArray());

        JsonElement workspace = root.GetProperty("workspace");
        Assert.Equal(new[] { "environment", "policy", "delivery", "ingress", "notes" }, workspace.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(172800000, workspace.GetProperty("policy").GetProperty("backoff").GetProperty("maxDelayMs").GetInt32());
        Assert.StartsWith("backoff.maxDelayMs=172800000 is above the 86400000 (24 hours)", Assert.Single(workspace.GetProperty("notes").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Verify_needs_a_queue_and_the_data_to_send()
    {
        CliRun noQueue = await CliHarness.RunAsync(() => VerifyCommand.RunAsync(Array.Empty<string>()));
        Assert.Equal(ExitCodes.Usage, noQueue.Exit);
        Assert.Contains("verify requires <queue>", noQueue.Stderr);

        // Ingen standard-payload: eventen går til den ekte mottakeren.
        CliRun noData = await CliHarness.RunAsync(() => VerifyCommand.RunAsync(new[] { "orders" }));
        Assert.Equal(ExitCodes.Usage, noData.Exit);
        Assert.Contains("treats as harmless", noData.Stderr);

        CliRun badTimeout = await CliHarness.RunAsync(() => VerifyCommand.RunAsync(new[] { "orders", "--data", "{}", "--timeout", "0" }));
        Assert.Equal(ExitCodes.Usage, badTimeout.Exit);
        Assert.Contains("--timeout takes whole seconds", badTimeout.Stderr);
    }

    /// <summary>En server som nekter å liste køene, med en foreslått handling.</summary>
    private static RecordingHandler Refusing() => new(req => req.Key switch
    {
        "GET /tenants/ten_abc/queues" => RecordingHandler.Error(HttpStatusCode.Forbidden, "missing_permission",
            "This key cannot read the workspace's queues.", "Use a key made with the Build profile."),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task With_json_an_error_is_json_on_stdout_with_its_action()
    {
        // Gap 5 fra gap-analysen: feil ble skrevet som prosa på stderr også med --json. Hele veien, fra
        // kommandolinjen til exit-koden, og stderr er tom — en agent som leser stdout, får bare JSON.
        string path = DeployFile("""{ "tenant": "ten_abc", "queues": { "orders": {} } }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--json")), Refusing());

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal(string.Empty, run.Stderr);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("missing_permission", error.GetProperty("code").GetString());
        Assert.Equal("Use a key made with the Build profile.", error.GetProperty("action").GetString());
        Assert.Equal(403, error.GetProperty("status").GetInt32());
    }

    // ── Miljø-merket (Queuey F2.2, 2026-10-05) ──────────────────────────────

    /// <summary>En server der workspacet er prod, så en nøkkel som setter dev, nektes slik Queuey nekter den.</summary>
    private static RecordingHandler ProdWorkspace() => new(req => req.Key switch
    {
        "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        "PATCH /tenants/ten_abc" => RecordingHandler.Error(HttpStatusCode.Forbidden, "environment_lowering_needs_a_person",
            "Only a person can lower a workspace's environment, and this would lower workspace ten_abc from prod to dev. "
            + "An API key can set the environment when it creates a workspace, and can raise it towards prod.",
            "Ask a person to change it on the workspace's page in the Queuey console: https://app.queuey.ai/console/t/ten_abc"),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task A_key_that_would_lower_the_environment_is_told_why_and_what_a_person_does_and_nothing_else_is_written()
    {
        string path = DeployFile("""{ "tenant": "ten_abc", "workspace": { "environment": "dev", "retentionDays": 7 }, "queues": { "orders": {} } }""");
        RecordingHandler api = ProdWorkspace();

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path)), api);

        Assert.Equal(ExitCodes.RuntimeError, human.Exit);
        Assert.Contains("Queuey error: Only a person can lower a workspace's environment, and this would lower workspace ten_abc from prod to dev.", human.Stderr);
        Assert.Contains("→ Ask a person to change it on the workspace's page in the Queuey console: https://app.queuey.ai/console/t/ten_abc", human.Stderr);
        Assert.Equal(new[] { "PATCH /tenants/ten_abc" }, api.Writes.Select(w => w.Key).ToArray());
        Assert.Equal("dev", api.Writes.Single().Json.GetProperty("environment").GetString());

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--json")), ProdWorkspace());

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        JsonElement error = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("error");
        Assert.Equal("environment_lowering_needs_a_person", error.GetProperty("code").GetString());
        Assert.Equal(403, error.GetProperty("status").GetInt32());
        Assert.Contains("Queuey console", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_dry_run_shows_the_environment_as_the_file_writes_it_and_checks_it_once_expanded()
    {
        // Utvidelsen leser prosessens miljø, så variabelen har en standardverdi her i stedet for å settes.
        string path = DeployFile("""{ "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT:-staging}", "retentionDays": 7 }, "queues": {} }""");

        CliRun text = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));
        CliRun json = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));

        Assert.Equal(ExitCodes.Success, text.Exit);
        Assert.Contains("workspace\tenvironment=${QUEUEY_WORKSPACE_ENVIRONMENT:-staging} retentionDays=7", text.Stdout);
        Assert.Equal("${QUEUEY_WORKSPACE_ENVIRONMENT:-staging}",
            JsonDocument.Parse(json.Stdout).RootElement.GetProperty("workspace").GetProperty("environment").GetString());

        string wrong = DeployFile("""{ "workspace": { "environment": "${QUEUEY_F22_NEVER_SET:-qa}" }, "queues": {} }""");
        CliRun refused = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", wrong, "--dry-run" }));

        Assert.Equal(ExitCodes.Configuration, refused.Exit);
        Assert.Contains("workspace.environment must be one of dev, test, staging, prod; got 'qa'.", refused.Stderr);
    }

    [Fact]
    public async Task Without_json_the_action_follows_the_message_on_stderr()
    {
        string path = DeployFile("""{ "tenant": "ten_abc", "queues": { "orders": {} } }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path)), Refusing());

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains("Queuey error: This key cannot read the workspace's queues.", run.Stderr);
        Assert.Contains("→ Use a key made with the Build profile.", run.Stderr);
    }

    [Fact]
    public async Task Apply_reports_a_queue_it_created_before_failing_as_created_with_the_mode_it_got()
    {
        // Review 2026-09-24: feilresultatet mistet at køen var opprettet, så ingen så at den lå i logOnly.
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = true }),
            "PATCH /queues/que_orders/policy" => RecordingHandler.Error(HttpStatusCode.BadRequest, "retention_cap_exceeded", "Your plan keeps events for at most 7 days."),
            _ => throw new InvalidOperationException(req.Key),
        });
        string path = DeployFile("""{ "queues": { "orders": { "retentionDays": 3650 } } }""");

        CliRun human = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(CliHarness.With("--file", path, "--tenant", "ten_abc")), api);

        Assert.Equal(ExitCodes.RuntimeError, human.Exit);
        Assert.Contains("✗ orders\tque_orders\tcreated, logOnly — 400 retention_cap_exceeded", human.Stdout);
        Assert.Contains("will not start delivering by itself", human.Stdout);
        Assert.Contains("0 applied (1 created), 1 failed", human.Stdout);

        CliRun json = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(CliHarness.With("--file", path, "--tenant", "ten_abc", "--json")), api);

        JsonElement orders = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("queues")[0];
        Assert.True(orders.GetProperty("created").GetBoolean());
        Assert.Equal("que_orders", orders.GetProperty("publicId").GetString());
        Assert.Equal("logOnly", orders.GetProperty("mode").GetString());
        Assert.False(orders.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Apply_shows_the_servers_suggested_action_under_a_failed_queue()
    {
        // Review 2026-09-24: bare feil som stoppet hele kommandoen, viste forslaget; en kø som feilet i
        // en vanlig apply, mistet det — både i teksten og i JSON.
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
            "PATCH /queues/que_orders/policy" => RecordingHandler.Error(HttpStatusCode.BadRequest, "retention_cap_exceeded",
                "Your plan keeps events for at most 7 days.", "Declare 7 or fewer, or upgrade the plan."),
            _ => throw new InvalidOperationException(req.Key),
        });
        string path = DeployFile("""{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 3650 } } }""");

        CliRun human = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(CliHarness.With("--file", path)), api);

        Assert.Equal(ExitCodes.RuntimeError, human.Exit);
        Assert.Contains("✗ orders\t400 retention_cap_exceeded Your plan keeps events for at most 7 days.\n      → Declare 7 or fewer, or upgrade the plan.",
            human.Stdout.Replace("\r\n", "\n"));

        CliRun json = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(CliHarness.With("--file", path, "--json")), api);

        JsonElement orders = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("queues")[0];
        Assert.Equal("Declare 7 or fewer, or upgrade the plan.", orders.GetProperty("action").GetString());
        Assert.Equal("retention_cap_exceeded", orders.GetProperty("errorCode").GetString());
        Assert.Equal(400, orders.GetProperty("status").GetInt32());
    }

    [Fact]
    public void The_tenant_verify_uses_is_the_one_the_file_names()
    {
        // Samme workspace som apply skrev til; bare tenant ekspanderes, så en annen ${VAR} som
        // mangler i skallet der verify kjøres, spiller ingen rolle.
        DeploymentFile file = DeploymentFile.Parse("""
        { "tenant": "${QUEUEY_TENANT_FOR_TEST}", "queues": { "orders": { "delivery": { "url": "${UNSET_IN_THIS_SHELL}" } } } }
        """);

        Assert.Equal("ten_file", file.ResolveTenant(name => name == "QUEUEY_TENANT_FOR_TEST" ? "ten_file" : null));
    }
}
