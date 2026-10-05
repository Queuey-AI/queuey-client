using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>`queuey verify` fra kommandolinjen: JSON-kontrakten og hva --file tar imot (review 2026-10-05).</summary>
[Collection(ConsoleCollection.Name)]
public sealed class VerifyCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-verify-cli-tests", Guid.NewGuid().ToString("N"));

    public VerifyCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>En kø som finnes, en lesing som er lov, en publisering og en event som feilet med 401.</summary>
    private static RecordingHandler RejectingReceiver() => new(req => req switch
    {
        { Method.Method: "GET", Path: "/tenants/ten_abc/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        { Method.Method: "GET", Path: "/events/que_orders" } => RecordingHandler.Json(HttpStatusCode.OK, new { items = Array.Empty<object>() }),
        { Method.Method: "POST", Path: "/events/ten_abc/orders" } => RecordingHandler.Json(HttpStatusCode.Accepted, new
        {
            queuePublicId = "que_orders", eventId = "evt_1", receivedAtUtc = DateTimeOffset.UnixEpoch, mode = "Deliver", replayed = false,
        }),
        { Method.Method: "GET", Path: "/events/que_orders/evt_1" } => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "evt_1", status = 4, attemptCount = 1,
            attempts = new[]
            {
                new
                {
                    attemptNumber = 1, targetEndpoint = "https://hooks.example.com/orders", status = 2, responseCode = 401,
                    failureClass = "AuthenticationFailed", decisionKind = "HoldEvent", decisionReason = "target_requires_action_auth_failed",
                },
            },
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task Verify_as_json_is_a_versioned_object_with_the_action_to_take()
    {
        // Ny kontrakt med verify: schemaVersion først, og «action» som i alle andre utskrifter, ikke «suggestedAction».
        CliRun run = await CliHarness.RunAsync(
            () => VerifyCommand.RunAsync(CliHarness.With("orders", "--data", "{}", "--json", "--tenant", "ten_abc")), RejectingReceiver());

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("schemaVersion", root.EnumerateObject().First().Name);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("failed", root.GetProperty("verdict").GetString());
        Assert.Contains("a person resumes the queue in the Queuey console", root.GetProperty("action").GetString());
        Assert.False(root.TryGetProperty("suggestedAction", out _));
    }

    [Fact]
    public async Task A_deployment_file_given_as_the_event_is_refused_and_nothing_is_sent()
    {
        // apply og plan tar deployment-fila med --file, verify tar eventen. Et feil valg sendte fila til den ekte mottakeren.
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "$schema": "x", "workspace": { "retentionDays": 7 }, "queues": { "orders": {} } }""");

        CliRun run = await CliHarness.RunAsync(() => VerifyCommand.RunAsync(CliHarness.With("orders", "--file", path, "--tenant", "ten_abc")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains($"--file is the event to send, and {path} is a deployment file.", run.Stderr);
        Assert.Contains("--deployment", run.Stderr);
    }

    [Theory]
    [InlineData("""{ "type": "order.created", "queues": ["a"] }""")]
    [InlineData("""[ { "queues": {} } ]""")]
    [InlineData("""not json""")]
    public void An_event_that_only_mentions_queues_is_not_a_deployment_file(string payload)
    {
        Assert.False(VerifyCommand.IsDeploymentFile(System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}
