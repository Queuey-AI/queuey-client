using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// queuey verify: bevis at en kø leverer. En apply som går gjennom sier at konfigurasjonen landet,
/// ikke at events kommer fram; gap-analysen 2026-09-23 fant at en agent ikke kunne se forskjell.
/// Hvert verdikt har en foreslått handling som peker på innstillingen som må endres.
/// </summary>
public class DeliveryVerificationTests
{
    private static readonly PublishResult Published = new() { QueuePublicId = "que_orders", EventId = "evt_1", Mode = "Deliver" };

    private static EventDetailsResponse Event(object status, params EventAttemptResponse[] attempts) => new()
    {
        Status = JsonSerializer.SerializeToElement(status),
        AttemptCount = attempts.Length,
        Attempts = attempts.ToList(),
    };

    private static EventAttemptResponse Attempt(int? code, string? failureClass = null, string? error = null, int number = 1) => new()
    {
        AttemptNumber = number,
        TargetEndpoint = "https://hooks.example.com/orders",
        ResponseCode = code,
        DurationMs = 140,
        FailureClass = failureClass,
        ErrorMessage = error,
    };

    private static DeliveryVerification Judge(EventDetailsResponse? e, QueueListItem? row = null)
        => DeliveryVerifier.Judge("orders", Published, e, row, TimeSpan.FromSeconds(30));

    [Fact]
    public void Delivered_says_where_and_how_fast()
    {
        DeliveryVerification v = Judge(Event(2, Attempt(200)));

        Assert.True(v.Delivered);
        Assert.Equal("delivered", v.Verdict.ToText());
        Assert.Equal("Delivered to https://hooks.example.com/orders: 200 in 140 ms.", v.Summary);
        Assert.Null(v.SuggestedAction);
    }

    [Fact]
    public void Logged_names_the_mode_to_set()
    {
        DeliveryVerification v = Judge(Event(3));

        Assert.Equal(DeliveryVerdict.LoggedNotDelivered, v.Verdict);
        Assert.Equal("logged_not_delivered", v.Verdict.ToText());
        Assert.Contains("\"mode\": \"deliver\" on queues.orders", v.SuggestedAction);
        Assert.Contains("queuey apply", v.SuggestedAction);
    }

    [Fact]
    public void Filtered_names_the_filter()
    {
        DeliveryVerification v = Judge(Event(8));

        Assert.Equal(DeliveryVerdict.Filtered, v.Verdict);
        Assert.Contains("queues.orders.filter", v.SuggestedAction);
    }

