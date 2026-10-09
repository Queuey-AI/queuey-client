using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Det gullflyten 2026-10-09 måtte rundt: et dev-workspace fra CLI-en (create-tenant --environment, og apply som lager det når
/// fila sier miljøet), plan som peker dit i stedet for til en person i konsollet, en manglende credential i leveringen som et
/// avslag med exit 1, og whoami som ikke kaller localhost Production.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class AgentFlowFrictionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-agent-flow-tests", Guid.NewGuid().ToString("N"), "stripe-express");

    public AgentFlowFrictionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    private string DeployFile(string json)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// En Queuey som lager workspacet ten_new, med miljøet den fikk når <paramref name="keepsEnvironment"/>, ellers uten, som en
    /// Queuey fra før F2.2. Applyen til ten_new går gjennom.
    /// </summary>
    private static RecordingHandler Creates(bool keepsEnvironment = true) => new(req => req.Key switch
    {
        "POST /tenants" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "ten_new",
            displayName = req.Json.GetProperty("displayName").GetString(),
            status = "Active",
            kind = "Standard",
            queues = Array.Empty<object>(),
            environment = keepsEnvironment && req.Json.TryGetProperty("environment", out JsonElement env) ? env.GetString() : null,
        }),
        "GET /tenants/ten_new/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        "PATCH /tenants/ten_new" => RecordingHandler.NoContent(),
        _ => throw new InvalidOperationException(req.Key),
    });

    // ── create-tenant --environment ──────────────────────────────────────────

    [Fact]
    public async Task Create_tenant_marks_the_new_workspace_with_the_environment_it_is_given()
    {
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "create-tenant", "--name", "Golden A", "--environment", "Dev", "--json")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement body = Assert.Single(api.Requests).Json;
        Assert.Equal("dev", body.GetProperty("environment").GetString());
        Assert.Equal("Golden A", body.GetProperty("displayName").GetString());

        JsonElement tenant = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("ten_new", tenant.GetProperty("publicId").GetString());
        Assert.Equal("dev", tenant.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task Create_tenant_without_an_environment_sends_none_as_before()
    {
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("create-tenant", "--name", "Golden A")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.False(Assert.Single(api.Requests).Json.TryGetProperty("environment", out _));
        Assert.Equal("Created tenant ten_new (Golden A, kind=Standard)", run.Stdout.Trim());
    }

    [Fact]
    public async Task Create_tenant_refuses_an_environment_queuey_does_not_take_before_it_sends_anything()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "create-tenant", "--name", "Golden A", "--environment", "production")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        // Verdien vises bare i den trygge formen (CliErrors.Shown), siden den kan være en hemmelighet limt inn på feil plass.
        Assert.Contains("--environment takes one of dev, test, staging, prod; got '", run.Stderr);
    }

    [Fact]
    public async Task A_queuey_that_ignores_the_environment_is_named_with_the_workspace_it_made()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "create-tenant", "--name", "Golden A", "--environment", "dev", "--json")), Creates(keepsEnvironment: false));

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("environment_not_set", error.GetProperty("code").GetString());
        Assert.Equal("Queuey created workspace ten_new but did not mark it dev: it is unmarked, which counts as prod. This Queuey does "
                     + "not take an environment when it creates a workspace.", error.GetProperty("message").GetString());
    }

    // ── apply lager workspacet når ingenting navngir et ─────────────────────

    [Fact]
    public async Task Apply_creates_a_workspace_marked_as_the_file_says_when_none_is_named_and_applies_to_it()
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[] { "POST /tenants", "GET /tenants/ten_new/queues", "PATCH /tenants/ten_new" }, api.Requests.Select(r => r.Key));
        JsonElement created = api.Requests[0].Json;
        Assert.Equal("dev", created.GetProperty("environment").GetString());
        // Et nøytralt navn, aldri mappens (review av #65, K3).
        Assert.Equal("queuey-dev", created.GetProperty("displayName").GetString());

        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("ten_new", root.GetProperty("tenant").GetString());
        JsonElement workspace = root.GetProperty("createdWorkspace");
        Assert.Equal("ten_new", workspace.GetProperty("publicId").GetString());
        Assert.Equal("dev", workspace.GetProperty("environment").GetString());
        Assert.Equal("--tenant ten_new", workspace.GetProperty("tenantOption").GetString());

        Assert.Contains($"No workspace is named, and {path} says environment dev: created workspace ten_new (queuey-dev, dev).", run.Stderr);
        Assert.Contains($"→ Name it, or the next apply creates another: \"tenant\": \"ten_new\" in {path}, tenant in the profile, or --tenant ten_new.", run.Stderr);
    }

    [Fact]
    public async Task Apply_never_creates_a_workspace_when_one_is_named()
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PATCH /tenants/ten_abc" => RecordingHandler.NoContent(),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "apply", "--file", path, "--no-git", "--tenant", "ten_abc", "--json")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain(api.Requests, r => r.Key == "POST /tenants");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("createdWorkspace").ValueKind);
    }

    [Fact]
    public async Task Apply_without_a_workspace_or_an_environment_still_asks_for_the_workspace()
    {
        string path = DeployFile("""{ "queues": {} }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("A workspace (ten_…) is required", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Apply_stops_before_it_writes_when_queuey_made_the_workspace_without_the_environment()
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");
        RecordingHandler api = Creates(keepsEnvironment: false);

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal(new[] { "POST /tenants" }, api.Requests.Select(r => r.Key));
        Assert.Equal("environment_not_set", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Når apply ikke lager workspacet (review av #65, M1 og B1) ───────────

    private static void NothingCreated(RecordingHandler? api, CliRun run, string because)
    {
        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.True(api is null || !api.Requests.Any(), "nothing may be sent");
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.True(error.GetProperty("message").GetString()!.StartsWith("A workspace (ten_…) is required for this call, and none is set, and ", StringComparison.Ordinal),
            error.GetProperty("message").GetString());
        Assert.Contains(because, error.GetProperty("message").GetString());
        Assert.Contains("`queuey create-tenant --name queuey-", error.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("CI", "true")]
    [InlineData("GITHUB_ACTIONS", "true")]
    public async Task Apply_never_creates_a_workspace_in_ci(string variable, string value)
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api,
            new Dictionary<string, string> { [variable] = value });

        NothingCreated(api, run, $"apply does not create a workspace in CI ({variable} is set)");
        Assert.Contains("--environment dev`", run.Stdout);
    }

    [Fact]
    public async Task Apply_in_ci_with_the_environment_variable_missing_creates_nothing()
    {
        // README-mønsteret for flere miljøer, i en CI-jobb der variabelen ikke er satt: ingen tenant, og miljøet fra en variabel.
        string path = DeployFile("""{ "workspace": { "environment": "${FRICTION_TEST_UNSET_ENVIRONMENT}" }, "queues": {} }""");
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api,
            new Dictionary<string, string> { ["CI"] = "true" });

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains("${FRICTION_TEST_UNSET_ENVIRONMENT}", run.Stdout);
        Assert.Contains("is not set", run.Stdout);
    }

    [Fact]
    public async Task Apply_in_ci_with_the_environment_defaulting_to_dev_creates_nothing_either()
    {
        string path = DeployFile("""{ "workspace": { "environment": "${FRICTION_TEST_UNSET_ENVIRONMENT:-dev}" }, "queues": {} }""");
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api,
            new Dictionary<string, string> { ["CI"] = "true" });

        NothingCreated(api, run, "apply does not create a workspace in CI (CI is set)");
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("staging")]
    public async Task Apply_creates_a_workspace_only_for_dev_or_test(string environment)
    {
        string path = DeployFile($$"""{ "workspace": { "environment": "{{environment}}" }, "queues": {} }""");
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api);

        NothingCreated(api, run, $"apply creates a workspace only for dev or test, and {path} says {environment}");
    }

    [Fact]
    public async Task Apply_refuses_before_it_creates_a_workspace_whose_delivery_would_need_a_credential_it_cannot_have_yet()
    {
        string path = DeployFile("""
        { "workspace": { "environment": "dev" },
          "queues": { "orders": { "delivery": { "url": "https://hooks.example.com/orders", "authMode": "Bearer", "credentialRef": "orders-api-token" } } } }
        """);
        RecordingHandler api = Creates();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api);

        NothingCreated(api, run, "queues.orders.delivery.credentialRef names a credential, which a new workspace does not have yet");
        Assert.Contains("then store the credential in it with `queuey credentials set --tenant ten_… …`",
            JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("action").GetString());
    }

    [Fact]
    public async Task An_apply_that_fails_after_creating_the_workspace_names_it_in_the_error()
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");
        var api = new RecordingHandler(req => req.Key switch
        {
            "POST /tenants" => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "ten_new", displayName = "queuey-dev", environment = "dev" }),
            "GET /tenants/ten_new/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PATCH /tenants/ten_new" => RecordingHandler.Error(HttpStatusCode.Forbidden, "forbidden", "The key may not write the workspace.", "Use another key."),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        JsonElement error = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("error");
        Assert.Equal("forbidden", error.GetProperty("code").GetString());
        Assert.Equal("Use another key. This apply created workspace ten_new: name it with --tenant ten_new, or the next apply creates another.",
            error.GetProperty("action").GetString());
        Assert.Equal("--tenant ten_new", error.GetProperty("createdWorkspace").GetProperty("tenantOption").GetString());
        Assert.Equal("ten_new", error.GetProperty("createdWorkspace").GetProperty("publicId").GetString());
    }

    [Fact]
    public async Task Plan_in_ci_without_a_workspace_gives_the_missing_workspace_error_with_the_way_out()
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", path, "--json")), null,
            new Dictionary<string, string> { ["CI"] = "true" });

        NothingCreated(null, run, "apply does not create a workspace in CI (CI is set)");
    }

    // ── Hintene (review av #65, K4) ─────────────────────────────────────────

    [Fact]
    public async Task Create_tenant_with_env_is_pointed_at_environment()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("create-tenant", "--name", "x", "--env", "dev")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--environment dev|test|staging|prod", run.Stderr);
        Assert.DoesNotContain("built-in environment", run.Stderr);
    }

    [Theory]
    [InlineData("VER_1")]
    [InlineData("ver-1")]
    public async Task Verify_wait_with_a_misspelled_id_says_it_takes_seconds_or_an_id(string value)
    {
        CliRun run = await CliHarness.RunAsync(() => VerifyCommand.RunAsync(CliHarness.With("que_orders", "--event", "evt_1", "--wait", value)));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--wait takes whole seconds, at least 1, or a verification id (ver_…, lower case)", run.Stderr);
    }

    // ── plan ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_without_a_workspace_says_apply_creates_it_from_the_files_environment()
    {
        string path = DeployFile("""{ "workspace": { "environment": "dev" }, "queues": {} }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", path, "--json")));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Contains("says environment dev, so `queuey apply` creates a workspace marked dev first", error.GetProperty("message").GetString());
        Assert.Contains("`queuey create-tenant --name queuey-dev --environment dev`", error.GetProperty("action").GetString());
        Assert.DoesNotContain("console", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Plan_refuses_a_delivery_credential_the_workspace_lacks_with_exit_1_and_says_how_to_store_it()
    {
        // Gullflyten 2026-10-09, funn 9: på ingressen er et manglende navn et notat (Queuey godtar det, og apply går gjennom), men
        // på leveringen stoppet planen med exit 3, koden for nøkkel- og vertsfeil. Leveringen kan ikke sendes uten credentialen,
        // så det er et avslag: exit 1, med koden credentials rotate også bruker.
        string path = DeployFile("""
        { "tenant": "ten_abc", "workspace": { "environment": "dev" },
          "queues": { "orders": { "delivery": { "url": "https://hooks.example.com/orders", "authMode": "Bearer", "credentialRef": "orders-api-token" } } } }
        """);
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", path, "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("credential_not_found", error.GetProperty("code").GetString());
        Assert.Contains("No credential named 'orders-api-token' in workspace ten_abc (queues.orders.delivery.credentialRef)", error.GetProperty("message").GetString());
        Assert.Contains("queuey credentials set --tenant ten_abc --name orders-api-token --type BearerToken", error.GetProperty("message").GetString());
        Assert.Empty(api.Writes);
    }

    // ── whoami ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, "Production")]
    [InlineData("http://localhost:5223", "http://127.0.0.1:54185", "Local")]
    [InlineData("https://api.test", "https://ingress.test", "Custom")]
    [InlineData("http://localhost:5223", "https://ingress.queuey.ai", "Custom")]
    public async Task Whoami_calls_the_hosts_what_they_are(string? apiBase, string? ingressBase, string expected)
    {
        var args = new List<string> { "whoami", "--json", "--config", Path.Combine(_dir, "no-queuey.json") };
        if (apiBase is not null) args.AddRange(new[] { "--api-base", apiBase });
        if (ingressBase is not null) args.AddRange(new[] { "--ingress-base", ingressBase });

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(args.ToArray()));
        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(args.Where(a => a != "--json").ToArray()));

        Assert.Equal(ExitCodes.Success, json.Exit);
        // «hosts», ikke «environment»: workspacet har sitt eget miljø (review av #65, K1).
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal(expected, root.GetProperty("hosts").GetString());
        Assert.False(root.TryGetProperty("environment", out _));
        Assert.Contains($"  Hosts       : {expected}", human.Stdout);
    }
}
