using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>What happened to the event <see cref="IQueueyService.VerifyDeliveryAsync"/> sent.</summary>
public enum DeliveryVerdict
{
    /// <summary>The receiver answered 2xx.</summary>
    Delivered,

    /// <summary>Stored without being sent — the queue is in logOnly mode.</summary>
    LoggedNotDelivered,

    /// <summary>Stored without being sent — the queue's delivery filter did not match.</summary>
    Filtered,

    /// <summary>Queuey tried and the receiver refused, errored or could not be reached.</summary>
    Failed,

    /// <summary>No outcome within the timeout.</summary>
    Timeout,
}

/// <summary>Text forms of <see cref="DeliveryVerdict"/>.</summary>
public static class DeliveryVerdicts
{
    /// <summary>The stable text an agent or a script matches on: <c>delivered</c>, <c>logged_not_delivered</c>, …</summary>
    public static string ToText(this DeliveryVerdict verdict) => verdict switch
    {
        DeliveryVerdict.Delivered => "delivered",
        DeliveryVerdict.LoggedNotDelivered => "logged_not_delivered",
        DeliveryVerdict.Filtered => "filtered",
        DeliveryVerdict.Failed => "failed",
        _ => "timeout",
    };
}

/// <summary>Options for <see cref="IQueueyService.VerifyDeliveryAsync"/>.</summary>
public sealed class VerifyDeliveryOptions
{
    /// <summary>How long to wait for an outcome. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the event is read while waiting. Default 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The payload's content type. Default <c>application/json</c>.</summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>The event type to publish with, when the queue reads it from a header.</summary>
    public string? EventType { get; set; }
}

/// <summary>
/// The outcome of one verification: the event, what became of it, and — when it was not delivered —
/// what to change. <see cref="SuggestedAction"/> names the setting, so an agent can act on it.
/// </summary>
public sealed class DeliveryVerification
{
    /// <summary>The workspace (<c>ten_…</c>) the event was published to.</summary>
    public string? Tenant { get; init; }

    /// <summary>The queue name that was published to.</summary>
    public string Queue { get; init; } = default!;

    /// <summary>The queue's public id (<c>que_…</c>).</summary>
    public string? QueuePublicId { get; init; }

    /// <summary>The event's public id (<c>evt_…</c>).</summary>
    public string? EventId { get; init; }

    /// <summary>What happened.</summary>
    public DeliveryVerdict Verdict { get; init; }

    /// <summary>True when the receiver got the event.</summary>
    public bool Delivered => Verdict == DeliveryVerdict.Delivered;

    /// <summary>The event's status in Queuey: <c>Delivered</c>, <c>Logged</c>, <c>Filtered</c>, <c>Failed</c>, <c>Dlq</c>, …</summary>
    public string? Status { get; init; }

    /// <summary>How many delivery attempts were made.</summary>
    public int Attempts { get; init; }

    /// <summary>Where the last attempt was sent.</summary>
    public string? Target { get; init; }

    /// <summary>The receiver's status code on the last attempt, when it answered.</summary>
    public int? ResponseCode { get; init; }

    /// <summary>How long the last attempt took, in milliseconds.</summary>
    public int? DurationMs { get; init; }

    /// <summary>Queuey's classification of the last failure, e.g. <c>AuthenticationFailed</c>.</summary>
    public string? FailureClass { get; init; }

    /// <summary>The last attempt's error, when there was no response to report.</summary>
    public string? Error { get; init; }

    /// <summary>One sentence on what happened.</summary>
    public string Summary { get; init; } = default!;

    /// <summary>What to change, when the event was not delivered.</summary>
    public string? SuggestedAction { get; init; }
}

// ── wire ──────────────────────────────────────────────────────────────────────

/// <summary>Wire shape of <c>GET /events/{que}/{evt}</c> — the envelope and the attempts, no payload.</summary>
internal sealed class EventDetailsResponse
{
    public string? PublicId { get; set; }
    public string? QueuePublicId { get; set; }

    // Tall på ledningen (EventStatus), men lest leniently: et navn virker også, og en verdi
    // klienten ikke kjenner blir ukjent i stedet for en feil som stopper verify.
    public JsonElement Status { get; set; }

    public int AttemptCount { get; set; }
    public string? HoldReason { get; set; }
    public List<EventAttemptResponse>? Attempts { get; set; }

