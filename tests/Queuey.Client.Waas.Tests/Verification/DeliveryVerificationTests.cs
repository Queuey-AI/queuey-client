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
/// Hvert verdikt har en foreslått handling som peker på innstillingen som må endres. Fra 2026-10-05
/// leser verify hva Queuey bestemte etter hvert forsøk, i stedet for å gjette ut fra feilklassen.
/// </summary>
public class DeliveryVerificationTests
{
    private static readonly PublishResult Published = new() { QueuePublicId = "que_orders", EventId = "evt_1", Mode = "Deliver" };

    private static EventDetailsResponse Event(object status, params EventAttemptResponse[] attempts) => new()
    {
        Status = JsonSerializer.SerializeToElement(status),
        AttemptCount = attempts.Count(a => !a.IsHeld),
        Attempts = attempts.ToList(),
    };

    /// <summary>Et forsøk som ble sendt (status 2 eller 3 på ledningen spiller ingen rolle her; bare Held = 5 gjør).</summary>
    private static EventAttemptResponse Attempt(
        int? code, string? failureClass = null, string? error = null, int number = 1, string? kind = null, string? reason = null) => new()
    {
        AttemptNumber = number,
        TargetEndpoint = "https://hooks.example.com/orders",
        ResponseCode = code,
        DurationMs = 140,
        FailureClass = failureClass,
        ErrorMessage = error,
        Status = JsonSerializer.SerializeToElement(code is >= 200 and < 300 ? 1 : 2),
        DecisionKind = kind,
        DecisionReason = reason,
    };

