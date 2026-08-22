using System.Net;
using System.Text;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Transfer;

/// <summary>
/// The wire contract against a scripted handler: the status class is the
/// ACK (204 queues return no body), the replay signal is the header, and
/// every Edge header rides every attempt.
/// </summary>
public class HttpTransferChannelTests
{
    [Fact]
    public async Task Accepted_with_body_yields_ack_with_cloud_event_id()
    {
        var (channel, handler) = Channel(_ => Json(HttpStatusCode.Accepted,
            """{"queuePublicId":"que_1","eventId":"evt_original","mode":"Deliver","replayed":false}"""));

        var attempt = await channel.SendAsync(Envelope(), attemptNumber: 1, CancellationToken.None);

        Assert.Equal(TransferClass.Accepted, attempt.Outcome.Class);
        Assert.Equal("evt_original", attempt.Ack!.CloudEventId);
        Assert.False(attempt.Ack.Replayed);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/events/ten_test/orders", request.Uri.AbsolutePath);
        Assert.Equal("qak_id.secret", request.Headers["X-Api-Key"]);
        Assert.Equal("transfer-1", request.Headers["Idempotency-Key"]);
        Assert.False(string.IsNullOrWhiteSpace(request.Headers["X-Queuey-Edge-Version"]));
        Assert.False(string.IsNullOrWhiteSpace(request.Headers["X-Queuey-Occurred-At"]));
        Assert.Equal("1", request.Headers["X-Queuey-Transfer-Attempt"]);
    }

    [Fact]
    public async Task A_204_queue_acks_by_status_class_alone()
    {
        var (channel, _) = Channel(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.TryAddWithoutValidation("X-Queuey-Idempotent-Replay", "true");
            return response;
        });

        var attempt = await channel.SendAsync(Envelope(), 2, CancellationToken.None);

        // No body anywhere — custody must not depend on parsing one. The
        // ONLY replay signal on these queues is the header.
        Assert.Equal(TransferClass.Accepted, attempt.Outcome.Class);
        Assert.Equal(TransferReason.Replayed, attempt.Outcome.Reason);
        Assert.Null(attempt.Ack!.CloudEventId);
        Assert.True(attempt.Ack.Replayed);
    }

    [Fact]
    public async Task Replay_header_on_a_200_marks_the_ack_replayed()
    {
        var (channel, _) = Channel(_ =>
        {
            var response = Json(HttpStatusCode.OK,
                """{"eventId":"evt_original","replayed":true}""");
            response.Headers.TryAddWithoutValidation("X-Queuey-Idempotent-Replay", "true");
            return response;
        });

        var attempt = await channel.SendAsync(Envelope(), 5, CancellationToken.None);

        Assert.True(attempt.Ack!.Replayed);
        Assert.Equal("evt_original", attempt.Ack.CloudEventId);
    }

    [Fact]
    public async Task Throttling_carries_retry_after_into_the_outcome()
    {
        var (channel, _) = Channel(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "23");
            return response;
        });

        var attempt = await channel.SendAsync(Envelope(), 1, CancellationToken.None);

        Assert.Equal(TransferClass.Throttled, attempt.Outcome.Class);
        Assert.Equal(TimeSpan.FromSeconds(23), attempt.Outcome.RetryAfter);
        Assert.Null(attempt.Ack);
    }

    [Fact]
    public async Task Auth_rejection_classifies_requires_action_with_evidence()
    {
        var (channel, _) = Channel(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"code":"invalid_api_key"}}"""));

        var attempt = await channel.SendAsync(Envelope(), 1, CancellationToken.None);

        Assert.Equal(TransferClass.RequiresAction, attempt.Outcome.Class);
        Assert.Equal(TransferReason.AuthenticationRejected, attempt.Outcome.Reason);
        Assert.Contains("invalid_api_key", attempt.Outcome.Evidence!.Snippet);
    }

    [Fact]
    public async Task Transport_exceptions_become_outcomes_never_throws()
    {
        var (channel, _) = Channel(_ => throw new HttpRequestException("boom"));

        var attempt = await channel.SendAsync(Envelope(), 1, CancellationToken.None);

        Assert.Equal(TransferClass.Transient, attempt.Outcome.Class);
        Assert.Null(attempt.Ack);
    }

    [Fact]
    public async Task Context_headers_ride_the_wire_when_present()
    {
        var (channel, handler) = Channel(_ => Json(HttpStatusCode.Accepted, """{"eventId":"evt_1"}"""));

        await channel.SendAsync(Envelope() with
        {
            EventType = "order.created",
            GroupKey = "cust-42",
            Source = "unit-7"
        }, 1, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("order.created", request.Headers["X-Queuey-Event-Type"]);
        Assert.Equal("cust-42", request.Headers["X-Queuey-Group-Key"]);
        Assert.Equal("unit-7", request.Headers["X-Queuey-Source"]);
    }

    // ── plumbing ────────────────────────────────────────────────────────

    private static (HttpTransferChannel Channel, ScriptedHandler Handler) Channel(
        Func<HttpRequestMessage, HttpResponseMessage> script)
    {
        var handler = new ScriptedHandler(script);
        var options = new QueueyEdgeOptions
        {
            ApiKey = "qak_id.secret",
            TenantPublicId = "ten_test",
            IngressBaseAddress = new Uri("https://ingress.example")
        };
        var channel = new HttpTransferChannel(
            new HttpClient(handler), options, new TransferOutcomeClassifier(), new FakeClock());
        return (channel, handler);
    }

    private static EventEnvelope Envelope() => new(
        Version: EventEnvelope.CurrentVersion,
        TransferId: "transfer-1",
        Queue: "orders",
        TenantPublicId: "ten_test",
        ContentType: "application/json",
        Payload: "{}"u8.ToArray(),
        OccurredAtUtc: new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;
        public List<(Uri Uri, Dictionary<string, string> Headers)> Requests { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script) => _script = script;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            Requests.Add((request.RequestUri!, headers));
            return Task.FromResult(_script(request));
        }
    }
}
