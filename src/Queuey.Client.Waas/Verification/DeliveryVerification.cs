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

        // Tidsavbrudd. To ting forklarer det oftest: en eldre event som feiler og holder køen
        // (ordnet levering), eller køens flyt (levering holdt tilbake). Les begge én gang.
        EventDetailsResponse? blocker = null;
        if (last is null || (last.AttemptCount == 0 && (last.Attempts?.Count ?? 0) == 0))
        {
            try
            {
                if (await controlPlane.GetOldestFailingEventAsync(published.QueuePublicId, cancellationToken).ConfigureAwait(false) is { PublicId: { } id }
                    && id != published.EventId)
                {
                    blocker = await controlPlane.GetEventAsync(published.QueuePublicId, id, cancellationToken).ConfigureAwait(false);
                    blocker.PublicId ??= id;
                }
            }
            catch (QueueyException)
            {
                // Forklaringen er et tillegg; verdiktet står uten den.
            }
        }

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

        return Judge(queueName, published, last, row, options.Timeout, blocker, tenantPublicId);
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
                    + (attempt?.ResponseCode is { } code ? $": {code}" : "")
                    + (attempt?.DurationMs is { } ms ? $" in {ms} ms" : "")
                    + (attempts > 1 ? $", on attempt {attempts}." : "."),
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
            ? $"The event could not be read within {timeout.TotalSeconds:0} s."
            : $"No outcome within {timeout.TotalSeconds:0} s: the event is still {status}.";

        if (blocker?.Attempts?.OrderByDescending(a => a.AttemptNumber).FirstOrDefault() is { } blocking)
            return Result(DeliveryVerdict.Timeout,
                waiting + $" An earlier event on this queue, {blocker.PublicId}, is failing — {Outcome(blocking)}" +
                (string.IsNullOrWhiteSpace(blocking.FailureClass) ? "" : $" [{blocking.FailureClass}]") +
                " — and with ordered delivery the events behind it wait for it.",
                FailureAction(blocking) + " Once that event is delivered or skipped, the events behind it go out.");

        if (queueRow?.DeliveryHeld == true)
            return Result(DeliveryVerdict.Timeout,
                waiting + " Delivery is held on this queue, so events wait until it is resumed.",
                "Resume delivery in the Queuey console. A deploy never resumes a queue someone paused.");

        if (queueRow?.Suspended == true)
            return Result(DeliveryVerdict.Timeout,
                waiting + " The queue is suspended.",
                "Contact Queuey support: a suspended queue does not deliver.");

        if (queueRow is { HasDeliveryTarget: false })
            return Result(DeliveryVerdict.Timeout,
                waiting + " The queue has no destination.",
                $"Give queues.{queue} a delivery.url, or set workspace.delivery.baseUrl, in queuey.deploy.json and run `queuey apply`.");

        return Result(DeliveryVerdict.Timeout,
            waiting + " There may be a backlog ahead of it.",
            $"See the backlog with `queuey metrics {published.QueuePublicId}`, or verify again with a longer --timeout.");
    }

    private static string Outcome(EventAttemptResponse? a)
        => a?.ResponseCode is { } code
            ? $"{a.TargetEndpoint ?? "the receiver"} answered {code}"
            : string.IsNullOrWhiteSpace(a?.ErrorMessage)
                ? $"{a?.TargetEndpoint ?? "the receiver"} did not answer"
                : $"{a!.TargetEndpoint ?? "the receiver"} could not be reached ({a.ErrorMessage})";

    private static string FailureSummary(EventAttemptResponse? a, bool inDlq)
    {
        string target = a?.TargetEndpoint ?? "the receiver";
        string outcome = a?.ResponseCode is { } code
            ? $"it answered {code}"
            : string.IsNullOrWhiteSpace(a?.ErrorMessage) ? "it did not answer" : $"it could not be reached ({a!.ErrorMessage})";
        string cls = string.IsNullOrWhiteSpace(a?.FailureClass) ? "" : $" [{a!.FailureClass}]";
        string then = inDlq
            ? " The event is in the DLQ."
            : " Queuey retries it on the queue's schedule.";
        return $"Delivery to {target} failed: {outcome}{cls}.{then}";
    }

    private static string FailureAction(EventAttemptResponse? a) => a?.FailureClass switch
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
            $"The receiver's side reported it unavailable ({a.ResponseCode}). Check that it is running.",
        "TargetServerError" =>
            "The receiver failed while handling the event. Look at its logs around this event.",
        "TransformFailed" =>
            "Queuey could not transform the payload before sending it. Check the queue's payload mutations in the Queuey console.",
        _ when a?.ResponseCode is null =>
            "Check that the delivery URL is right and reachable from the internet.",
        _ => "Look at this attempt in the Queuey console for the receiver's response.",
    };
}