    internal string? StatusName => Status.ValueKind switch
    {
        JsonValueKind.Number when Status.TryGetInt32(out int n) => n switch
        {
            0 => "Received",
            1 => "InProgress",
            2 => "Delivered",
            3 => "Logged",
            4 => "Failed",
            5 => "Sandbox",
            6 => "Dlq",
            7 => "Skipped",
            8 => "Filtered",
            _ => null,
        },
        JsonValueKind.String => Status.GetString(),
        _ => null,
    };
}

/// <summary>Wire shape of a page from <c>GET /events/{que}</c> — only what verify reads.</summary>
internal sealed class EventListPageResponse
{
    public List<EventListItemResponse>? Items { get; set; }
}

internal sealed class EventListItemResponse
{
    public string? PublicId { get; set; }
    public int AttemptCount { get; set; }
}

internal sealed class EventAttemptResponse
{
    public int AttemptNumber { get; set; }
    public string? TargetEndpoint { get; set; }
    public int? ResponseCode { get; set; }
    public int? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FailureClass { get; set; }
}

/// <summary>
/// Publishes one event and follows it to an outcome. The judging is a pure function of what the API
/// returned (<see cref="Judge"/>), so every verdict and its advice is testable without a clock.
/// </summary>
internal static class DeliveryVerifier
{
    public static async Task<DeliveryVerification> RunAsync(
        QueueyClient client,
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        string? tenantPublicId,
        string queueName,
        byte[] payload,
        VerifyDeliveryOptions options,
        CancellationToken cancellationToken)
    {
        // A fresh idempotency key per run: an idempotent queue would otherwise collapse a second
        // verification into the first and report the first one's outcome.
        PublishResult published = await client.Ingress.PublishAsync(queueName, payload, new PublishOptions
        {
            ContentType = options.ContentType,
            EventType = options.EventType,
            IdempotencyKey = "queuey-verify-" + Guid.NewGuid().ToString("N"),
        }, cancellationToken).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        EventDetailsResponse? last = null;

        while (true)
        {
            try
            {
                last = await controlPlane.GetEventAsync(published.QueuePublicId, published.EventId, cancellationToken).ConfigureAwait(false);
            }
            catch (QueueyNotFoundException)
            {
                // Et nettopp akseptert event kan være et øyeblikk unna å kunne leses; vent som ellers.
            }
            catch (QueueyForbiddenException ex)
            {
                // Eventen er alt sendt; si hvorfor utfallet ikke kan leses, og hva som hjelper.
                throw new QueueyForbiddenException(
                    $"Published {published.EventId} to '{queueName}', but this key cannot read events back, so its outcome " +
                    "is unknown. Verifying needs the event.read permission: a deploy key made with the Build profile has " +
                    $"it (keys made before 2026-09-23 do not). {ex.Message}",
                    ex.ErrorCode);
            }

            if (last is not null && IsSettled(last))
                return Judge(queueName, published, last, null, options.Timeout, tenant: tenantPublicId);

            if (clock.Elapsed + options.PollInterval > options.Timeout)
                break;

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // Tidsavbrudd. Køens flyt leses først: holdt eller suspendert levering forklarer ventingen
        // uansett hva som ligger foran eventen, og da er en eldre feilende event ikke årsaken.
        QueueListItem? row = null;
        if (!string.IsNullOrWhiteSpace(tenantPublicId))
        {
            try
            {
                row = (await management.ListQueuesAsync(tenantPublicId!, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(q => q.PublicId == published.QueuePublicId);
            }
            catch (QueueyException)
            {
                // Forklaringen er et tillegg; verdiktet står uten den.
            }
        }

        // En eldre feilende event holder eventene bak seg i to tilfeller:
        // - Feilen parkerer målet eller køen (401, 403, 404, TLS, en permanent feil, transform eller
        //   Stripe-signaturen). Da holder Queuey hele køen til noen gjenopptar den, uansett ordering.
        // - Hele køen er én rekke: ordering fifo, uten partisjonsnøkkel. Med bykey holder eventen ellers
        //   bare sin egen nøkkel, og med besteffort ingen.
        // Før 2026-09-24 ble den navngitt uansett, og kunne peke på feil årsak. Fram til 2026-10-05 bare på
        // fifo, også når en 401 holdt en bykey-kø.
        EventDetailsResponse? blocker = null;
        bool flowExplains = row is { DeliveryHeld: true } or { Suspended: true };
        bool notTriedYet = last is null || (last.AttemptCount == 0 && (last.Attempts?.Count ?? 0) == 0);
        if (!flowExplains && notTriedYet)
        {
            try
            {
                if (await controlPlane.GetOldestFailingEventAsync(published.QueuePublicId, cancellationToken).ConfigureAwait(false) is { PublicId: { } id }
                    && id != published.EventId)
                {
                    EventDetailsResponse failing = await controlPlane.GetEventAsync(published.QueuePublicId, id, cancellationToken).ConfigureAwait(false);
                    failing.PublicId ??= id;

                    if (HoldsTheQueue(LastAttempt(failing)?.FailureClass)
                        || await OrderingAsync(controlPlane, published.QueuePublicId, cancellationToken).ConfigureAwait(false) == "fifo")
                        blocker = failing;
                }
            }
            catch (QueueyException)
            {
                // Forklaringen er et tillegg; verdiktet står uten den.
            }
        }

        return Judge(queueName, published, last, row, options.Timeout, blocker, tenantPublicId);
    }

    /// <summary>
    /// The queue's effective ordering — <c>fifo</c>, <c>bykey</c> or <c>besteffort</c> — or null when it
    /// cannot be read. Unknown is treated as "not one lane", so no event is blamed on a guess.
    /// </summary>
    private static async Task<string?> OrderingAsync(QueueyControlPlaneClient controlPlane, string queuePublicId, CancellationToken cancellationToken)
    {
        try
        {
            QueueConfigResponse config = await controlPlane.GetQueueConfigAsync(queuePublicId, cancellationToken).ConfigureAwait(false);
            return config.Policy?.Ordering?.Trim().ToLowerInvariant();
        }
        catch (QueueyException)
        {
            return null;
        }
    }

    /// <summary>
    /// Done waiting: a terminal status, or a failed attempt. A failure is reported on the first
    /// attempt rather than after every retry — the retries can take hours, and the first answer is
    /// what says what to fix.
    /// </summary>
    internal static bool IsSettled(EventDetailsResponse e) => e.StatusName switch
    {
        "Delivered" or "Logged" or "Filtered" or "Dlq" or "Skipped" or "Sandbox" => true,
        "Failed" => true,
        _ => false,
    };

    internal static DeliveryVerification Judge(
        string queue, PublishResult published, EventDetailsResponse? e, QueueListItem? queueRow, TimeSpan timeout,
        EventDetailsResponse? blocker = null, string? tenant = null)
    {
        EventAttemptResponse? attempt = e?.Attempts?.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();
        string? status = e?.StatusName;
        int attempts = Math.Max(e?.AttemptCount ?? 0, e?.Attempts?.Count ?? 0);

        DeliveryVerification Result(DeliveryVerdict verdict, string summary, string? action) => new()
        {
            Tenant = tenant,
            Queue = queue,
            QueuePublicId = published.QueuePublicId,
            EventId = published.EventId,
            Verdict = verdict,
            Status = status,
            Attempts = attempts,
            Target = attempt?.TargetEndpoint,
            ResponseCode = attempt?.ResponseCode,
            DurationMs = attempt?.DurationMs,
            FailureClass = attempt?.FailureClass,
            Error = attempt?.ErrorMessage,
            Summary = summary,
            SuggestedAction = action,
        };

        switch (status)
        {
            case "Delivered":
                return Result(DeliveryVerdict.Delivered,
                    $"Delivered to {attempt?.TargetEndpoint ?? "the receiver"}"
                    + (attempt?.ResponseCode is { } code ? FormattableString.Invariant($": {code}") : "")
                    + (attempt?.DurationMs is { } ms ? FormattableString.Invariant($" in {ms} ms") : "")
                    + (attempts > 1 ? FormattableString.Invariant($", on attempt {attempts}.") : "."),
                    null);

            case "Logged":
                return Result(DeliveryVerdict.LoggedNotDelivered,
                    "The queue is in logOnly mode: the event was stored as Logged and never sent.",
                    $"To deliver, set \"mode\": \"deliver\" on queues.{queue} in queuey.deploy.json. The queue needs a destination — " +
                    "its own delivery.url, or workspace.delivery.baseUrl — then run `queuey apply` and verify again.");

            case "Sandbox":
                return Result(DeliveryVerdict.LoggedNotDelivered,
                    "The event went to the sandbox stream, which only simulates delivery.",
                    "Check the queue in the Queuey console: a verification publishes to the production route.");

            case "Filtered":
                return Result(DeliveryVerdict.Filtered,
                    "The queue's delivery filter did not match this event: it was stored as Filtered and never sent.",
                    $"Verify with data the filter matches, or change queues.{queue}.filter in queuey.deploy.json.");

            case "Skipped":
                return Result(DeliveryVerdict.Failed,
                    "Someone skipped the event before it was delivered.",
                    "Verify again.");

            case "Failed":
            case "Dlq":
                return Result(DeliveryVerdict.Failed, FailureSummary(attempt, status == "Dlq"), FailureAction(attempt));
        }

        // Ingen utfall innen fristen.
        string waiting = status is null
            ? FormattableString.Invariant($"The event could not be read within {timeout.TotalSeconds:0} s.")
            : FormattableString.Invariant($"No outcome within {timeout.TotalSeconds:0} s: the event is still {status}.");

        // Holdt og suspendert levering først: de stopper hele køen, så en feilende event foran er ikke
        // grunnen til at denne venter.
        if (queueRow?.DeliveryHeld == true)
            return Result(DeliveryVerdict.Timeout,
                waiting + " Delivery is held on this queue, so events wait until it is resumed.",
                "Resume delivery in the Queuey console. A deploy never resumes a queue someone paused.");

        if (queueRow?.Suspended == true)
            return Result(DeliveryVerdict.Timeout,
                waiting + " The queue is suspended.",
                "Contact Queuey support: a suspended queue does not deliver.");

        if (LastAttempt(blocker) is { } blocking)
            return Result(DeliveryVerdict.Timeout,
                waiting + $" An earlier event on this queue, {blocker!.PublicId}, is failing — {Outcome(blocking)}" +
                (string.IsNullOrWhiteSpace(blocking.FailureClass) ? "" : $" [{blocking.FailureClass}]") +
                (HoldsTheQueue(blocking.FailureClass)
                    ? " — and Queuey holds the queue's deliveries until that is fixed and the queue is resumed."
                    : " — and with fifo ordering the events behind it wait for it."),
                FailureAction(blocking) + " Once that event is delivered or skipped, the events behind it go out.");

        if (queueRow is { HasDeliveryTarget: false })
            return Result(DeliveryVerdict.Timeout,
                waiting + " The queue has no destination.",
                $"Give queues.{queue} a delivery.url, or set workspace.delivery.baseUrl, in queuey.deploy.json and run `queuey apply`.");

        return Result(DeliveryVerdict.Timeout,
            waiting + " There may be a backlog ahead of it.",
            $"See the backlog with `queuey metrics {published.QueuePublicId}`, or verify again with a longer --timeout.");
    }

    private static EventAttemptResponse? LastAttempt(EventDetailsResponse? e)
        => e?.Attempts?.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();

    /// <summary>
    /// Failures that stop Queuey before it sends anything: the receiver was never contacted, so the
    /// outcome is not the receiver's to explain.
    /// </summary>
    private static bool StoppedBeforeSending(string? failureClass)
        => failureClass is "TransformFailed" or "OriginNotVerified" or "SignatureRecalculationFailed";

    /// <summary>
    /// Failures that park the receiver or the queue for a person: Queuey holds every delivery on the
    /// queue, whatever its ordering, until someone fixes the cause and resumes the queue.
    /// </summary>
    private static bool HoldsTheQueue(string? failureClass) => failureClass is
        "AuthenticationFailed" or "AuthorizationFailed" or "RouteOrConfigError" or "ProtocolOrSecurityIssue"
        or "PermanentTargetError" or "TransformFailed" or "SignatureRecalculationFailed";

    private static string Outcome(EventAttemptResponse? a)
        => StoppedBeforeSending(a?.FailureClass)
            ? $"Queuey did not send it to {a!.TargetEndpoint ?? "the receiver"}"
            : a?.ResponseCode is { } code
            ? $"{a.TargetEndpoint ?? "the receiver"} answered {code}"
            : string.IsNullOrWhiteSpace(a?.ErrorMessage)
                ? $"{a?.TargetEndpoint ?? "the receiver"} did not answer"
                : $"{a!.TargetEndpoint ?? "the receiver"} could not be reached ({a.ErrorMessage})";

    private static string FailureSummary(EventAttemptResponse? a, bool inDlq)
    {
        string target = a?.TargetEndpoint ?? "the receiver";
        string cls = string.IsNullOrWhiteSpace(a?.FailureClass) ? "" : $" [{a!.FailureClass}]";
        string then = inDlq ? " The event is in the DLQ." : WhatQueueyDoesNext(a?.FailureClass);

        // Stoppet før sending (2026-10-05): «it did not answer» la skylda på en mottaker som aldri ble
        // kontaktet. Feilmeldingen fra forsøket sier hva som stoppet den.
        if (StoppedBeforeSending(a?.FailureClass))
            return $"Queuey did not send the event to {target}{cls}"
                   + (string.IsNullOrWhiteSpace(a!.ErrorMessage) ? "" : $": {a.ErrorMessage!.Trim().TrimEnd('.')}")
                   + $". The receiver was never contacted.{then}";

        string outcome = a?.ResponseCode is { } code
            ? $"it answered {code}"
            : string.IsNullOrWhiteSpace(a?.ErrorMessage) ? "it did not answer" : $"it could not be reached ({a!.ErrorMessage})";
        return $"Delivery to {target} failed: {outcome}{cls}.{then}";
    }

    /// <summary>
    /// What Queuey does with a failed event that is not in the DLQ, by its class. A class whose next step
    /// depends on more than the class (the DLQ setting, the ordering) gets no forecast.
    /// </summary>
    // Før 2026-10-05 sa hver feil «Queuey retries it on the queue's schedule», også en 401, der Queuey i stedet
    // holder køen til en person gjenopptar den.
    private static string WhatQueueyDoesNext(string? failureClass) => failureClass switch
    {
        "TargetServerError" or "TargetUnavailable" =>
            " Queuey sends it again, and if the receiver stays down, probes it and resumes delivering when it answers.",
        "RateLimited" =>
            " Queuey sends it again after the wait the receiver asked for.",
        _ when HoldsTheQueue(failureClass) =>
            " Queuey holds the queue's deliveries until this is fixed and the queue is resumed.",
        _ => "",
    };

    /// <summary>
    /// What to change, and for a failure that holds the queue, the step after the fix. Without that step
    /// the next verify waits behind the held queue and times out, even with the cause fixed.
    /// </summary>
    private static string FailureAction(EventAttemptResponse? a)
        => WhatToFix(a) + (HoldsTheQueue(a?.FailureClass)
            ? " Then resume the queue with Verify & resume, in the Queuey console or over MCP: until then Queuey holds its deliveries, so verifying again waits."
            : "");

    private static string WhatToFix(EventAttemptResponse? a) => a?.FailureClass switch
    {
        "AuthenticationFailed" =>
            "The receiver rejected the credentials. Check delivery.authMode and delivery.credentialRef (or signing) " +
            "against what the receiver expects; `queuey credentials set` stores a new secret under the same name.",
        "AuthorizationFailed" =>
            "The receiver refused the request (403). Check its permissions or IP allowlist for Queuey's deliveries.",
        "RouteOrConfigError" =>
            $"The receiver has no such route. Check the delivery URL: {a.TargetEndpoint}.",
        "BadPayload" or "ContentTypeMismatch" or "PayloadTooLarge" =>
            "The receiver rejected the payload. Check that it accepts this body and content type.",
        "RateLimited" =>
            "The receiver is rate-limiting Queuey. The event is retried after the wait it asked for.",
        "TargetUnavailable" when a.ResponseCode is null =>
            $"Queuey could not reach {a.TargetEndpoint}. Check the URL, and that the receiver is reachable from the internet — localhost is not.",
        "TargetUnavailable" =>
            FormattableString.Invariant($"The receiver's side reported it unavailable ({a.ResponseCode}). Check that it is running."),
        "TargetServerError" =>
            "The receiver failed while handling the event. Look at its logs around this event.",
        "TransformFailed" =>
            "Queuey could not transform the payload before sending it. Check the queue's payload mutations in the Queuey console.",

        // OriginNotVerified og SignatureRecalculationFailed kom i backenden etter at verify ble skrevet (2026-09-24 og -25),
        // og TLS og permanente feil manglet. Alle fire endte i rådet om en URL som ikke var nåbar, også når Queuey selv
        // holdt eventen (2026-10-05).
        "SignatureRecalculationFailed" =>
            "Queuey could not recalculate the Stripe signature on this queue, so it held the delivery. Fix what the error names: " +
            "turn the queue's Stripe verification back on, remove its payload mutations, or replace the signing secret.",
        "OriginNotVerified" =>
            "This queue re-signs its deliveries as the provider, and Queuey signs only what the provider sent through the queue's " +
            "verified ingress, which a test event is not. Prove delivery with an event from the provider instead: for Stripe, " +
            "the event's Resend button in the Dashboard or `stripe events resend`.",
        "ProtocolOrSecurityIssue" =>
            "The secure connection to the receiver failed. Check the delivery URL's scheme, and the receiver's certificate and the TLS version it requires.",
        "PermanentTargetError" =>
            "The receiver says this request will never succeed as it is. Have whoever runs it accept it, or point the delivery URL at a receiver that does.",
        _ when a?.ResponseCode is null =>
            "Check that the delivery URL is right and reachable from the internet.",
        _ => "Look at this attempt in the Queuey console for the receiver's response.",
    };
}
