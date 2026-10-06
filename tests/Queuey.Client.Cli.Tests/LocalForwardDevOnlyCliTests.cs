using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Queuey (vedtatt av Kenneth 2026-10-06): bare et workspace merket dev videresender til en lytter, og Queuey nekter
/// <c>localForward</c> ellers med <c>local_forward_needs_dev_workspace</c> og veien ut. <c>queuey plan</c> sier det før apply,
/// også for en kø den ville laget, og <c>queuey apply</c> viser avslaget under køen. Teksten er Queueys, så den går gjennom
/// <see cref="TerminalText"/>.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class LocalForwardDevOnlyCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-lf-dev-cli-tests", Guid.NewGuid().ToString("N"));

    public LocalForwardDevOnlyCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile()
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "tenant": "ten_abc", "queues": { "stripe": { "delivery": { "kind": "localForward" } } } }""");
        return path;
    }

    // Veien ut med en ESC-sekvens, som en server kunne sendt: den skal ikke nå terminalen.
    private const string Action = "Mark the workspace dev in the Queuey console.\u001b]0;owned\u0007";

    private static HttpResponseMessage Refused()
        => RecordingHandler.Error(HttpStatusCode.Conflict, "local_forward_needs_dev_workspace",
            "This would route the queue to a local listener (queuey listen), and only a workspace marked dev forwards to one: "
            + "workspace ten_abc is marked prod. Nothing was saved.", Action);

    [Fact]
    public async Task Plan_says_before_apply_that_a_queue_it_would_create_cannot_forward_outside_a_dev_workspace()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/config" => RecordingHandler.Json(HttpStatusCode.OK, new { environment = "prod" }),
            "PATCH /tenants/ten_abc/policy" => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
            "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = (string?)null, displayName = "stripe", created = true, hasDeliveryTarget = false }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile())), api);
        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, human.Exit);
        Assert.Contains("✗ local_forward_needs_dev_workspace: Queue 'stripe' would be created with \"kind\": \"localForward\", and only "
                        + "a workspace marked dev forwards to a local listener (queuey listen): workspace ten_abc is marked prod.", human.Stdout);
        Assert.Contains("→ Give the queue \"kind\": \"http\" where the workspace is not dev", human.Stdout);

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        JsonElement error = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("steps")[0].GetProperty("error");
        Assert.Equal("local_forward_needs_dev_workspace", error.GetProperty("code").GetString());
        Assert.Equal(409, error.GetProperty("status").GetInt32());
        Assert.Contains("${QUEUEY_STRIPE_DELIVERY_KIND}", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Plan_shows_queueys_refusal_for_a_queue_that_exists_with_the_way_out_and_nothing_a_terminal_would_run()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_stripe", displayName = "stripe", mode = "Deliver", hasDeliveryTarget = true } }),
            "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_stripe", displayName = "stripe", created = false, hasDeliveryTarget = true }),
            "PATCH /tenants/ten_abc/policy" => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
            "PATCH /queues/que_stripe/local-forward" => Refused(),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile())), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("✗ local_forward_needs_dev_workspace: This would route the queue to a local listener", run.Stdout);
        Assert.Contains("→ Mark the workspace dev in the Queuey console.", run.Stdout);
        Assert.DoesNotContain("\u001b", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_shows_queueys_refusal_under_the_queue_with_the_way_out_and_nothing_a_terminal_would_run()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_stripe", displayName = "stripe", mode = "Deliver", hasDeliveryTarget = true } }),
            "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_stripe", displayName = "stripe", created = false, hasDeliveryTarget = true }),
            "PATCH /queues/que_stripe/local-forward" => Refused(),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(CliHarness.With("--file", DeployFile())), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        string stdout = run.Stdout.Replace("\r\n", "\n");
        Assert.Contains("✗ stripe\t409 local_forward_needs_dev_workspace This would route the queue to a local listener", stdout);
        Assert.Contains("\n      → Mark the workspace dev in the Queuey console.", stdout);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
    }
}
