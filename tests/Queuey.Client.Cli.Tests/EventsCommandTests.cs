using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey events get` (F2.7, 2026-10-06): ett event slik REST serverer det. Konvolutten til en nøkkel med event.read, og
/// innholdet bare med --content, når nøkkelen har event.payload.read og køens synlighet slipper verdier ut. En titt som ikke
/// kan gis, prøves ikke, så Queuey logger ingenting forgjeves.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class EventsCommandTests
{
    private static Task<int> Events(params string[] args) => CliEntry.RunAsync(CliHarness.With(new[] { "events" }.Concat(args).ToArray()));

    private static string[] Keys(RecordingHandler api) => api.Requests.Select(r => r.Key).ToArray();

    private static object Envelope(bool canRevealContent, string visibility = "shape") => new
    {
        publicId = "evt_1",
        queuePublicId = "que_orders",
        status = 2,
        source = "orders-api",
        contentType = "application/json",
        payloadText = (string?)null,
        createdAtUtc = "2026-10-06T10:00:00Z",
        completedAtUtc = "2026-10-06T10:00:01Z",
        holdReason = (string?)null,
        attemptCount = 1,
        attempts = new[]
        {
            new { attemptNumber = 1, status = 1, responseCode = 200, durationMs = 38, decisionKind = (string?)null, errorMessage = (string?)null },
        },
        canRevealContent,
        payloadVisibility = visibility,
        payloadShape = "{ orderId: string }",
    };

    private static RecordingHandler Server(object envelope) => new(req => req switch
    {
        { Method.Method: "GET", Path: "/tenants/ten_abc/queues" } => FlowAnswers.Queues(),
        { Method.Method: "GET", Path: "/events/que_orders/evt_1" } => RecordingHandler.Json(HttpStatusCode.OK, envelope),
        { Method.Method: "GET", Path: "/events/que_orders/evt_1/content" } => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "evt_1",
            contentType = "application/json",
            payloadText = """{"orderId":"A-1"}""",
            payloadBytes = 17,
            resolvedHeaders = new Dictionary<string, string> { ["X-Customer"] = "c-1" },
            attempts = new[] { new { publicId = "att_1", attemptNumber = 1, responsePreview = "ok" } },
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task An_event_is_read_as_rest_serves_it_and_its_content_is_not_read_unless_asked()
    {
        RecordingHandler api = Server(Envelope(canRevealContent: true));

        CliRun run = await CliHarness.RunAsync(() => Events("get", "evt_1", "--queue", "orders", "--tenant", "ten_abc", "--json"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[] { "GET /tenants/ten_abc/queues", "GET /events/que_orders/evt_1" }, Keys(api));

        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(new[] { "schemaVersion", "queuePublicId", "eventPublicId", "status", "payloadVisibility", "canRevealContent", "event", "content" },
            json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Delivered", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("canRevealContent").GetBoolean());
        Assert.Equal("{ orderId: string }", json.GetProperty("event").GetProperty("payloadShape").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task Content_is_revealed_when_asked_for_and_allowed()
    {
        RecordingHandler api = Server(Envelope(canRevealContent: true, visibility: "values"));

        CliRun run = await CliHarness.RunAsync(() => Events("get", "evt_1", "--queue", "que_orders", "--content", "--json"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(new[] { "GET /events/que_orders/evt_1", "GET /events/que_orders/evt_1/content" }, Keys(api));
        JsonElement content = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("content");
        Assert.Equal("""{"orderId":"A-1"}""", content.GetProperty("payloadText").GetString());
    }

    [Theory]
    [InlineData("shapeOnly", "payload_visibility_shape_only", "serves its payload values to nobody")]
    [InlineData("shape", "payload_read_not_allowed", "needs event.payload.read")]
    public async Task Content_that_may_not_be_revealed_is_refused_before_anything_is_read(string visibility, string code, string why)
    {
        RecordingHandler api = Server(Envelope(canRevealContent: false, visibility));

        CliRun run = await CliHarness.RunAsync(() => Events("get", "evt_1", "--queue", "que_orders", "--content", "--json"), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal(new[] { "GET /events/que_orders/evt_1" }, Keys(api));
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Contains(why, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_human_answer_has_the_status_each_attempt_and_whether_the_payload_can_be_revealed()
    {
        RecordingHandler api = Server(Envelope(canRevealContent: false));

        CliRun run = await CliHarness.RunAsync(() => Events("get", "evt_1", "--queue", "que_orders"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        string[] lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("evt_1 in que_orders: Delivered", lines[0]);
        Assert.Contains("    #1  Success  HTTP 200  38 ms", lines);
        Assert.Equal("  payload: not shown. Revealing it needs event.payload.read, which this key does not have.", lines[^1]);
    }

    [Fact]
    public async Task Values_the_producer_controls_are_written_without_escape_sequences_or_line_breaks()
    {
        // F2.7-review (2026-10-06): source, groupKey, holdReason og errorMessage kommer fra produsenten eller mottakeren.
        var envelope = new
        {
            publicId = "evt_1",
            status = 4,
            source = "orders-api\u001b]0;pwned\u0007\u001b[2J",
            groupKey = "cust-1\nPublished evt_9 to orders",
            holdReason = "held\u202Edesrever",
            attempts = new[] { new { attemptNumber = 1, status = 2, responseCode = 500, errorMessage = "boom\u001b[31mred\r\nnext" } },
            canRevealContent = false,
            payloadVisibility = "shape",
        };
        RecordingHandler api = new(req => req switch
        {
            { Method.Method: "GET", Path: "/events/que_orders/evt_1" } => RecordingHandler.Json(HttpStatusCode.OK, envelope),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => Events("get", "evt_1", "--queue", "que_orders"), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        // Ordinalt: med kulturens sammenligning teller et formateringstegn som U+202E som tomt, og finnes overalt.
        Assert.DoesNotContain("\u001b", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("  source: orders-api", run.Stdout);
        Assert.Contains("  group key: cust-1 Published evt_9 to orders", run.Stdout);
        Assert.Contains("boomred  next", run.Stdout);
        Assert.DoesNotContain("\nPublished evt_9", run.Stdout);
    }

    [Theory]
    [InlineData(new[] { "get", "evt_1" }, "missing_argument")]
    [InlineData(new[] { "get", "--queue", "orders" }, "missing_argument")]
    [InlineData(new[] { "get", "qak_kid.pasted", "--queue", "orders" }, "invalid_value")]
    [InlineData(new[] { "list" }, "unknown_subcommand")]
    public async Task A_command_line_without_an_event_id_and_its_queue_is_a_usage_error(string[] args, string code)
    {
        CliRun run = await CliHarness.RunAsync(() => Events(args.Concat(new[] { "--json" }).ToArray()));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(code, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("pasted", run.Stdout);
    }
}
