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
    [InlineData(401, "AuthenticationFailed", "delivery.credentialRef", "holds the queue's deliveries")]
    [InlineData(403, "AuthorizationFailed", "allowlist", "holds the queue's deliveries")]
    [InlineData(404, "RouteOrConfigError", "Check the delivery URL: https://hooks.example.com/orders", "holds the queue's deliveries")]
    [InlineData(422, "BadPayload", "accepts this body and content type", null)]
    [InlineData(500, "TargetServerError", "its logs", "probes it")]
    [InlineData(null, "TargetUnavailable", "reachable from the internet", "probes it")]
    [InlineData(429, "RateLimited", "rate-limiting", "after the wait the receiver asked for")]
    public void A_failure_says_what_to_fix_and_what_Queuey_does_next_for_its_class(int? code, string failureClass, string advice, string? next)
    {
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass, code is null ? "Connection refused" : null)));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Equal(failureClass, v.FailureClass);
        Assert.Contains(advice, v.SuggestedAction);

        // Før 2026-10-05 lovte hver feil «Queuey retries it on the queue's schedule», også en 401, der backenden
        // parkerer målet og holder køen. En klasse der neste steg avhenger av DLQ-en, får ingen spådom.
        Assert.DoesNotContain("on the queue's schedule", v.Summary);
        if (next is null)
            Assert.EndsWith($"[{failureClass}].", v.Summary);
        else
            Assert.Contains(next, v.Summary);
    }

    [Theory]
    [InlineData(401, "AuthenticationFailed", "delivery.credentialRef")]
    [InlineData(403, "AuthorizationFailed", "allowlist")]
    [InlineData(404, "RouteOrConfigError", "Check the delivery URL")]
    [InlineData(null, "ProtocolOrSecurityIssue", "certificate")]
    [InlineData(501, "PermanentTargetError", "will never succeed as it is")]
    [InlineData(null, "TransformFailed", "payload mutations")]
    [InlineData(null, "SignatureRecalculationFailed", "Stripe verification back on")]
    public void A_failure_that_holds_the_queue_says_to_resume_it_after_the_fix(int? code, string failureClass, string advice)
    {
        // Funnet mot backenden 2026-10-05: disse parkerer målet eller køen, og Queuey holder køens leveringer til noen
        // gjenopptar den. Rådet sa bare hva som skulle rettes, så neste verify ventet bak den holdte køen. TLS og
        // permanente feil fikk før rådet om en URL som ikke var nåbar.
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass)));

        Assert.Contains("holds the queue's deliveries until this is fixed and the queue is resumed", v.Summary);
        Assert.Contains(advice, v.SuggestedAction);
        Assert.EndsWith("Then resume the queue with Verify & resume, in the Queuey console or over MCP: until then Queuey " +
                        "holds its deliveries, so verifying again waits.", v.SuggestedAction);
    }

    [Theory]
    [InlineData(422, "BadPayload")]
    [InlineData(500, "TargetServerError")]
    [InlineData(null, "TargetUnavailable")]
    [InlineData(429, "RateLimited")]
    [InlineData(null, "OriginNotVerified")]
    public void A_failure_that_leaves_the_queue_running_asks_for_no_resume(int? code, string failureClass)
    {
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass)));

        Assert.DoesNotContain("Verify & resume", v.SuggestedAction);
        Assert.DoesNotContain("holds the queue", v.Summary);
    }

    [Theory]
    [InlineData("SignatureRecalculationFailed", "turn the queue's Stripe verification back on")]
    [InlineData("OriginNotVerified", "`stripe events resend`")]
    [InlineData("TransformFailed", "payload mutations")]
    public void A_failure_before_sending_says_the_receiver_was_never_contacted(string failureClass, string advice)
    {
        // Queuey stoppet eventen selv. Før 2026-10-05 sa verify «it did not answer» om mottakeren, og to av klassene
        // (fra backenden 2026-09-24 og -25) fikk rådet om en URL som ikke var nåbar.
        DeliveryVerification v = Judge(Event(4, Attempt(null, failureClass, "The queue's ingress no longer verifies Stripe.")));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.StartsWith(
            $"Queuey did not send the event to https://hooks.example.com/orders [{failureClass}]: The queue's ingress no longer " +
            "verifies Stripe. The receiver was never contacted.", v.Summary);
        Assert.DoesNotContain("did not answer", v.Summary);
        Assert.Contains(advice, v.SuggestedAction);
        Assert.DoesNotContain("reachable from the internet", v.SuggestedAction);
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
        Assert.Contains("Queuey holds the queue's deliveries until that is fixed and the queue is resumed", v.Summary);
        Assert.Contains("delivery.credentialRef", v.SuggestedAction);
        Assert.Contains("Verify & resume", v.SuggestedAction);
        Assert.Contains("the events behind it go out", v.SuggestedAction);
    }

    [Theory]
    [InlineData(true, false, "Delivery is held")]
    [InlineData(false, true, "The queue is suspended")]
    public void A_held_or_suspended_queue_is_the_reason_even_with_a_failing_event_ahead(bool held, bool suspended, string reason)
    {
        // Review 2026-09-24: holdt og suspendert levering stopper hele køen, så en feilende event foran
        // er ikke grunnen til at denne venter. Før ble eventen navngitt først.
        EventDetailsResponse blocker = Event(4, Attempt(401, "AuthenticationFailed"));
        blocker.PublicId = "evt_head";
        var row = new QueueListItem { PublicId = "que_orders", HasDeliveryTarget = true, DeliveryHeld = held, Suspended = suspended };

        DeliveryVerification v = DeliveryVerifier.Judge("orders", Published, Event(0), row, TimeSpan.FromSeconds(5), blocker);

        Assert.Contains(reason, v.Summary);
        Assert.DoesNotContain("evt_head", v.Summary);
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

    /// <summary>
    /// En kø der eventen fortsatt venter, og en eldre event som feiler. Hver lesing verify gjør, har sin
    /// egen rute: før svarte stuben med eventen på alt, også på listen over feilende events — den ble
    /// lest som en side uten elementer, så testen besto uten at det fantes noen feilende event.
    /// </summary>
    private static StubHttpMessageHandler Waiting(
        string? ordering, bool held = false, int aheadCode = 401, string aheadClass = "AuthenticationFailed") => new(req => req.RequestUri!.AbsolutePath switch
    {
        "/tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
        {
            new { publicId = "que_1", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true, deliveryHeld = held, suspended = false },
        }),
        "/queues/que_1/config" => ordering is null
            ? StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "forbidden", message = "no" } })
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy = new { ordering } }),
        "/events/que_1" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { items = new[] { new { publicId = "evt_head", attemptCount = 3 } } }),
        "/events/que_1/evt_head" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "evt_head", status = 4, attemptCount = 3,
            attempts = new[] { new { attemptNumber = 3, targetEndpoint = "https://hooks.example.com/orders", responseCode = aheadCode, failureClass = aheadClass } },
        }),
        "/events/que_1/evt_1" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "evt_1", status = 0, attemptCount = 0 }),
        string other => throw new InvalidOperationException(other),
    });

    private static Task<DeliveryVerification> VerifyUntilTimeout(StubHttpMessageHandler api)
        => WaasTestHost.Build(apiStub: api).VerifyDeliveryAsync("orders", "{}"u8.ToArray(),
            new VerifyDeliveryOptions { PollInterval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromMilliseconds(50) });

    [Fact]
    public async Task Verify_times_out_with_the_queues_flow_as_the_reason()
    {
        // En feilende event ligger foran i en fifo-kø, men levering er holdt: det er grunnen.
        StubHttpMessageHandler api = Waiting("fifo", held: true);

        DeliveryVerification v = await VerifyUntilTimeout(api);

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("Delivery is held", v.Summary);
        Assert.DoesNotContain("evt_head", v.Summary);
        Assert.Equal("ten_abc", v.Tenant);
        Assert.Contains(api.Requests, r => r.RequestUri!.AbsolutePath == "/tenants/ten_abc/queues");
    }

    [Theory]
    [InlineData("fifo", true)]         // hele køen er én rekke: eventen foran holder denne
    [InlineData("bykey", false)]       // den holder bare sin egen nøkkel
    [InlineData("besteffort", false)]  // ingen rekkefølge, ingen holder noen
    [InlineData(null, false)]          // rekkefølgen kunne ikke leses: ikke gjett
    public async Task A_rejected_event_ahead_is_named_only_when_the_queue_is_one_fifo_lane(string? ordering, bool named)
    {
        // En 422 gjelder eventen, ikke mottakeren: den holder bare eventene bak seg i sin egen rekke.
        DeliveryVerification v = await VerifyUntilTimeout(Waiting(ordering, aheadCode: 422, aheadClass: "BadPayload"));

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        if (named)
        {
            Assert.Contains("evt_head, is failing", v.Summary);
            Assert.Contains("with fifo ordering the events behind it wait for it", v.Summary);
            Assert.Contains("accepts this body and content type", v.SuggestedAction);
        }
        else
        {
            Assert.DoesNotContain("evt_head", v.Summary);
            Assert.Contains("queuey metrics que_1", v.SuggestedAction);
        }
    }

    [Theory]
    [InlineData("fifo")]
    [InlineData("bykey")]
    [InlineData("besteffort")]
    [InlineData(null)]
    public async Task A_failure_ahead_that_holds_the_queue_is_named_whatever_the_ordering(string? ordering)
    {
        // Backenden parkerer målet på en 401, og da står hele køen, også med bykey og besteffort. Fra 2026-09-24 til
        // 2026-10-05 ble eventen bare navngitt på fifo, og verify gjettet ellers på en backlog.
        StubHttpMessageHandler api = Waiting(ordering);

        DeliveryVerification v = await VerifyUntilTimeout(api);

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("evt_head, is failing — https://hooks.example.com/orders answered 401 [AuthenticationFailed]", v.Summary);
        Assert.Contains("Queuey holds the queue's deliveries until that is fixed and the queue is resumed", v.Summary);
        Assert.Contains("Verify & resume", v.SuggestedAction);

        // Klassen avgjør alene, så rekkefølgen trengs ikke.
        Assert.DoesNotContain(api.Requests, r => r.RequestUri!.AbsolutePath == "/queues/que_1/config");
    }
}