    [Theory]
    [InlineData(401, "AuthenticationFailed", "delivery.credentialRef")]
    [InlineData(403, "AuthorizationFailed", "allowlist")]
    [InlineData(404, "RouteOrConfigError", "Check the delivery URL: https://hooks.example.com/orders")]
    [InlineData(422, "BadPayload", "accepts this body and content type")]
    [InlineData(500, "TargetServerError", "its logs")]
    [InlineData(null, "TargetUnavailable", "reachable from the internet")]
    public void A_failure_says_what_to_fix_for_its_class(int? code, string failureClass, string advice)
    {
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass, code is null ? "Connection refused" : null)));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Equal(failureClass, v.FailureClass);
        Assert.Contains(advice, v.SuggestedAction);
        Assert.Contains("retries it", v.Summary);
    }

    [Fact]
    public void A_dead_lettered_event_says_so()
    {
        DeliveryVerification v = Judge(Event(6, Attempt(500, "TargetServerError", number: 1), Attempt(503, "TargetUnavailable", number: 2)));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Equal(503, v.ResponseCode);   // det siste forsøket
        Assert.Equal(2, v.Attempts);
        Assert.Contains("in the DLQ", v.Summary);
    }

    [Fact]
    public void A_timeout_on_a_held_queue_says_to_resume_it()
    {
        DeliveryVerification v = Judge(Event(0), new QueueListItem { PublicId = "que_orders", DeliveryHeld = true, HasDeliveryTarget = true });

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("still Received", v.Summary);
        Assert.Contains("Resume delivery in the Queuey console", v.SuggestedAction);
    }

    [Fact]
    public void A_timeout_behind_a_failing_event_names_it_and_what_to_fix()
    {
        // Funnet lokalt 2026-09-23: en 401 holdt FIFO-køen, og verify gjettet på en backlog.
        EventDetailsResponse blocker = Event(4, Attempt(401, "AuthenticationFailed"));
        blocker.PublicId = "evt_head";

        DeliveryVerification v = DeliveryVerifier.Judge("orders", Published, Event(0), null, TimeSpan.FromSeconds(5), blocker);

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("evt_head, is failing — https://hooks.example.com/orders answered 401 [AuthenticationFailed]", v.Summary);
        Assert.Contains("delivery.credentialRef", v.SuggestedAction);
        Assert.Contains("the events behind it go out", v.SuggestedAction);
    }

    [Fact]
    public void A_timeout_otherwise_points_at_the_backlog()
    {
        DeliveryVerification v = Judge(Event(1), new QueueListItem { PublicId = "que_orders", HasDeliveryTarget = true });

        Assert.Contains("queuey metrics que_orders", v.SuggestedAction);
    }

    [Fact]
    public void The_status_is_read_as_a_number_or_a_name()
    {
        Assert.Equal(DeliveryVerdict.Delivered, Judge(Event("Delivered", Attempt(200))).Verdict);
        Assert.Equal(DeliveryVerdict.Timeout, Judge(Event(99)).Verdict);   // ukjent tall: ikke et utfall
    }

    // ── hele løpet mot stubber ───────────────────────────────────────────────

    [Fact]
    public async Task Verify_publishes_once_and_follows_the_event_to_its_outcome()
    {
        var ingress = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(new
        {
            queuePublicId = "que_orders", eventId = "evt_1", receivedAtUtc = DateTimeOffset.UnixEpoch, mode = "Deliver", replayed = false,
        }));

        // Først ikke lesbar ennå, så i gang, så levert.
        var api = new StubHttpMessageHandler((n, req, _) => n switch
        {
            0 => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, new { error = new { code = "not_found", message = "no" } }),
            1 => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "evt_1", status = 1, attemptCount = 0, attempts = Array.Empty<object>() }),
            _ => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                publicId = "evt_1", status = 2, attemptCount = 1,
                attempts = new[] { new { attemptNumber = 1, targetEndpoint = "https://hooks.example.com/orders", responseCode = 200, durationMs = 90 } },
            }),
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, ingressStub: ingress);

        DeliveryVerification v = await service.VerifyDeliveryAsync("orders", """{"type":"queuey.verify"}"""u8.ToArray(),
            new VerifyDeliveryOptions { PollInterval = TimeSpan.Zero, Timeout = TimeSpan.FromSeconds(10) });

        Assert.Equal(DeliveryVerdict.Delivered, v.Verdict);
        Assert.Equal("evt_1", v.EventId);
        Assert.All(api.Requests, r => Assert.Equal("/events/que_orders/evt_1", r.RequestUri!.AbsolutePath));

        // Én publisering, som JSON, med en ny idempotensnøkkel hver gang — en idempotent kø skal
        // ikke slå sammen to verifiseringer og svare med den første.
        HttpRequestMessage publish = Assert.Single(ingress.Requests);
        Assert.Equal("/events/ten_abc/orders", publish.RequestUri!.AbsolutePath);
        Assert.StartsWith("queuey-verify-", publish.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("application/json", publish.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_key_that_cannot_read_events_is_told_which_permission_verify_needs()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
            new { error = new { code = "forbidden", message = "Missing permission event.read." } }));

        QueueyService service = WaasTestHost.Build(apiStub: api);

        var ex = await Assert.ThrowsAsync<QueueyForbiddenException>(
            () => service.VerifyDeliveryAsync("orders", "{}"u8.ToArray(), new VerifyDeliveryOptions { PollInterval = TimeSpan.Zero }));

        Assert.Contains("Published evt_1 to 'orders'", ex.Message);
        Assert.Contains("event.read", ex.Message);
        Assert.Contains("Build profile", ex.Message);
    }

    [Fact]
    public async Task Verify_times_out_with_the_queues_flow_as_the_reason()
    {
        var api = new StubHttpMessageHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/tenants/", StringComparison.Ordinal)
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_1", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true, deliveryHeld = true } })
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "evt_1", status = 0, attemptCount = 0 }));

        QueueyService service = WaasTestHost.Build(apiStub: api);

        DeliveryVerification v = await service.VerifyDeliveryAsync("orders", "{}"u8.ToArray(),
            new VerifyDeliveryOptions { PollInterval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("Delivery is held", v.Summary);
        Assert.Contains(api.Requests, r => r.RequestUri!.AbsolutePath == "/tenants/ten_abc/queues");
    }
}
