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

    /// <summary>Planlegger alt, og køens policy ville endret retention fra 7 til 5.</summary>
    private static RecordingHandler PlansEverything() => new(req => req switch
    {
        { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        { Method.Method: "PUT", Path: "/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
        { Path: "/queues/que_orders/policy" } => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            dryRun = true, target = "queue que_orders",
            changes = new[] { new { path = "policy.retentionDays", from = 7, to = 5 } }, notes = Array.Empty<string>(),
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
        Assert.Equal(new[] { "schemaVersion", "file", "tenant", "wouldSucceed", "changeCount", "steps" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ten_abc", root.GetProperty("tenant").GetString());
        Assert.True(root.GetProperty("wouldSucceed").GetBoolean());
        Assert.Equal(1, root.GetProperty("changeCount").GetInt32());

        // Køen finnes, så den eneste skrivingen som endrer noe, er policyen.
        JsonElement step = Assert.Single(root.GetProperty("steps").EnumerateArray());
        Assert.Equal(new[] { "target", "aspect", "creates", "changes", "notes", "error" },
            step.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("queues.orders", step.GetProperty("target").GetString());
        Assert.Equal("policy", step.GetProperty("aspect").GetString());
        JsonElement change = Assert.Single(step.GetProperty("changes").EnumerateArray());
        Assert.Equal("policy.retentionDays", change.GetProperty("path").GetString());
        Assert.Equal("7", change.GetProperty("from").GetString());
        Assert.Equal("5", change.GetProperty("to").GetString());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("error").ValueKind);
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
