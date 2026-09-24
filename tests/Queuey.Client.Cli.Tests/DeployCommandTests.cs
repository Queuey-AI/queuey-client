using System.Net;
using System.Text.Json;
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
    public async Task A_dry_run_shows_mode_retry_and_filter()
    {
        string path = DeployFile("""
        { "queues": {
            "orders": { "mode": "deliver", "maxAttempts": 5, "backoff": { "jitter": "full" },
                        "filter": { "conditions": [ { "field": "type", "op": "eq", "value": "order.created" } ] } },
            "audit": {} } }
        """);

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("orders\tmode=deliver inherits the workspace maxAttempts=5 backoff.jitter=full filter=(all: type eq order.created)", run.Stdout);
        Assert.Contains("audit\tmode=(deliver when it has a destination, if new)", run.Stdout);
    }

    [Fact]
    public async Task A_dry_run_as_json_carries_the_mode_and_the_filter()
    {
        string path = DeployFile("""{ "queues": { "orders": { "mode": "logOnly", "filter": { "match": "any", "conditions": [] } } } }""");

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement plan = JsonDocument.Parse(run.Stdout).RootElement[0];
        Assert.Equal("logOnly", plan.GetProperty("mode").GetString());
        Assert.Equal("any", plan.GetProperty("policy").GetProperty("filter").GetProperty("match").GetString());
    }

    [Fact]
    public async Task A_dry_run_fails_on_a_mode_the_file_cannot_set()
    {
        string path = DeployFile("""{ "queues": { "orders": { "mode": "paused" } } }""");

        var ex = await Assert.ThrowsAsync<Queuey.Client.QueueyConfigurationException>(
            () => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));
        Assert.Contains("not a mode a deployment file sets", ex.Message);
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