    /// <summary>Et holdt forsøk: Queuey sendte ikke, og eventen er planlagt på nytt.</summary>
    private static EventAttemptResponse Held(string reason, int number = 2, DateTimeOffset? until = null) => new()
    {
        AttemptNumber = number,
        TargetEndpoint = "https://hooks.example.com/orders",
        ResponseCode = null,
        DurationMs = 0,
        FailureClass = "None",
        ErrorMessage = reason,
        Status = JsonSerializer.SerializeToElement(5),
        DecisionKind = "HoldTarget",
        DecisionReason = reason,
        DecisionUntilUtc = until,
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

    // ── hva Queuey bestemte ─────────────────────────────────────────────────

    [Theory]
    [InlineData(401, "AuthenticationFailed", "HoldEvent", "target_requires_action_auth_failed", "delivery.credentialRef", "holds the queue's deliveries")]
    [InlineData(403, "AuthorizationFailed", "HoldEvent", "target_requires_action_auth_z_failed", "allowlist", "holds the queue's deliveries")]
    [InlineData(404, "RouteOrConfigError", "HoldEvent", "target_requires_action_route_or_config", "Check the delivery URL: https://hooks.example.com/orders", "holds the queue's deliveries")]
    [InlineData(500, "TargetServerError", "RetryLater", "retry_transient_target_server_error", "its logs", "probes it")]
    [InlineData(null, "TargetUnavailable", "RetryLater", "retry_transient_target_unavailable", "reachable from the internet", "probes it")]
    [InlineData(429, "RateLimited", "RetryLater", "retry_after_target_hint", "rate-limiting", "after the wait the receiver asked for")]
    [InlineData(422, "BadPayload", "HoldQueue", "no_retry_bad_payload", "accepts this body and content type", "the event holds the queue")]
    [InlineData(422, "BadPayload", "HoldKey", "no_retry_bad_payload", "accepts this body and content type", "the event holds its key")]
    public void A_failure_says_what_to_fix_and_what_Queuey_decided_to_do_next(
        int? code, string failureClass, string kind, string reason, string advice, string next)
    {
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass, code is null ? "Connection refused" : null, kind: kind, reason: reason)));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Equal(failureClass, v.FailureClass);
        Assert.Contains(advice, v.SuggestedAction);
        Assert.Contains(next, v.Summary);

        // Før 2026-10-05 lovte hver feil «Queuey retries it on the queue's schedule», også en 401, der backenden
        // parkerer målet og holder køen.
        Assert.DoesNotContain("on the queue's schedule", v.Summary);
    }

    [Theory]
    [InlineData("HoldEvent", "Then a person resumes the queue in the Queuey console (Verify & resume)")]
    [InlineData("HoldQueue", "Then a person unlocks the queue in the Queuey console, which sends the event again, or skips it")]
    [InlineData("HoldKey", "Then a person sends the event again or skips it in the Queuey console")]
    public void A_failure_that_waits_for_a_person_says_what_the_person_does_after_the_fix(string kind, string step)
    {
        // Verify & resume finnes i konsollet: REST-ruten krever en person, og MCP krever target.write (review 2026-10-05).
        DeliveryVerification v = Judge(Event(4, Attempt(422, "BadPayload", kind: kind, reason: "no_retry_bad_payload")));

        Assert.Contains(step, v.SuggestedAction);
        Assert.DoesNotContain("over MCP", v.SuggestedAction);
    }

    [Theory]
    [InlineData("HoldQueue", "the event holds the queue: nothing behind it is delivered")]
    [InlineData("HoldKey", "the event holds its key: later events with the same key wait, and other keys keep delivering")]
    public void With_the_dlq_off_a_rejected_event_says_it_holds_the_queue_or_its_key(string kind, string holds)
    {
        // Review 2026-10-05: med DLQ av stopper en 422 køen (HoldQueue, eller HoldKey på en bykey-kø). Testen sa det motsatte.
        DeliveryVerification v = Judge(Event(4, Attempt(422, "BadPayload", kind: kind, reason: "no_retry_bad_payload")));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Contains("With the DLQ off, " + holds, v.Summary);
        Assert.Contains("With \"dlqEnabled\": true, an event the receiver rejects goes to the DLQ instead", v.SuggestedAction);
    }

    [Fact]
    public void A_timeout_on_a_queue_not_marked_idempotent_says_how_to_let_Queuey_send_it_again()
    {
        // Med standardvalgene (idempotent false, timeoutBehavior Hold) parkerer et tidsavbrudd køen (review 2026-10-05).
        // Før sa verify at Queuey sendte den igjen, og rådet om å sjekke URL-en.
        DeliveryVerification v = Judge(Event(4, Attempt(null, "TargetUnavailable", "The request timed out after 30000 ms.",
            kind: "HoldEvent", reason: "target_requires_action_timeout_not_idempotent")));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Contains("the queue is not marked idempotent, so Queuey holds the queue rather than risk delivering it twice", v.Summary);
        Assert.DoesNotContain("sends it again", v.Summary);
        Assert.Equal(
            "The receiver did not answer within the delivery timeout. If it handles the same event twice safely, declare " +
            "\"idempotent\": true on queues.orders; if it is only slow, raise delivery.timeoutMs. Run `queuey apply`, " +
            "then a person resumes the queue in the Queuey console.", v.SuggestedAction);
    }

    [Fact]
    public void A_dead_lettered_event_says_so()
    {
        DeliveryVerification v = Judge(Event(6,
            Attempt(500, "TargetServerError", number: 1, kind: "RetryLater"),
            Attempt(503, "TargetUnavailable", number: 2, kind: "MoveToDlq", reason: "dlq_after_retries_exhausted")));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Equal(503, v.ResponseCode);   // det siste forsøket
        Assert.Equal(2, v.Attempts);
        Assert.Contains("in the DLQ", v.Summary);
        Assert.DoesNotContain("Verify & resume", v.SuggestedAction);
    }

    [Theory]
    [InlineData(401, "AuthenticationFailed", "delivery.credentialRef")]
    [InlineData(404, "RouteOrConfigError", "Check the delivery URL")]
    [InlineData(null, "ProtocolOrSecurityIssue", "certificate")]
    [InlineData(501, "PermanentTargetError", "will never succeed as it is")]
    [InlineData(422, "BadPayload", "accepts this body")]
    public void Without_a_recorded_decision_the_class_decides_as_before(int? code, string failureClass, string advice)
    {
        // Et API uten beslutninger på forsøkene: klassen avgjør, som før 2026-10-05.
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass)));

        Assert.Contains(advice, v.SuggestedAction);
        bool parks = failureClass != "BadPayload";
        Assert.Equal(parks, v.SuggestedAction!.Contains("a person resumes the queue in the Queuey console"));
        Assert.Equal(parks, v.Summary.Contains("holds the queue's deliveries"));
    }

    // ── holdt før sending ───────────────────────────────────────────────────

    [Fact]
    public void A_held_attempt_is_not_an_outcome_so_verify_keeps_waiting()
    {
        // Review 2026-10-05: sendebudsjettet skriver et holdt forsøk og setter eventen til Failed for å planlegge den på
        // nytt. To verify innenfor samme vindu ga «could not be reached… check the URL» og exit 1.
        EventDetailsResponse e = Event(4, Attempt(200, number: 1), Held("rate_limit_budget"));

        Assert.False(DeliveryVerifier.IsSettled(e));
        Assert.True(DeliveryVerifier.IsSettled(Event(4, Attempt(500, "TargetServerError", kind: "RetryLater"))));
    }

    [Theory]
    [InlineData("rate_limit_budget", "The send budget for this endpoint (delivery.rateLimit) is spent", "The event goes out when the window resets")]
    [InlineData("target_open", "The receiver failed several times in a row, so Queuey holds the queue's deliveries and probes it", "Queuey sends the held events when a probe gets through")]
    [InlineData("target_requires_action", "An earlier failure parked the receiver", "a person resumes the queue in the Queuey console")]
    public void A_timeout_on_a_held_event_says_what_holds_it(string reason, string because, string action)
    {
        DeliveryVerification v = Judge(Event(4, Held(reason, number: 1, until: new DateTimeOffset(2026, 10, 5, 12, 0, 30, TimeSpan.Zero))));

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("Queuey is holding the event before sending it", v.Summary);
        Assert.Contains(because, v.Summary);
        Assert.Contains(action, v.SuggestedAction);
        Assert.DoesNotContain("could not be reached", v.Summary);
        Assert.DoesNotContain("reachable from the internet", v.SuggestedAction);
        if (reason == "rate_limit_budget")
            Assert.Contains("until 12:00:30 UTC", v.Summary);
    }

    // ── stoppet før sending ─────────────────────────────────────────────────

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

    [Theory]
    [InlineData("no_retry_origin_not_verified")]
    [InlineData("no_retry_origin_from_a_later_backend")]
    [InlineData(null)]
    public void Origin_not_verified_for_a_reason_verify_does_not_know_points_at_the_error_and_resending(string? reason)
    {
        // Backend #354 (2026-10-05): ingen frist etter et bytte av secret, og hver nekting har sin årsak. En årsak verify ikke
        // kjenner, får det generelle rådet, som ikke gjetter på årsaken.
        DeliveryVerification v = Judge(Event(6, Attempt(null, "OriginNotVerified", kind: "MoveToDlq", reason: reason)));

        Assert.Contains("It could not vouch for this one, and the attempt's error says why.", v.SuggestedAction);
        Assert.Contains("`stripe events resend`", v.SuggestedAction);
        Assert.DoesNotContain("signing secret changed", v.SuggestedAction);
        Assert.DoesNotContain("test event", v.SuggestedAction);
    }

    [Theory]
    [InlineData("no_retry_origin_before_recalculation", "arrived before Stripe signature recalculation was switched on for the queue", "`stripe events resend`")]
    [InlineData("no_retry_origin_no_ingress_record", "it has no record of which secret verified this one", "`stripe events resend`")]
    [InlineData("no_retry_origin_other_secret", "verified with an earlier Stripe secret of the queue's ingress, and that secret can no longer be used", "`stripe events resend`")]
    [InlineData("no_retry_origin_test_event", "a test or sandbox event that never came through the queue's ingress", "have Stripe send an event to the queue's ingress")]
    public void Origin_not_verified_says_why_by_its_reason_and_that_the_event_is_sent_from_Stripe_again(string reason, string why, string remedy)
    {
        DeliveryVerification v = Judge(Event(6, Attempt(null, "OriginNotVerified", "Not signed as Stripe.", kind: "MoveToDlq", reason: reason)));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Contains("The receiver was never contacted. The event is in the DLQ.", v.Summary);
        Assert.Contains(why, v.SuggestedAction);
        Assert.Contains(remedy, v.SuggestedAction);
        Assert.DoesNotContain("the attempt's error says why", v.SuggestedAction);
        Assert.DoesNotContain("signing secret changed", v.SuggestedAction);
    }

    [Theory]
    [InlineData(401, "AuthenticationFailed")]
    [InlineData(403, "AuthorizationFailed")]
    public void A_receiver_that_refuses_the_earlier_secret_gets_the_event_from_Stripe_again_and_the_secret_revoked(int code, string failureClass)
    {
        // Backend #427: mottakeren har gått over til det nye secret-et. Ingenting er galt med oppsettet, så rådet om
        // credentialRef ville sendt brukeren feil vei.
        DeliveryVerification v = Judge(Event(6, Attempt(code, failureClass, kind: "MoveToDlq", reason: "no_retry_signed_with_earlier_secret")));

        Assert.Equal(DeliveryVerdict.Failed, v.Verdict);
        Assert.Contains($"it answered {code} [{failureClass}]. The event is in the DLQ.", v.Summary);
        Assert.Contains("it has moved on to the secret the ingress has now", v.SuggestedAction);
        Assert.Contains("resend it from Stripe instead", v.SuggestedAction);
        Assert.Contains("Revoke the earlier secret in the Queuey console once the receiver no longer accepts it", v.SuggestedAction);
        Assert.DoesNotContain("delivery.credentialRef", v.SuggestedAction);
        Assert.DoesNotContain("allowlist", v.SuggestedAction);
    }

    [Theory]
    [InlineData(null, "OriginNotVerified", "no_retry_origin_before_recalculation", "HoldQueue",
        "Then a person skips the event in the Queuey console, which unlocks the queue: sending it again fails the same way.")]
    [InlineData(401, "AuthenticationFailed", "no_retry_signed_with_earlier_secret", "HoldKey",
        "Then a person skips the event in the Queuey console; until then, events with its key wait. Sending it again fails the same way.")]
    public void With_the_dlq_off_an_event_to_resend_from_Stripe_is_skipped_not_sent_again(
        int? code, string failureClass, string reason, string kind, string step)
    {
        DeliveryVerification v = Judge(Event(4, Attempt(code, failureClass, kind: kind, reason: reason)));

        Assert.Contains(step, v.SuggestedAction);
        Assert.DoesNotContain("sends the event again", v.SuggestedAction);
    }

    [Theory]
    [InlineData("AuthenticationFailed", "Credential 'partner-key' could not be resolved.", "Queuey could not set up the credentials for this delivery, so it never contacted the receiver")]
    [InlineData("RouteOrConfigError", "destination_not_allowed", "it is a private, local or blocked address")]
    public void A_failure_of_Queuey_own_setup_is_not_blamed_on_the_receiver(string failureClass, string error, string advice)
    {
        // Review 2026-10-05: uten svar kom en autentiseringsfeil fra oppsettet av credentialen eller identitetsleverandøren,
        // og en rutefeil fra sperren for utgående trafikk. Verify sa at mottakeren avviste credentialen eller ikke hadde ruten.
        DeliveryVerification v = Judge(Event(4, Attempt(null, failureClass, error, kind: "HoldEvent", reason: "target_requires_action")));

        Assert.StartsWith($"Queuey did not send the event to https://hooks.example.com/orders [{failureClass}]", v.Summary);
        Assert.Contains("The receiver was never contacted.", v.Summary);
        Assert.Contains(advice, v.SuggestedAction);
        Assert.DoesNotContain("The receiver rejected the credentials", v.SuggestedAction);
        Assert.DoesNotContain("has no such route", v.SuggestedAction);
    }

    [Fact]
    public void A_401_from_the_receiver_is_still_the_receiver_rejecting_the_credentials()
    {
        DeliveryVerification v = Judge(Event(4, Attempt(401, "AuthenticationFailed", kind: "HoldEvent", reason: "target_requires_action_auth_failed")));

        Assert.StartsWith("Delivery to https://hooks.example.com/orders failed: it answered 401 [AuthenticationFailed].", v.Summary);
        Assert.StartsWith("The receiver rejected the credentials.", v.SuggestedAction);
    }

    // ── tidsavbrudd ─────────────────────────────────────────────────────────

    [Fact]
    public void A_timeout_on_a_held_queue_says_a_person_resumes_it()
    {
        DeliveryVerification v = Judge(Event(0), new QueueListItem { PublicId = "que_orders", DeliveryHeld = true, HasDeliveryTarget = true });

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("still Received", v.Summary);
        Assert.Contains("A person resumes delivery in the Queuey console", v.SuggestedAction);
    }

    [Fact]
    public void A_timeout_behind_a_failing_event_names_it_and_what_to_fix()
    {
        // Funnet lokalt 2026-09-23: en 401 holdt FIFO-køen, og verify gjettet på en backlog.
        EventDetailsResponse blocker = Event(4, Attempt(401, "AuthenticationFailed", kind: "HoldEvent", reason: "target_requires_action_auth_failed"));
        blocker.PublicId = "evt_head";

        DeliveryVerification v = DeliveryVerifier.Judge("orders", Published, Event(0), null, TimeSpan.FromSeconds(5), blocker);

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("evt_head, is failing — https://hooks.example.com/orders answered 401 [AuthenticationFailed]", v.Summary);
        Assert.Contains("Queuey holds the queue's deliveries until that is dealt with", v.Summary);
        Assert.Contains("delivery.credentialRef", v.SuggestedAction);
        Assert.Contains("a person resumes the queue in the Queuey console", v.SuggestedAction);
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

        EventAttemptResponse named = Held("rate_limit_budget");
        named.Status = JsonSerializer.SerializeToElement("Held");
        Assert.True(named.IsHeld);
    }

    // ── hele løpet mot stubber ───────────────────────────────────────────────

    [Fact]
    public async Task Verify_reads_one_event_first_then_publishes_once_and_follows_the_event_to_its_outcome()
    {
        var calls = new List<string>();
        var ingress = new StubHttpMessageHandler(req =>
        {
            calls.Add("publish");
            return StubHttpMessageHandler.Accepted(new
            {
                queuePublicId = "que_orders", eventId = "evt_1", receivedAtUtc = DateTimeOffset.UnixEpoch, mode = "Deliver", replayed = false,
            });
        });

        // Lesingen først, så: ikke lesbar ennå, i gang, holdt av sendebudsjettet, og levert.
        int reads = 0;
        var api = new StubHttpMessageHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            calls.Add(path + req.RequestUri.Query);
            return path switch
            {
                "/tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
                "/events/que_orders" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { items = Array.Empty<object>() }),
                "/events/que_orders/evt_1" => reads++ switch
                {
                    0 => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, new { error = new { code = "not_found", message = "no" } }),
                    1 => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "evt_1", status = 1, attemptCount = 0, attempts = Array.Empty<object>() }),
                    2 => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        publicId = "evt_1", status = 4, attemptCount = 0,
                        attempts = new[] { new { attemptNumber = 1, targetEndpoint = "https://hooks.example.com/orders", status = 5, failureClass = "None", decisionKind = "HoldTarget", decisionReason = "rate_limit_budget" } },
                    }),
                    _ => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                    {
                        publicId = "evt_1", status = 2, attemptCount = 1,
                        attempts = new object[]
                        {
                            new { attemptNumber = 1, targetEndpoint = "https://hooks.example.com/orders", status = 5, failureClass = "None", decisionKind = "HoldTarget", decisionReason = "rate_limit_budget" },
                            new { attemptNumber = 2, targetEndpoint = "https://hooks.example.com/orders", status = 1, responseCode = 200, durationMs = 90, failureClass = "None", decisionKind = "Delivered", decisionReason = "success" },
                        },
                    }),
                },
                string other => throw new InvalidOperationException(other),
            };
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, ingressStub: ingress);

        DeliveryVerification v = await service.VerifyDeliveryAsync("orders", """{"type":"queuey.verify"}"""u8.ToArray(),
            new VerifyDeliveryOptions { PollInterval = TimeSpan.Zero, Timeout = TimeSpan.FromSeconds(10) });

        // Det holdte forsøket var ikke et utfall: verify ventet til eventen ble levert.
        Assert.Equal(DeliveryVerdict.Delivered, v.Verdict);
        Assert.Equal("evt_1", v.EventId);
        Assert.Equal(1, v.Attempts);
        Assert.Equal(new[] { "/tenants/ten_abc/queues", "/events/que_orders?pageSize=1", "publish" }, calls.Take(3).ToArray());
        Assert.All(calls.Skip(3), c => Assert.Equal("/events/que_orders/evt_1", c));

        // Én publisering, som JSON, med en ny idempotensnøkkel hver gang: ingress slår ellers sammen to verifiseringer
        // og svarer med den første.
        HttpRequestMessage publish = Assert.Single(ingress.Requests);
        Assert.Equal("/events/ten_abc/orders", publish.RequestUri!.AbsolutePath);
        Assert.StartsWith("queuey-verify-", publish.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("application/json", publish.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_key_that_cannot_read_events_is_refused_before_anything_is_published()
    {
        // Review 2026-10-05: verify publiserte først og fant ut etterpå at nøkkelen ikke kunne lese utfallet.
        var ingress = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(WaasTestHost.DefaultPublishBody()));
        var api = new StubHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            _ => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "forbidden", message = "Missing permission event.read." } }),
        });

        QueueyService service = WaasTestHost.Build(apiStub: api, ingressStub: ingress);

        var ex = await Assert.ThrowsAsync<QueueyForbiddenException>(
            () => service.VerifyDeliveryAsync("orders", "{}"u8.ToArray(), new VerifyDeliveryOptions { PollInterval = TimeSpan.Zero }));

        Assert.Contains("This key cannot read events on queue 'orders'", ex.Message);
        Assert.Contains("Nothing was published", ex.Message);
        Assert.Contains("event.read", ex.Message);
        Assert.Empty(ingress.Requests);

        // Ingen profil eller dato: Build fikk event.read først med backend #400, eldre nøkler er ikke fylt inn, og Build i
        // prod har den ikke.
        Assert.DoesNotContain("Build", ex.Message);
        Assert.DoesNotContain("2026", ex.Message);
    }

    [Fact]
    public async Task A_key_that_loses_the_read_after_publishing_is_told_which_permission_verify_needs()
    {
        // Lesingen før publiseringen kan ikke gjøres når køen ikke kan listes. Da er feilen etter publiseringen bakstopperen.
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
            new { error = new { code = "forbidden", message = "Missing permission event.read." } }));

        QueueyService service = WaasTestHost.Build(apiStub: api);

        var ex = await Assert.ThrowsAsync<QueueyForbiddenException>(
            () => service.VerifyDeliveryAsync("orders", "{}"u8.ToArray(), new VerifyDeliveryOptions { PollInterval = TimeSpan.Zero }));

        Assert.Contains("Published evt_1 to 'orders'", ex.Message);
        Assert.Contains("event.read permission on the queue", ex.Message);
        Assert.DoesNotContain("Build", ex.Message);
    }

    /// <summary>
    /// En kø der eventen fortsatt venter, og en eldre event som feiler. Hver lesing verify gjør, har sin
    /// egen rute: før svarte stuben med eventen på alt, også på listen over feilende events — den ble
    /// lest som en side uten elementer, så testen besto uten at det fantes noen feilende event.
    /// </summary>
    private static StubHttpMessageHandler Waiting(
        string? ordering, bool held = false, int aheadCode = 401, string aheadClass = "AuthenticationFailed",
        string? aheadKind = null) => new(req => req.RequestUri!.AbsolutePath switch
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
            attempts = new[] { new { attemptNumber = 3, targetEndpoint = "https://hooks.example.com/orders", status = 2, responseCode = aheadCode, failureClass = aheadClass, decisionKind = aheadKind } },
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
    [InlineData("fifo", null, true)]          // hele køen er én rekke: eventen foran holder denne
    [InlineData("bykey", null, false)]        // den holder bare sin egen nøkkel
    [InlineData("besteffort", null, false)]   // ingen rekkefølge, ingen holder noen
    [InlineData(null, null, false)]           // rekkefølgen kunne ikke leses: ikke gjett
    [InlineData("bykey", "HoldKey", false)]   // DLQ av på en bykey-kø: bare nøkkelen står
    [InlineData("besteffort", "HoldQueue", true)]   // DLQ av: eventen stopper hele køen, uansett ordering
    public async Task A_rejected_event_ahead_is_named_when_it_holds_this_event(string? ordering, string? kind, bool named)
    {
        // En 422 gjelder eventen, ikke mottakeren. Uten DLQ stopper den køen (HoldQueue) eller nøkkelen (HoldKey).
        DeliveryVerification v = await VerifyUntilTimeout(Waiting(ordering, aheadCode: 422, aheadClass: "BadPayload", aheadKind: kind));

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        if (named)
        {
            Assert.Contains("evt_head, is failing", v.Summary);
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
        StubHttpMessageHandler api = Waiting(ordering, aheadKind: "HoldEvent");

        DeliveryVerification v = await VerifyUntilTimeout(api);

        Assert.Equal(DeliveryVerdict.Timeout, v.Verdict);
        Assert.Contains("evt_head, is failing — https://hooks.example.com/orders answered 401 [AuthenticationFailed]", v.Summary);
        Assert.Contains("Queuey holds the queue's deliveries until that is dealt with", v.Summary);
        Assert.Contains("a person resumes the queue in the Queuey console", v.SuggestedAction);

        // Beslutningen avgjør alene, så rekkefølgen trengs ikke.
        Assert.DoesNotContain(api.Requests, r => r.RequestUri!.AbsolutePath == "/queues/que_1/config");
    }
}
