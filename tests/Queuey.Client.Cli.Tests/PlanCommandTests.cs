using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>`queuey plan` hele veien fra kommandolinjen, mot en server som svarer på dry-run.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class PlanCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-plan-cli-tests", Guid.NewGuid().ToString("N"));

    public PlanCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile()
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 } } }""");
        return path;
    }

    /// <summary>Planlegger alt, men svarer 204 på køens policy: som en server som utførte den.</summary>
    private static RecordingHandler AnswersPolicyWithNoContent() => new(req => req switch
    {
        { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        { Method.Method: "PUT", Path: "/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
        { Path: "/queues/que_orders/policy" } => RecordingHandler.NoContent(),
        _ => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
    });

    /// <summary>Planlegger alt: køens policy ville endret retention fra 7 til 5, slått av DLQ-en og satt en ventetid.</summary>
    private static RecordingHandler PlansEverything() => new(req => req switch
    {
        { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        { Method.Method: "PUT", Path: "/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
        { Path: "/queues/que_orders/policy" } => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            dryRun = true, target = "queue que_orders",
            changes = new object[]
            {
                new { path = "policy.retentionDays", from = 7, to = 5 },
                new { path = "policy.dlqEnabled", from = true, to = false },
                new { path = "policy.backoff", from = (object?)null, to = new { baseDelayMs = 1000, jitter = "full" } },
            },
            notes = Array.Empty<string>(),
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task Plan_as_json_is_a_versioned_object_with_a_step_per_write()
    {
        // Samme mønster som apply --dry-run --json (Kenneth valgte et versjonert objekt 2026-10-05): formen er ny med
        // queuey plan, så den har en versjon fra start, og et skript sjekker den først.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--json")), PlansEverything());

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        // planId, planHash og queues kom til i versjon 1 med Queuey F2.3 (2026-10-06), før noen tag hadde sluppet den, og
        // skipped med Queuey F2.4 samme dag.
        Assert.Equal(new[] { "schemaVersion", "file", "tenant", "planId", "planHash", "wouldSucceed", "changeCount", "queues", "steps", "skipped" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ten_abc", root.GetProperty("tenant").GetString());
        Assert.True(root.GetProperty("wouldSucceed").GetBoolean());
        Assert.Equal(3, root.GetProperty("changeCount").GetInt32());
        Assert.Matches("^sha256:[0-9a-f]{64}$", root.GetProperty("planHash").GetString());
        Assert.Equal("plan_" + root.GetProperty("planHash").GetString()!.Substring(7, 24), root.GetProperty("planId").GetString());

        JsonElement queue = Assert.Single(root.GetProperty("queues").EnumerateArray());
        Assert.Equal("orders", queue.GetProperty("name").GetString());
        Assert.Equal("que_orders", queue.GetProperty("publicId").GetString());
        Assert.EndsWith("/events/ten_abc/orders", queue.GetProperty("ingressUrl").GetString());

        // Køen finnes, så den eneste skrivingen som endrer noe, er policyen.
        JsonElement step = Assert.Single(root.GetProperty("steps").EnumerateArray());
        Assert.Equal(new[] { "target", "aspect", "creates", "changes", "notes", "state", "desired", "error" },
            step.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("queues.orders", step.GetProperty("target").GetString());
        Assert.Equal("policy", step.GetProperty("aspect").GetString());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("error").ValueKind);

        // Verdiene er typet som i serverens plan (review 2026-10-05): tall, sannhetsverdier og objekter som JSON, ikke tekst.
        JsonElement[] changes = step.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "policy.retentionDays", "policy.dlqEnabled", "policy.backoff" }, changes.Select(c => c.GetProperty("path").GetString()).ToArray());
        Assert.Equal(7, changes[0].GetProperty("from").GetInt32());
        Assert.Equal(5, changes[0].GetProperty("to").GetInt32());
        Assert.Equal(JsonValueKind.True, changes[1].GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.False, changes[1].GetProperty("to").ValueKind);
        Assert.Equal(JsonValueKind.Null, changes[2].GetProperty("from").ValueKind);
        Assert.Equal(1000, changes[2].GetProperty("to").GetProperty("baseDelayMs").GetInt32());

        // Uten --json står de som en linje hver, med planens id og hash og køens ingress-URL først.
        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile())), PlansEverything());
        Assert.Contains(root.GetProperty("planId").GetString() + "  " + root.GetProperty("planHash").GetString(), human.Stdout);
        Assert.Contains("orders\tingress " + queue.GetProperty("ingressUrl").GetString(), human.Stdout);
        Assert.Contains("~ policy.retentionDays: 7 → 5", human.Stdout);
        Assert.Contains("~ policy.dlqEnabled: true → false", human.Stdout);
        Assert.Contains("~ policy.backoff: (none) → {\"baseDelayMs\":1000,\"jitter\":\"full\"}", human.Stdout);
    }

    [Fact]
    public async Task A_2xx_that_is_not_a_plan_ends_the_plan_with_an_error_instead_of_a_crash()
    {
        // Review 2026-09-24: en 204 etter proben kastet JsonException ut av CLI-en (exit 134, stacktrace).
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile())), AnswersPolicyWithNoContent());

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("Queuey error: Queuey answered the dry run of queues.orders · policy with something that is not a plan", run.Stderr);
        Assert.DoesNotContain("   at ", run.Stderr);

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--json")), AnswersPolicyWithNoContent());

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        Assert.Equal("dry_run_ignored", JsonDocument.Parse(json.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
