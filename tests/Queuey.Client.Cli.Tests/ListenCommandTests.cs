using System.Net;
using System.Text;
using System.Text.Json;
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

    private static Task<ListenTarget> Resolve(params string[] args)
    {
        string[] all = CliHarness.With(new[] { "--forward-to", "http://localhost:5000" }.Concat(args).ToArray());
        Assert.True(ListenCommand.Options.TryParse(all, out ArgMap map, out _));
        return ListenCommand.ResolveTargetAsync(map, CliHost.Resolve(map));
    }
}
