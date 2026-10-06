using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey verify` mot Queuey sin flytverifisering (F2.6, 2026-10-06): POST /queues/{que}/verifications, så GET til den er
/// avgjort. Hva hver modus sender, exit-kodene, en Queuey uten endepunktet, og at utskriften bare har det klienten kjenner.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class VerifyCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-verify-cli-tests", Guid.NewGuid().ToString("N"));

    public VerifyCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// En Queuey med flytverifisering: kølista til ten_abc, starten (201 med <paramref name="started"/>) og lesingene, ett svar
    /// fra <paramref name="reads"/> per lesing, og det siste om igjen.
    /// </summary>
    private static RecordingHandler Server(object started, params object[] reads)
    {
        int read = 0;
        return new RecordingHandler(req => req switch
        {
            { Method.Method: "GET", Path: "/tenants/ten_abc/queues" } => FlowAnswers.Queues(),
            { Method.Method: "POST", Path: "/queues/que_orders/verifications" } => RecordingHandler.Json(HttpStatusCode.Created, started),
            { Method.Method: "GET", Path: "/queues/que_orders/verifications/ver_1" }
                => RecordingHandler.Json(HttpStatusCode.OK, reads[Math.Min(read++, reads.Length - 1)]),
            _ => throw new InvalidOperationException(req.Key),
        });
    }

    private static Task<int> Verify(params string[] args) => VerifyCommand.RunAsync(CliHarness.With(args));

    private static string[] Keys(RecordingHandler api) => api.Requests.Select(r => r.Key).ToArray();

    private static string[] Names(JsonElement json) => json.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task An_event_is_followed_by_its_id_and_read_until_Queuey_settles_it()
    {
        RecordingHandler api = Server(
            FlowAnswers.Verification("pending", settled: false),
            FlowAnswers.Verification("pending", settled: false),
            FlowAnswers.Verification("passed"));

        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--event", "evt_1", "--timeout", "30", "--tenant", "ten_abc"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[]
        {
            "GET /tenants/ten_abc/queues",
            "POST /queues/que_orders/verifications",
            "GET /queues/que_orders/verifications/ver_1",
            "GET /queues/que_orders/verifications/ver_1",
        }, Keys(api));

        JsonElement body = api.Requests[1].Json;
        Assert.Equal(new[] { "eventPublicId", "timeoutSeconds" }, Names(body));
        Assert.Equal("evt_1", body.GetProperty("eventPublicId").GetString());
        Assert.Equal(30, body.GetProperty("timeoutSeconds").GetInt32());
        Assert.All(api.Requests.Skip(2), r => Assert.Equal("?waitSeconds=20", r.Uri.Query));

        Assert.Equal("Following evt_1 on orders (que_orders) for up to 60 s.", run.Stderr.Trim());
        string[] lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("✓ Passed — orders (que_orders) in ten_abc, event evt_1", lines[0]);
        Assert.Equal("  Event evt_1 went from the ingress to Delivered: the receiver answered 200.", lines[1]);
        Assert.Equal("  ✓ receiver_response   passed  statusCode=200 durationMs=38", lines[8]);
        Assert.Equal("  – ingress_auth        skipped  reason=not_recorded", lines[3]);
        Assert.Equal("  Verification ver_1.", lines[^1]);
    }

    [Fact]
    public async Task A_session_waits_for_the_event_type_and_the_ingress_template_and_says_to_trigger_it()
    {
        object waiting = FlowAnswers.Verification("pending", settled: false, mode: "observed_session", eventId: null,
            expectations: new { eventType = "payment_intent.succeeded", ingressAuth = "stripe" });
        RecordingHandler api = Server(waiting, FlowAnswers.Verification("passed", mode: "observed_session"));

        // Med køens id slås ingenting opp ved navn, og workspacet trengs ikke.
        CliRun run = await CliHarness.RunAsync(
            () => Verify("que_orders", "--event-type", "payment_intent.succeeded", "--ingress-auth", "stripe"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[] { "POST /queues/que_orders/verifications", "GET /queues/que_orders/verifications/ver_1" }, Keys(api));

        JsonElement body = api.Requests[0].Json;
        Assert.Equal(new[] { "eventType", "ingressAuth" }, Names(body));
        Assert.Equal("payment_intent.succeeded", body.GetProperty("eventType").GetString());
        Assert.Equal("stripe", body.GetProperty("ingressAuth").GetString());

        Assert.Equal("Waiting up to 60 s for the next 'payment_intent.succeeded' event on que_orders, verified with stripe. " +
                     "Trigger it now: an event that arrived before this does not count.", run.Stderr.Trim());
        Assert.StartsWith("✓ Passed — que_orders in ten_abc, event evt_1", run.Stdout);
    }

    [Fact]
    public async Task Send_posts_the_test_event_as_a_json_value_with_its_type()
    {
        RecordingHandler api = Server(FlowAnswers.Verification("passed", mode: "active", eventId: "evt_9",
            expectations: new { eventType = "order.created", sentWith = "none" }));

        CliRun run = await CliHarness.RunAsync(() => Verify(
            "orders", "--send", "--data", """{ "orderId": "A-1", "lines": [1, 2] }""", "--event-type", "order.created",
            "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(string.Empty, run.Stderr);

        JsonElement body = api.Requests.Single(r => r.Method == HttpMethod.Post).Json;
        Assert.Equal(new[] { "eventType", "send", "payload" }, Names(body));
        Assert.True(body.GetProperty("send").GetBoolean());
        Assert.Equal("order.created", body.GetProperty("eventType").GetString());
        JsonElement payload = body.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("A-1", payload.GetProperty("orderId").GetString());
        Assert.Equal(2, payload.GetProperty("lines").GetArrayLength());

        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(new[] { "schemaVersion", "tenant", "queue", "queuePublicId", "verification" }, Names(root));
        Assert.Equal(VerifyCommand.JsonSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, VerifyCommand.JsonSchemaVersion);
        Assert.Equal("ten_abc", root.GetProperty("tenant").GetString());
        Assert.Equal("orders", root.GetProperty("queue").GetString());
        Assert.Equal("que_orders", root.GetProperty("queuePublicId").GetString());

        JsonElement verification = root.GetProperty("verification");
        Assert.Equal(1, verification.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ver_1", verification.GetProperty("verificationId").GetString());
        Assert.Equal("active", verification.GetProperty("mode").GetString());
        Assert.Equal("passed", verification.GetProperty("outcome").GetString());
        Assert.True(verification.GetProperty("settled").GetBoolean());
        Assert.Equal("evt_9", verification.GetProperty("subject").GetProperty("eventPublicId").GetString());
        Assert.Equal(new[] { "eventType", "sentWith" }, Names(verification.GetProperty("expectations")));
        Assert.Equal(8, verification.GetProperty("steps").GetArrayLength());

        // Evidensen har bare feltene Queuey satte: ingen null-felt for resten.
        Assert.Equal(new[] { "reason" }, Names(verification.GetProperty("steps")[1].GetProperty("evidence")));
        Assert.Equal(new[] { "statusCode", "durationMs" }, Names(verification.GetProperty("steps")[6].GetProperty("evidence")));
        Assert.False(verification.TryGetProperty("passed", out _));
    }

    [Theory]
    [InlineData("failed", "✗ Failed")]
    [InlineData("timed_out", "✗ Timed out")]
    [InlineData("not_tried", "✗ Not tried")]
    [InlineData("inconclusive", "✗ inconclusive")]   // et utfall klienten ikke kjenner, vises som det er
    public async Task Only_passed_exits_0_and_any_other_outcome_shows_queueys_summary(string outcome, string headline)
    {
        RecordingHandler api = Server(FlowAnswers.Verification(outcome, summary: $"Queuey says {outcome}."));

        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--event", "evt_1", "--tenant", "ten_abc"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.StartsWith(headline + " — orders (que_orders) in ten_abc", run.Stdout);
        Assert.Contains($"  Queuey says {outcome}.", run.Stdout);

        CliRun json = await CliHarness.RunAsync(() => Verify("orders", "--event", "evt_1", "--tenant", "ten_abc", "--json"),
            Server(FlowAnswers.Verification(outcome)));
        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        Assert.Equal(outcome, JsonDocument.Parse(json.Stdout).RootElement.GetProperty("verification").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_refusal_exits_1_with_queueys_code_message_and_action()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "POST /queues/que_orders/verifications" => RecordingHandler.Error(HttpStatusCode.Forbidden, "production_workspace",
                "Queuey sends no test event to a production workspace.", "Verify the producer's own event with --event-type instead."),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "verify", "que_orders", "--send", "--data", "{}", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("production_workspace", error.GetProperty("code").GetString());
        Assert.Equal("Queuey sends no test event to a production workspace.", error.GetProperty("message").GetString());
        Assert.Equal("Verify the producer's own event with --event-type instead.", error.GetProperty("action").GetString());
        Assert.Equal(403, error.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Without_flow_verification_Queuey_says_what_is_missing_and_nothing_else_is_tried()
    {
        // En Queuey fra før F2.6: ruten finnes ikke, og svaret er 404 uten kropp. Køen finnes. Ingen fallback: verify publiserer
        // ikke selv, slik den gjorde før.
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => FlowAnswers.Queues(),
            "POST /queues/que_orders/verifications" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "GET /queues/que_orders" => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders" }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "verify", "orders", "--send", "--data", "{}", "--tenant", "ten_abc", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("flow_verification_unavailable", error.GetProperty("code").GetString());
        Assert.Equal("This Queuey cannot verify flows: it answered 404 to POST /queues/que_orders/verifications, the endpoint " +
                     "verify uses. Nothing was sent.", error.GetProperty("message").GetString());
        Assert.Contains("--api-base", error.GetProperty("action").GetString());
        Assert.Equal(new[] { "GET /tenants/ten_abc/queues", "POST /queues/que_orders/verifications", "GET /queues/que_orders" }, Keys(api));

        // Uten --json: meldingen og handlingen på stderr.
        CliRun prose = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "verify", "orders", "--event", "evt_1", "--tenant", "ten_abc")), new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => FlowAnswers.Queues(),
            "POST /queues/que_orders/verifications" => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            _ => throw new InvalidOperationException(req.Key),
        }));
        Assert.Equal(ExitCodes.RuntimeError, prose.Exit);
        Assert.Contains("This Queuey cannot verify flows: it answered 405", prose.Stderr);
    }

    [Fact]
    public async Task A_404_for_a_queue_that_is_not_there_says_that_and_not_that_verification_is_missing()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "POST /queues/que_gone/verifications" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "GET /queues/que_gone" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("verify", "que_gone", "--event", "evt_1", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("queue_not_found", error.GetProperty("code").GetString());
        Assert.Equal("Queue que_gone was not found.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_queue_name_the_workspace_does_not_have_is_not_found_and_not_shown()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_abc/queues" => FlowAnswers.Queues(),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "verify", "ordrs-FAKEsecret", "--event", "evt_1", "--tenant", "ten_abc", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("queue_not_found", error.GetProperty("code").GetString());
        Assert.Equal("Workspace ten_abc has no queue by that name.", error.GetProperty("message").GetString());
        Assert.DoesNotContain("FAKE", run.Stdout);
        Assert.Equal(new[] { "GET /tenants/ten_abc/queues" }, Keys(api));
    }

    [Fact]
    public async Task An_answer_in_a_shape_this_client_does_not_read_is_not_shown_as_an_outcome()
    {
        RecordingHandler api = Server(FlowAnswers.Verification("passed", schemaVersion: 2));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "verify", "orders", "--event", "evt_1", "--tenant", "ten_abc", "--json")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("verification_schema_unsupported", error.GetProperty("code").GetString());
        Assert.Contains("in shape 2, and this client reads shape 1", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_output_has_no_payload_value_secret_or_field_the_client_does_not_know()
    {
        // Queuey sin evidens har aldri payload-verdier eller hemmeligheter. Klienten viser i tillegg bare feltene den kjenner,
        // så et felt en framtidig eller feil server legger til, når aldri utskriften.
        Dictionary<string, object?> answer = FlowAnswers.Verification("passed", mode: "active", steps: new object[]
        {
            new { name = "ingress_reached", status = "passed", at = FlowAnswers.Created, evidence = new { eventId = "evt_1", payloadExcerpt = "LEAK-1" } },
            new { name = "final_state", status = "passed", at = FlowAnswers.Created, evidence = new { status = "Delivered" }, debug = "LEAK-2" },
        });
        answer["internalNote"] = "LEAK-3";
        answer["subject"] = new { workspacePublicId = "ten_abc", queuePublicId = "que_orders", eventPublicId = "evt_1", eventReceivedAt = FlowAnswers.Created, payload = "LEAK-4" };

        foreach (bool json in new[] { false, true })
        {
            string[] args = { "orders", "--send", "--data", """{ "card": "4242-PAYLOAD" }""", "--tenant", "ten_abc" };
            CliRun run = await CliHarness.RunAsync(() => Verify(json ? args.Append("--json").ToArray() : args), Server(answer));

            Assert.Equal(ExitCodes.Success, run.Exit);
            string output = run.Stdout + run.Stderr;
            Assert.DoesNotContain("LEAK", output);
            Assert.DoesNotContain("PAYLOAD", output);
            Assert.DoesNotContain("qak_kid.secret", output);
            Assert.DoesNotContain(".secret", output);
        }
    }

    // Over Queuey sin grense på 64 KB: 70 008 byte som JSON.
    private static readonly string TooLarge = "{\"x\":\"" + new string('a', 70_000) + "\"}";

    public static TheoryData<string[], string> UsageErrors => new()
    {
        { new[] { "orders" }, "missing_argument" },
        { new[] { "orders", "--data", "{}" }, "send_required" },
        { new[] { "orders", "--event-type", "x", "--file", "{event}" }, "send_required" },
        { new[] { "orders", "--send" }, "missing_body" },
        { new[] { "orders", "--send", "--data", "{}", "--stdin" }, "conflicting_options" },
        { new[] { "orders", "--send", "--data", "not json" }, "invalid_value" },
        { new[] { "orders", "--send", "--data", TooLarge }, "invalid_value" },
        { new[] { "orders", "--send", "--data", "{}", "--ingress-auth", "stripe" }, "conflicting_options" },
        { new[] { "orders", "--event", "evt_1", "--event-type", "x" }, "conflicting_options" },
        { new[] { "orders", "--event", "evt_1", "--send", "--data", "{}" }, "conflicting_options" },
        { new[] { "orders", "--event", "qak_kid.FAKEsecret" }, "invalid_value" },
        { new[] { "orders", "--event" }, "missing_value" },
        { new[] { "orders", "--event-type" }, "missing_value" },
        { new[] { "orders", "--ingress-auth", "stripe" }, "missing_argument" },
        { new[] { "orders", "--event", "evt_1", "--timeout", "0" }, "invalid_value" },
        { new[] { "orders", "--event", "evt_1", "--timeout", "soon" }, "invalid_value" },
        { new[] { "orders", "--event", "evt_1", "--timeout", "5", "--wait", "5" }, "conflicting_options" },
        { new[] { "orders", "--event", "evt_1", "--content-type", "text/plain" }, "unknown_option" },
    };

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task A_command_line_that_cannot_be_a_verification_is_a_usage_error_and_nothing_is_sent(string[] args, string code)
    {
        string eventFile = Path.Combine(_dir, "event.json");
        File.WriteAllText(eventFile, """{ "test": true }""");
        var api = new RecordingHandler(req => throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(
            () => Verify(args.Select(a => a == "{event}" ? eventFile : a).Append("--json").ToArray()), api);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Empty(api.Requests);
        Assert.DoesNotContain("FAKE", run.Stdout);
    }

    [Fact]
    public async Task A_test_event_without_send_points_to_the_producers_own_event_and_says_when_send_works()
    {
        // Review av #51: --send virker ikke i prod-Queuey, i et workspace som regnes som prod, eller med en ingress som tar
        // nøkkel eller signatur. Feilen peker på veiene som virker overalt.
        var api = new RecordingHandler(req => throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--data", "{}", "--json"), api);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("send_required", error.GetProperty("code").GetString());
        string action = error.GetProperty("action").GetString()!;
        Assert.Contains("publish it with a producer key and run `queuey verify <queue> --event <evt_…>`", action);
        Assert.Contains("`queuey verify <queue> --event-type <type>` and trigger the event yourself, such as with `stripe trigger`", action);
        Assert.EndsWith(VerifyCommand.SendConditions, action);
        Assert.Contains("active verification switched on (production Queuey does not today)", VerifyCommand.SendConditions);
        Assert.Contains("tagged dev, test or staging", VerifyCommand.SendConditions);
        Assert.Contains("without a key or a signature", VerifyCommand.SendConditions);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_test_event_over_64_KB_is_a_usage_error_before_anything_is_sent()
    {
        // Review av #51: over 128 KB svarte Kestrel 413 uten kropp, og brukeren så bare en generell feil.
        var api = new RecordingHandler(req => throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--send", "--data", TooLarge, "--json"), api);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("invalid_value", error.GetProperty("code").GetString());
        Assert.Equal("The test event is 70,008 bytes as JSON, and Queuey takes at most 65,536 (64 KB).", error.GetProperty("message").GetString());
        Assert.DoesNotContain("aaaa", run.Stdout);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task The_retired_content_type_says_that_the_test_event_is_sent_as_json()
    {
        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--send", "--data", "{}", "--content-type", "text/plain"));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("Unknown option --content-type for queuey verify.", run.Stderr);
        Assert.Contains("as application/json", run.Stderr);
    }

    [Fact]
    public async Task Wait_is_the_other_name_for_timeout()
    {
        RecordingHandler api = Server(FlowAnswers.Verification("passed"));

        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--event", "evt_1", "--wait", "120", "--tenant", "ten_abc"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(120, api.Requests.Single(r => r.Method == HttpMethod.Post).Json.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task A_deployment_file_given_as_the_test_event_is_refused_and_nothing_is_sent()
    {
        // apply og plan tar deployment-fila med --file, verify tar testeventen. Et feil valg sendte fila til den ekte mottakeren.
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "$schema": "x", "workspace": { "retentionDays": 7 }, "queues": { "orders": {} } }""");

        CliRun run = await CliHarness.RunAsync(() => Verify("orders", "--send", "--file", path, "--tenant", "ten_abc"));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains($"--file is the test event to send, and {path} is a deployment file.", run.Stderr);
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
