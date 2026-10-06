using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// <c>queuey listen</c> for agents (Queuey F2.5, 2026-10-06). With <c>--json</c> it prints one versioned JSON object per
/// line — listening, a delivery per forward with the event id, the queue endpoint's path, where it went on this machine,
/// the status, the time and the signature headers, and one last line: refused, superseded or closed. A queue is named
/// as in the deployment file and the user stories (<c>--queue orders</c>), and <c>--tee</c>, which never did what it
/// said, is gone.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ListenCommandTests
{
    private static ListenEnvelope Envelope(List<string>? signatureHeaders) => new(
        EventId: "evt_1",
        QueuePublicId: "que_1",
        QueueName: "",
        EventType: "invoice.paid",
        GroupKey: null,
        Method: "POST",
        PathAndQuery: "/api/stripe",
        OriginalUrl: "https://api.example.com/api/stripe",
        Headers: new List<ListenHeader> { new("Stripe-Signature", "t=1,v1=abc"), new("Content-Type", "application/json") },
        ContentType: "application/json",
        BodyBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")),
        CorrelationId: "c_1",
        Mode: "Redirect",
        SignatureHeaders: signatureHeaders);

    private static readonly LocalForwardResult Answered = new(200, 12, new Uri("http://localhost:5000/api/stripe"), null);

    private static JsonElement[] Lines(StringWriter stdout)
        => stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    [Fact]
    public void With_json_a_delivery_is_one_line_with_the_fields_an_agent_reads()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        new ListenOutput(json: true, stdout, stderr).Delivery(Envelope(new List<string> { "Stripe-Signature" }), Answered);

        JsonElement line = Assert.Single(Lines(stdout));
        Assert.Equal(1, line.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("delivery", line.GetProperty("type").GetString());
        Assert.Equal("evt_1", line.GetProperty("eventId").GetString());
        Assert.Equal("/api/stripe", line.GetProperty("path").GetString());
        Assert.Equal("http://localhost:5000/api/stripe", line.GetProperty("localUrl").GetString());
        Assert.Equal(200, line.GetProperty("status").GetInt32());
        Assert.Equal(12, line.GetProperty("durationMs").GetInt64());
        Assert.Equal(new[] { "Stripe-Signature" }, line.GetProperty("signatureHeaders").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Null, line.GetProperty("error").ValueKind);
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void A_queuey_older_than_the_field_is_read_for_signature_headers_by_their_names()
    {
        Assert.Equal(new[] { "Stripe-Signature" }, ListenOutput.SignatureHeaders(Envelope(signatureHeaders: null)));
        Assert.Empty(ListenOutput.SignatureHeaders(Envelope(new List<string>())));
    }

    [Fact]
    public void The_stream_starts_with_listening_and_every_way_it_ends_is_one_versioned_line()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var output = new ListenOutput(json: true, stdout, stderr);

        output.Listening(new ListenTarget("queue", "que_1", Name: "orders"), "listen:queue:que_1", "http://localhost:5000", tookOver: true);
        output.Refused("listener_already_connected", "another queuey listen session has listened on it", "Stop it, or --take-over.",
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        output.Superseded("Another queuey listen session took the queue over.", forwarded: 3);
        output.Closed("connection_lost", "The connection to Queuey was lost and did not come back.", forwarded: 3);

        JsonElement[] lines = Lines(stdout);
        Assert.Equal(new[] { "listening", "refused", "superseded", "closed" }, lines.Select(l => l.GetProperty("type").GetString()));
        Assert.All(lines, l => Assert.Equal(1, l.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal("que_1", lines[0].GetProperty("id").GetString());
        Assert.Equal("orders", lines[0].GetProperty("name").GetString());
        Assert.True(lines[0].GetProperty("tookOver").GetBoolean());
        Assert.Equal("listener_already_connected", lines[1].GetProperty("code").GetString());
        Assert.Equal("2026-10-06T09:00:00+00:00", lines[1].GetProperty("heldSinceUtc").GetString());
        Assert.Equal("connection_lost", lines[3].GetProperty("reason").GetString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void Without_json_a_delivery_shows_the_event_id_beside_its_type()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        new ListenOutput(json: false, stdout, stderr).Delivery(Envelope(signatureHeaders: null), Answered);

        Assert.Contains("POST", stdout.ToString());
        Assert.Contains("/api/stripe  →  200 (12ms)  [invoice.paid evt_1]", stdout.ToString());
    }

    [Fact]
    public async Task The_retired_tee_option_says_why_it_is_gone()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "listen", "--forward-to", "http://localhost:5000", "--tee" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--tee is gone", run.Stderr);
    }

    [Fact]
    public async Task A_queue_given_by_name_is_found_in_its_workspace()
    {
        var api = new RecordingHandler(r => r.Path == "/tenants/ten_dev/queues"
            ? RecordingHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "que_other", displayName = "other" },
                new { publicId = "que_orders", displayName = "orders" },
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        ListenTarget? target = null;
        await CliHarness.RunAsync(async () =>
        {
            target = await Resolve("--queue", "orders", "--tenant", "ten_dev");
            return 0;
        }, api);

        Assert.Equal(new ListenTarget("queue", "que_orders", Name: "orders"), target);
    }

    [Fact]
    public async Task A_queue_given_by_id_is_used_as_it_is()
    {
        ListenTarget? target = null;
        await CliHarness.RunAsync(async () =>
        {
            target = await Resolve("--queue", "que_orders");
            return 0;
        });

        Assert.Equal(new ListenTarget("queue", "que_orders"), target);
    }

    [Fact]
    public async Task A_queue_name_without_its_workspace_says_how_to_give_one()
    {
        CliUsageException? refused = null;
        await CliHarness.RunAsync(async () =>
        {
            refused = await Assert.ThrowsAsync<CliUsageException>(() => Resolve("--queue", "orders"));
            return 0;
        });

        Assert.Contains("--tenant", refused!.Action);
    }

    [Fact]
    public async Task A_queue_name_the_workspace_does_not_have_is_not_found()
    {
        var api = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_other", displayName = "other" } }));

        QueueyNotFoundException? notFound = null;
        await CliHarness.RunAsync(async () =>
        {
            notFound = await Assert.ThrowsAsync<QueueyNotFoundException>(() => Resolve("--queue", "orders", "--tenant", "ten_dev"));
            return 0;
        }, api);

        Assert.Equal("queue_not_found", notFound!.ErrorCode);
    }

    // ── review av #50 ────────────────────────────────────────────────────────

    [Theory]
    // Strengen ASP.NET Core 10 gir til klienten CLI-en bruker (8.0.11), målt i backend-testen
    // ListenReplicaRoutingTests.An_unknown_hub_method_is_refused_with_a_message_that_names_it (K1).
    [InlineData("Failed to invoke 'ListenAsOwner' due to an error on the server. HubException: Method does not exist.", true)]
    [InlineData("Unknown hub method 'ListenAsOwner'", true)]
    [InlineData("An unexpected error occurred invoking 'ListenAsOwner' on the server. HubException: Forbidden: no.", false)]
    [InlineData("Failed to invoke 'Heartbeat' due to an error on the server. HubException: Method does not exist.", false)]
    public void An_older_queuey_is_known_by_the_server_saying_it_has_no_ListenAsOwner(string serverMessage, bool older)
    {
        Assert.Equal(older, ListenCommand.IsUnknownMethod(new HubException(serverMessage), "ListenAsOwner"));
    }

    public static TheoryData<string[], string, int> ErrorsBeforeTheSession => new()
    {
        { new[] { "listen", "--json", "--no-such-option" }, "unknown_option", ExitCodes.Usage },
        { new[] { "listen", "--json" }, "missing_argument", ExitCodes.Usage },
        { new[] { "listen", "--json", "--forward-to", "http://localhost:5000", "--config", Path.Combine(Path.GetTempPath(), "queuey-cli-tests-no-config.json") }, "config_error", ExitCodes.Configuration },
        { CliHarness.With("listen", "--json", "--forward-to", "http://localhost:5000", "--queue", "orders"), "missing_argument", ExitCodes.Usage },
    };

    [Theory]
    [MemberData(nameof(ErrorsBeforeTheSession))]
    public async Task With_json_an_error_before_the_session_is_one_refused_line_and_keeps_its_exit_code(string[] args, string code, int exit)
    {
        // Review av #50, K2: før kom disse som et JSON-objekt over flere linjer, som en NDJSON-leser ikke kan lese.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));

        Assert.Equal(exit, run.Exit);
        JsonElement line = Assert.Single(Lines(new StringWriter(new StringBuilder(run.Stdout))));
        Assert.Equal("refused", line.GetProperty("type").GetString());
        Assert.Equal(1, line.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(code, line.GetProperty("code").GetString());
    }

    [Fact]
    public async Task With_json_a_queue_name_the_workspace_does_not_have_is_one_refused_line()
    {
        var api = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_other", displayName = "other" } }));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(
            CliHarness.With("listen", "--json", "--forward-to", "http://localhost:5000", "--queue", "orders", "--tenant", "ten_dev")), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement line = Assert.Single(Lines(new StringWriter(new StringBuilder(run.Stdout))));
        Assert.Equal("queue_not_found", line.GetProperty("code").GetString());
    }

    [Fact]
    public void An_address_is_printed_without_its_query_or_a_part_that_may_be_a_secret_and_the_app_still_gets_all_of_it()
    {
        // Review av #50, K7: det en agent leser, havner i transkriptet (F1.5, TargetUrlRedaction).
        var env = Envelope(signatureHeaders: null) with
        {
            OriginalUrl = "https://api.example.com/hooks/s3cr3t-t0k3n?sig=abc",
            PathAndQuery = "/hooks/s3cr3t-t0k3n?sig=abc",
        };
        var result = new LocalForwardResult(200, 3, ListenForwarder.LocalUrl(env, "http://localhost:5000"), null);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        new ListenOutput(json: true, stdout, stderr).Delivery(env, result);

        JsonElement line = Assert.Single(Lines(stdout));
        Assert.Equal("/hooks/…", line.GetProperty("path").GetString());
        Assert.Equal("http://localhost:5000/hooks/…", line.GetProperty("localUrl").GetString());
        Assert.Equal("http://localhost:5000/hooks/s3cr3t-t0k3n?sig=abc", result.LocalUrl.ToString());
        Assert.Equal("/services/T0001/B0001/…", UrlRedaction.EndpointPath("https://hooks.slack.com/services/T0001/B0001/XXXXsecret", "/services/T0001/B0001/XXXXsecret"));
    }

    [Fact]
    public async Task An_app_that_does_not_answer_in_time_is_a_504_the_delivery_records()
    {
        // Review av #50, K4: HttpClient venter 100 s som standard, og Queuey venter 20 s på svaret.
        using var http = new HttpClient(new NeverAnswers()) { Timeout = TimeSpan.FromMilliseconds(200) };

        LocalForwardResult result = await ListenForwarder.ForwardAsync(http, Envelope(signatureHeaders: null), "http://localhost:5000", CancellationToken.None);

        Assert.Equal(504, result.Status);
        Assert.Contains("did not answer", result.Error);
        Assert.True(ListenForwarder.LocalTimeout < TimeSpan.FromSeconds(20), "the app's time is shorter than Queuey's wait for the answer");
    }

    public static TheoryData<string, string?, string, int> SessionEnds => new()
    {
        { "stopped", null, "closed", ExitCodes.Success },
        { "terminated", null, "closed", ListenCommand.TerminatedExitCode },
        { "connection_lost", null, "closed", ExitCodes.RuntimeError },
        { "superseded", null, "superseded", ExitCodes.RuntimeError },
        { "refused", "forbidden", "refused", ExitCodes.RuntimeError },
        { "refused", "unauthorized", "refused", ExitCodes.Configuration },
    };

    [Theory]
    [MemberData(nameof(SessionEnds))]
    public void Every_way_a_session_ends_is_one_last_line_and_an_exit_code(string reason, string? code, string type, int exit)
    {
        // Review av #50, K5 og K6: SIGTERM gir en siste linje og 143, og en nekting ved reconnect er refused med sin kode.
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        int exitCode = ListenCommand.Finish(new ListenCommand.ListenEnd(reason, "why", code), new ListenOutput(json: true, stdout, stderr), forwarded: 2);

        Assert.Equal(exit, exitCode);
        JsonElement line = Assert.Single(Lines(stdout));
        Assert.Equal(type, line.GetProperty("type").GetString());
        if (type == "closed")
            Assert.Equal(reason, line.GetProperty("reason").GetString());
        if (code is not null)
            Assert.Equal(code, line.GetProperty("code").GetString());
    }

    [Fact]
    public void A_session_whose_output_is_gone_ends_without_writing_more()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        int exitCode = ListenCommand.Finish(new ListenCommand.ListenEnd("output_closed"), new ListenOutput(json: true, stdout, stderr), forwarded: 0);

        Assert.Equal(ExitCodes.RuntimeError, exitCode);
        Assert.Equal("", stdout.ToString());
    }

    [Fact]
    public void A_workspace_session_that_lost_a_queue_says_so_in_one_line_and_keeps_listening()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        new ListenOutput(json: true, stdout, stderr).Lost(new ListenLost("que_orders", "Another queuey listen session took the queue que_orders over."));

        JsonElement line = Assert.Single(Lines(stdout));
        Assert.Equal("lost", line.GetProperty("type").GetString());
        Assert.Equal("que_orders", line.GetProperty("queue").GetString());
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static Task<ListenTarget> Resolve(params string[] args)
    {
        string[] all = CliHarness.With(new[] { "--forward-to", "http://localhost:5000" }.Concat(args).ToArray());
        Assert.True(ListenCommand.Options.TryParse(all, out ArgMap map, out _));
        return ListenCommand.ResolveTargetAsync(map, CliHost.Resolve(map));
    }
}
