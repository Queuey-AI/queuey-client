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
        File.WriteAllText(path, """{ "tenant": "ten_abc", "queues": { "orders": { "maxAttempts": 5 } } }""");
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
