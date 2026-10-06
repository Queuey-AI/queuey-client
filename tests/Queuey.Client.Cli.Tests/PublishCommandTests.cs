using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey publish` mot en kø (F2.7, 2026-10-06): én event, publisert slik produsenten gjør, med nøkkelen til køens
/// ingress-URL, og et svar med eventets id som `queuey verify &lt;kø&gt; --event` følger. Krever ingressen noe CLI-en ikke
/// kan gi, sies det før noe sendes. Svaret har aldri payloaden eller en nøkkel.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class PublishCommandTests : IDisposable
{
    private const string Payload = """{ "orderId": "A-1", "amount": 1200 }""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-publish-cli-tests", Guid.NewGuid().ToString("N"));

    public PublishCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Gjennom CliEntry, så en feil blir det svaret en skal lese (JSON med --json), slik den blir i et skall.
    private static Task<int> Publish(params string[] args) => CliEntry.RunAsync(CliHarness.With(new[] { "publish" }.Concat(args).ToArray()));

    private static string[] Keys(RecordingHandler api) => api.Requests.Select(r => r.Key).ToArray();

    private static object Receipt(bool replayed = false) => new
    {
        queuePublicId = "que_orders",
        eventId = "evt_7",
        receivedAtUtc = "2026-10-06T10:00:00Z",
        mode = "Deliver",
        replayed,
    };

    /// <summary>
    /// A Queuey with the workspace ten_abc and its queue orders, whose ingress is <paramref name="ingress"/>, and an ingress
    /// that answers <paramref name="publish"/>.
    /// </summary>
    private static RecordingHandler Server(object ingress, Func<HttpResponseMessage>? publish = null, string tenant = "ten_abc")
        => new(req => req switch
        {
            { Method.Method: "GET", Path: var p } when p == $"/tenants/{tenant}/queues" => FlowAnswers.Queues(),
            { Method.Method: "GET", Path: "/queues/que_orders/config" } => RecordingHandler.Json(HttpStatusCode.OK, new { ingress }),
            { Method.Method: "POST", Path: var p } when p == $"/events/{tenant}/orders"
                => publish?.Invoke() ?? RecordingHandler.Json(HttpStatusCode.Accepted, Receipt()),
            _ => throw new InvalidOperationException(req.Key),
        });

    [Fact]
    public async Task An_event_goes_to_the_queues_ingress_with_the_producers_key_and_the_answer_names_the_verify_that_follows_it()
    {
        RecordingHandler api = Server(new { authMode = "ApiKey", successStatusCode = 202 });

        CliRun run = await CliHarness.RunAsync(() => Publish(
            "orders", "--data", Payload, "--idempotency-key", "order-A-1", "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[] { "GET /tenants/ten_abc/queues", "GET /queues/que_orders/config", "POST /events/ten_abc/orders" }, Keys(api));

        RecordedRequest sent = api.Requests[2];
        Assert.Equal("ingress.test", sent.Uri.Host);
        Assert.Equal(Payload, sent.Body);
        Dictionary<string, string> headers = api.Headers[2];
        Assert.Equal("qak_kid.secret", headers["X-Api-Key"]);
        Assert.Equal("order-A-1", headers["Idempotency-Key"]);

        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(new[] { "schemaVersion", "tenant", "queue", "queuePublicId", "eventPublicId", "receivedAtUtc", "mode", "replayed", "verify" },
            json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("evt_7", json.GetProperty("eventPublicId").GetString());
        Assert.Equal("que_orders", json.GetProperty("queuePublicId").GetString());
        Assert.Equal("queuey verify orders --event evt_7", json.GetProperty("verify").GetString());

        // Aldri payloaden, nøkkelen eller idempotency-nøkkelen i svaret.
        foreach (string secret in new[] { "A-1", "1200", "qak_", "secret", "order-A-1" })
            Assert.DoesNotContain(secret, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_stream_event_carries_its_type_and_group_key_and_the_human_answer_says_how_to_follow_it()
    {
        RecordingHandler api = Server(new { authMode = "None" });

        CliRun run = await CliHarness.RunAsync(() => Publish(
            "orders", "--event", "order.created", "--key", "cust-1", "--data", Payload, "--tenant", "ten_abc"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Dictionary<string, string> headers = api.Headers[2];
        Assert.Equal("order.created", headers["X-Queuey-Event-Type"]);
        Assert.Equal("cust-1", headers["X-Queuey-Group-Key"]);

        string[] lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Published evt_7 to orders (que_orders) in ten_abc, mode Deliver.", lines[0]);
        Assert.Equal("  → Follow it: queuey verify orders --event evt_7", lines[1]);
    }

    [Fact]
    public async Task A_replayed_idempotency_key_answers_with_the_earlier_event()
    {
        RecordingHandler api = Server(new { authMode = "None" }, () => RecordingHandler.Json(HttpStatusCode.Accepted, Receipt(replayed: true)));

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--idempotency-key", "k1", "--tenant", "ten_abc"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("The idempotency key matched an earlier event, so this is that event: nothing new was stored.", run.Stdout);
    }

    [Theory]
    [InlineData("SignedRequest", "stripe", "verifies stripe signatures", "queuey verify orders --event-type <type> --ingress-auth stripe")]
    [InlineData("SignedRequest", "queuey", "checks Queuey's signature on each event", "queuey verify orders --event <evt_…>")]
    [InlineData("ApiKeyAndSignedRequest", "stripe", "both an API key and a signature (stripe)", "--ingress-auth stripe")]
    [InlineData("ApiKeyAndSignedRequest", "queuey", "both an API key and a signature (queuey)", "queuey verify orders --event <evt_…>")]
    public async Task An_ingress_that_wants_a_signature_the_cli_does_not_make_is_refused_before_anything_is_sent(
        string authMode, string template, string why, string action)
    {
        RecordingHandler api = Server(new { authMode, signedRequest = new { template, credentialRef = "cred_1" } });

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Post);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("ingress_requires_signature", error.GetProperty("code").GetString());
        Assert.Contains(why, error.GetProperty("message").GetString());
        Assert.Contains("nothing was published", error.GetProperty("message").GetString());
        Assert.Contains(action, error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_key_that_may_only_publish_lets_the_ingress_decide_and_a_missing_signature_is_said_plainly()
    {
        RecordingHandler api = new(req => req switch
        {
            { Method.Method: "GET", Path: "/tenants/ten_abc/queues" }
                => RecordingHandler.Error(HttpStatusCode.Forbidden, "missing_permission", "The key lacks queue.read."),
            { Method.Method: "POST", Path: "/events/ten_abc/orders" }
                => RecordingHandler.Error(HttpStatusCode.Unauthorized, "missing_required_header", "A required signature header is missing."),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal(new[] { "GET /tenants/ten_abc/queues", "POST /events/ten_abc/orders" }, Keys(api));
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("missing_required_header", error.GetProperty("code").GetString());
        Assert.Contains("checks a signature on each event", error.GetProperty("message").GetString());
        Assert.Contains("A required signature header is missing.", error.GetProperty("message").GetString());
        Assert.Contains("queuey verify orders --event <evt_…>", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_key_that_may_not_publish_to_the_queue_is_told_what_it_needs()
    {
        RecordingHandler api = Server(new { authMode = "ApiKey" },
            () => RecordingHandler.Error(HttpStatusCode.Forbidden, "missing_permission", "The key lacks event.publish."));

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("missing_permission", error.GetProperty("code").GetString());
        Assert.Contains("This key may not publish to the queue", error.GetProperty("message").GetString());
        Assert.Contains("event.publish", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task An_ingress_that_answers_204_takes_the_event_without_a_receipt_and_the_answer_says_the_id_is_unknown()
    {
        RecordingHandler api = Server(new { authMode = "None", successStatusCode = 204 }, RecordingHandler.NoContent);

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(JsonValueKind.Null, json.GetProperty("eventPublicId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("verify").ValueKind);
        Assert.Equal("que_orders", json.GetProperty("queuePublicId").GetString());
    }

    [Fact]
    public async Task A_queue_the_workspace_does_not_have_is_refused_without_repeating_its_name_and_nothing_is_sent()
    {
        RecordingHandler api = Server(new { authMode = "None" });

        CliRun run = await CliHarness.RunAsync(() => Publish("ordres-typo", "--data", Payload, "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal(new[] { "GET /tenants/ten_abc/queues" }, Keys(api));
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("queue_not_found", error.GetProperty("code").GetString());
        Assert.DoesNotContain("ordres-typo", run.Stdout);
    }

    [Fact]
    public async Task A_queue_whose_ingress_is_closed_is_refused_before_anything_is_sent()
    {
        RecordingHandler api = new(req => req switch
        {
            { Method.Method: "GET", Path: "/tenants/ten_abc/queues" } => RecordingHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "que_orders", displayName = "orders", mode = "Deliver", ingressClosed = true },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal("ingress_closed", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_workspace_is_the_deployment_files_so_verify_finds_the_queue_publish_used()
    {
        string file = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(file, """{ "tenant": "ten_file", "queues": { "orders": {} } }""");
        RecordingHandler api = Server(new { authMode = "None" }, tenant: "ten_file");

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--deployment", file, "--json"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[] { "GET /tenants/ten_file/queues", "GET /queues/que_orders/config", "POST /events/ten_file/orders" }, Keys(api));
        Assert.Equal("ten_file", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task A_deployment_file_whose_tenant_is_not_a_workspace_id_is_refused_without_showing_it()
    {
        string file = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(file, """{ "tenant": "qak_kid.pasted-secret", "queues": {} }""");

        CliRun run = await CliHarness.RunAsync(() => Publish("orders", "--data", Payload, "--deployment", file, "--json"));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("is not a workspace id", run.Stdout);
        Assert.DoesNotContain("pasted-secret", run.Stdout + run.Stderr);
    }

    [Theory]
    [InlineData(new[] { "--data", Payload }, "missing_argument")]
    [InlineData(new[] { "orders" }, "missing_body")]
    [InlineData(new[] { "orders", "--data", Payload, "--stdin" }, "conflicting_options")]
    [InlineData(new[] { "orders", "--queue", "orders", "--data", Payload }, "conflicting_options")]
    public async Task A_command_line_without_one_queue_and_one_event_is_a_usage_error(string[] args, string code)
    {
        CliRun run = await CliHarness.RunAsync(() => Publish(args.Concat(new[] { "--tenant", "ten_abc", "--json" }).ToArray()));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(code, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
