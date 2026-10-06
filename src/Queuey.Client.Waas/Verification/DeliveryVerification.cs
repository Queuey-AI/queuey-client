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

    // DeliveryStatus, tall på ledningen (Held = 5), lest leniently som eventens status.
    public JsonElement Status { get; set; }

    // Det Queuey bestemte etter forsøket, med årsaken: RetryLater, HoldEvent, HoldQueue, HoldKey, HoldTarget eller
    // MoveToDlq. Det står på ledningen både i prod og på integration/agents. Før 2026-10-05 gjettet verify ut fra
    // feilklassen, og tok feil når DLQ-en var av, ved tidsavbrudd og ved holdte forsøk.
    public string? DecisionKind { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset? DecisionUntilUtc { get; set; }

    /// <summary>
    /// A row for a delivery Queuey held before sending: the send budget was spent, or the receiver is being probed or
    /// waits for a person. Not a failure: the event is rescheduled, and the receiver was not contacted.
    /// </summary>
    internal bool IsHeld => Status.ValueKind switch
    {
        JsonValueKind.Number => Status.TryGetInt32(out int n) && n == 5,
        JsonValueKind.String => string.Equals(Status.GetString(), "Held", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(DecisionKind, "HoldTarget", StringComparison.Ordinal),
    };

    /// <summary>The failure class, or null for none (a held row and a success carry <c>None</c>).</summary>
    internal string? Class => FailureClass is { Length: > 0 } c && c != "None" ? c : null;
}

/// <summary>
/// Publishes one event and follows it to an outcome. The judging is a pure function of what the API
/// returned (<see cref="Judge"/>), so every verdict and its advice is testable without a clock.
/// </summary>
internal static class DeliveryVerifier
{
    // Årsaken Queuey skriver når et tidsavbrudd mot en mottaker som ikke er merket idempotent, parkerer køen.
    private const string TimeoutNotIdempotent = "target_requires_action_timeout_not_idempotent";

    // Mottakeren avviste (401/403) en Stripe-signatur laget med et tidligere secret på inngangen, det som verifiserte
    // eventen (backend #427, 2026-10-05). Eventen går til DLQ alene; et nytt forsøk signeres med det samme secret-et.
    private const string SignedWithEarlierSecret = "no_retry_signed_with_earlier_secret";

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
        await EnsureEventsCanBeReadAsync(controlPlane, management, tenantPublicId, queueName, cancellationToken).ConfigureAwait(false);

        // A fresh idempotency key per run: ingress collapses a repeated key into the event it already has, and verify
        // would then report the first run's outcome.
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
                // Lesingen ble sjekket før publiseringen, så dette er en tilgang som endret seg underveis.
                throw new QueueyForbiddenException(
                    $"Published {published.EventId} to '{queueName}', but this key cannot read events back, so its outcome " +
                    $"is unknown. Verifying needs the event.read permission on the queue. {ex.Message}",
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
        // - Den holder hele køen: Queuey parkerte målet (HoldEvent), eventen stopper køen med DLQ-en av (HoldQueue), eller
        //   målet holdes for en probe eller en person. Det gjelder uansett ordering.
        // - Hele køen er én rekke: ordering fifo, uten partisjonsnøkkel. Med bykey holder eventen ellers bare sin egen
        //   nøkkel, og med besteffort ingen.
        // Før 2026-09-24 ble den navngitt uansett, og kunne peke på feil årsak. Fram til 2026-10-05 bare på fifo, også når
        // en 401 holdt en bykey-kø.
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

                    if (HoldsTheQueue(failing)
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
    /// Refuses before publishing when the key cannot read the queue's events: the event goes to the real receiver,
    /// and verify could not say what became of it. A cheap read of one event. When the queue cannot be found by name
    /// here, the publish decides, and the read after it is the backstop.
    /// </summary>
    private static async Task EnsureEventsCanBeReadAsync(
        QueueyControlPlaneClient controlPlane, IQueueyManagement management, string? tenantPublicId, string queueName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId))
            return;

        string? queuePublicId;
        try
        {
            queuePublicId = (await management.ListQueuesAsync(tenantPublicId!, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(q => string.Equals(q.DisplayName, queueName, StringComparison.Ordinal))?.PublicId;
        }
        catch (QueueyException)
        {
            return;
        }

        if (queuePublicId is null)
            return;

        try
        {
            await controlPlane.ReadOneEventAsync(queuePublicId, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyForbiddenException ex)
        {
            // Før 2026-10-05 publiserte verify først og fant ut etterpå at nøkkelen ikke kunne lese utfallet.
            throw new QueueyForbiddenException(
                $"This key cannot read events on queue '{queueName}', so verify could not follow the event it sends. Nothing " +
                $"was published. Verifying needs the event.read permission on the queue: use a key that has it. {ex.Message}",
                ex.ErrorCode);
        }
        catch (QueueyException)
        {
            // Bare tilgangen sjekkes her. Andre svar avgjøres av publiseringen og lesingen etter den.
        }
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
    /// what says what to fix. A delivery Queuey held before sending is not an outcome: verify keeps waiting.
    /// </summary>
    internal static bool IsSettled(EventDetailsResponse e) => e.StatusName switch
    {
        "Delivered" or "Logged" or "Filtered" or "Dlq" or "Skipped" or "Sandbox" => true,

        // Et holdt forsøk setter eventen til Failed for å planlegge den på nytt (sendebudsjettet, en mottaker som probes
        // eller venter på en person). Før 2026-10-05 ble det lest som en feil: to verify innenfor samme vindu for
        // sendebudsjettet ga «could not be reached» og exit 1.
        "Failed" => LastAttempt(e) is not { IsHeld: true },
        _ => false,
    };

    internal static DeliveryVerification Judge(
        string queue, PublishResult published, EventDetailsResponse? e, QueueListItem? queueRow, TimeSpan timeout,
        EventDetailsResponse? blocker = null, string? tenant = null)
    {
        EventAttemptResponse? attempt = LastAttempt(e);
        EventAttemptResponse? tried = LastTried(e);
        string? status = e?.StatusName;
        int attempts = Math.Max(e?.AttemptCount ?? 0, e?.Attempts?.Count(a => !a.IsHeld) ?? 0);
        bool held = attempt is { IsHeld: true };

        DeliveryVerification Result(DeliveryVerdict verdict, string summary, string? action) => new()
        {
            Tenant = tenant,
            Queue = queue,
            QueuePublicId = published.QueuePublicId,
            EventId = published.EventId,
            Verdict = verdict,
            Status = status,
            Attempts = attempts,
            Target = (tried ?? attempt)?.TargetEndpoint,
            ResponseCode = tried?.ResponseCode,
            DurationMs = tried?.DurationMs,
            FailureClass = tried?.Class,
            Error = tried?.ErrorMessage,
            Summary = summary,
            SuggestedAction = action,
        };

        switch (held ? null : status)
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
                return Result(DeliveryVerdict.Failed, FailureSummary(attempt, status == "Dlq"), FailureAction(attempt, queue));
        }

        // Ingen utfall innen fristen.
        string waiting = held
            ? FormattableString.Invariant($"No outcome within {timeout.TotalSeconds:0} s: Queuey is holding the event before sending it.")
            : status is null
                ? FormattableString.Invariant($"The event could not be read within {timeout.TotalSeconds:0} s.")
                : FormattableString.Invariant($"No outcome within {timeout.TotalSeconds:0} s: the event is still {status}.");

        // Holdt og suspendert levering først: de stopper hele køen, så en feilende event foran er ikke
        // grunnen til at denne venter.
        if (queueRow?.DeliveryHeld == true)
            return Result(DeliveryVerdict.Timeout,
                waiting + " Delivery is held on this queue, so events wait until it is resumed.",
                "A person resumes delivery in the Queuey console. A deploy never resumes a queue someone paused.");

        if (queueRow?.Suspended == true)
            return Result(DeliveryVerdict.Timeout,
                waiting + " The queue is suspended.",
                "Contact Queuey support: a suspended queue does not deliver.");

        if (held)
            return Result(DeliveryVerdict.Timeout,
                waiting + " " + HeldBecause(attempt!) + (tried is null ? "" : $" Its last attempt: {Outcome(tried)}{Bracketed(tried.Class)}."),
                HeldAction(attempt!));

        if (LastTried(blocker) is { } blocking)
            return Result(DeliveryVerdict.Timeout,
                waiting + $" An earlier event on this queue, {blocker!.PublicId}, is failing — {Outcome(blocking)}" +
                Bracketed(blocking.Class) +
                (HoldsTheQueue(blocker)
                    ? " — and Queuey holds the queue's deliveries until that is dealt with."
                    : " — and with fifo ordering the events behind it wait for it."),
                FailureAction(blocking, queue) + " Once that event is delivered or skipped, the events behind it go out.");

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

    /// <summary>The latest attempt Queuey made, sent or stopped before sending: not a held row.</summary>
    private static EventAttemptResponse? LastTried(EventDetailsResponse? e)
        => e?.Attempts?.Where(a => !a.IsHeld).OrderByDescending(a => a.AttemptNumber).FirstOrDefault();

    private static string Bracketed(string? failureClass) => failureClass is null ? "" : $" [{failureClass}]";

    /// <summary>
    /// Whether the event holds every delivery on its queue, whatever the ordering: by the decision Queuey recorded,
    /// or by the class when there is none. A target held for a probe or a person holds all its events too.
    /// </summary>
    private static bool HoldsTheQueue(EventDetailsResponse e)
    {
        if (LastAttempt(e) is { IsHeld: true } heldRow && heldRow.DecisionReason?.StartsWith("target_", StringComparison.Ordinal) == true)
            return true;

        return LastTried(e) is { } tried && HoldsTheQueue(tried);
    }

    private static bool HoldsTheQueue(EventAttemptResponse a) => a.DecisionKind switch
    {
        "HoldEvent" or "HoldQueue" => true,
        "RetryLater" or "MoveToDlq" or "HoldKey" or "HoldTarget" or "Delivered" => false,
        _ => ParksByClass(a.Class),
    };

    /// <summary>
    /// The classes that park the receiver or the queue for a person, for an attempt without a recorded decision.
    /// </summary>
    private static bool ParksByClass(string? failureClass) => failureClass is
        "AuthenticationFailed" or "AuthorizationFailed" or "RouteOrConfigError" or "ProtocolOrSecurityIssue"
        or "PermanentTargetError" or "TransformFailed" or "SignatureRecalculationFailed";

    /// <summary>
    /// Failures that stop Queuey before it sends anything: the receiver was never contacted, so the outcome is not
    /// the receiver's to explain. A credential or identity-provider setup failure is an authentication failure without
    /// a response, and a destination the egress guard refuses is a route failure without one.
    /// </summary>
    private static bool StoppedBeforeSending(EventAttemptResponse? a) => a?.Class switch
    {
        "TransformFailed" or "OriginNotVerified" or "SignatureRecalculationFailed" => true,
        "AuthenticationFailed" or "RouteOrConfigError" => a.ResponseCode is null,
        _ => false,
    };

    private static string Outcome(EventAttemptResponse? a)
        => StoppedBeforeSending(a)
            ? $"Queuey did not send it to {a!.TargetEndpoint ?? "the receiver"}"
            : a?.ResponseCode is { } code
            ? $"{a.TargetEndpoint ?? "the receiver"} answered {code}"
            : string.IsNullOrWhiteSpace(a?.ErrorMessage)
                ? $"{a?.TargetEndpoint ?? "the receiver"} did not answer"
                : $"{a!.TargetEndpoint ?? "the receiver"} could not be reached ({a.ErrorMessage})";

    private static string FailureSummary(EventAttemptResponse? a, bool inDlq)
    {
        string target = a?.TargetEndpoint ?? "the receiver";
        string cls = Bracketed(a?.Class);
        string then = inDlq ? " The event is in the DLQ." : WhatQueueyDoesNext(a);

        // Stoppet før sending (2026-10-05): «it did not answer» la skylda på en mottaker som aldri ble
        // kontaktet. Feilmeldingen fra forsøket sier hva som stoppet den.
        if (StoppedBeforeSending(a))
            return $"Queuey did not send the event to {target}{cls}"
                   + (string.IsNullOrWhiteSpace(a!.ErrorMessage) ? "" : $": {a.ErrorMessage!.Trim().TrimEnd('.')}")
                   + $". The receiver was never contacted.{then}";

        string outcome = a?.ResponseCode is { } code
            ? $"it answered {code}"
            : string.IsNullOrWhiteSpace(a?.ErrorMessage) ? "it did not answer" : $"it could not be reached ({a!.ErrorMessage})";
        return $"Delivery to {target} failed: {outcome}{cls}.{then}";
    }

    /// <summary>
    /// What Queuey does with a failed event that is not in the DLQ: what it decided after the attempt, or by the class
    /// for an attempt without a decision. A class whose next step depends on more than the class gets no forecast.
    /// </summary>
    // Før 2026-10-05 sa hver feil «Queuey retries it on the queue's schedule», også en 401, der Queuey i stedet holder
    // køen til en person gjenopptar den.
    private static string WhatQueueyDoesNext(EventAttemptResponse? a) => a?.DecisionKind switch
    {
        "MoveToDlq" => " The event is in the DLQ.",
        "HoldEvent" when a.DecisionReason == TimeoutNotIdempotent =>
            " The receiver may have got it, and the queue is not marked idempotent, so Queuey holds the queue rather than " +
            "risk delivering it twice, until a person resumes it.",
        "HoldEvent" => " Queuey holds the queue's deliveries until this is fixed and a person resumes the queue.",
        "HoldQueue" => " With the DLQ off, the event holds the queue: nothing behind it is delivered until a person deals with it.",
        "HoldKey" => " With the DLQ off, the event holds its key: later events with the same key wait, and other keys keep delivering.",
        "RetryLater" when a.Class == "RateLimited" => " Queuey sends it again after the wait the receiver asked for.",
        "RetryLater" => " Queuey sends it again, and if the receiver stays down, probes it and resumes delivering when it answers.",
        _ => a?.Class switch
        {
            "TargetServerError" or "TargetUnavailable" =>
                " Queuey sends it again, and if the receiver stays down, probes it and resumes delivering when it answers.",
            "RateLimited" => " Queuey sends it again after the wait the receiver asked for.",
            { } c when ParksByClass(c) => " Queuey holds the queue's deliveries until this is fixed and a person resumes the queue.",
            _ => "",
        },
    };

    /// <summary>
    /// What to change, and when the queue waits for a person, the step after the fix. Without that step the next
    /// verify waits behind the held queue and times out, even with the cause fixed.
    /// </summary>
    private static string FailureAction(EventAttemptResponse? a, string queue)
    {
        // Et tidsavbrudd mot en mottaker som ikke er merket idempotent, parkerer køen med standardvalgene (idempotent false,
        // timeoutBehavior Hold). Rådet før 2026-10-05 var å sjekke URL-en, og at Queuey sendte den igjen.
        if (a?.DecisionKind == "HoldEvent" && a.DecisionReason == TimeoutNotIdempotent)
            return $"The receiver did not answer within the delivery timeout. If it handles the same event twice safely, declare " +
                   $"\"idempotent\": true on queues.{queue}; if it is only slow, raise delivery.timeoutMs. Run `queuey apply`, " +
                   "then a person resumes the queue in the Queuey console.";

        string fix = WhatToFix(a);

        // Et event Queuey ikke kunne signere som Stripe, eller som er signert med et secret mottakeren har forlatt, feiler
        // likt hver gang det sendes på nytt: det sendes fra Stripe igjen, og den som låser opp, hopper over det.
        bool resendFromStripe = a?.Class == "OriginNotVerified" || a?.DecisionReason == SignedWithEarlierSecret;
        return a?.DecisionKind switch
        {
            "HoldEvent" => fix + PersonResumes,
            "HoldQueue" when resendFromStripe =>
                fix + " Then a person skips the event in the Queuey console, which unlocks the queue: sending it again fails the " +
                "same way. With \"dlqEnabled\": true, such an event goes to the DLQ instead, and the queue keeps delivering.",
            "HoldQueue" => fix + " Then a person unlocks the queue in the Queuey console, which sends the event again, or skips it. " +
                           "With \"dlqEnabled\": true, an event the receiver rejects goes to the DLQ instead, and the queue keeps delivering.",
            "HoldKey" when resendFromStripe =>
                fix + " Then a person skips the event in the Queuey console; until then, events with its key wait. Sending it again " +
                "fails the same way.",
            "HoldKey" => fix + " Then a person sends the event again or skips it in the Queuey console; until then, events with its key wait. " +
                         "With \"dlqEnabled\": true, an event the receiver rejects goes to the DLQ instead.",
            "RetryLater" or "MoveToDlq" => fix,
            _ => ParksByClass(a?.Class) ? fix + PersonResumes : fix,
        };
    }

    // Verify & resume finnes i konsollet. REST-ruten krever en person, og MCP krever target.write, så en nøkkel kan ikke
    // gjøre det (review 2026-10-05).
    private const string PersonResumes =
        " Then a person resumes the queue in the Queuey console (Verify & resume): until then Queuey holds its deliveries, " +
        "so verifying again waits.";

    // Samme regel som plan og apply (CredentialStoring, review av #58, K2): verify vet ikke workspacets miljø, og et ukjent
    // miljø regnes som prod, så en person limer inn verdien, og set nevnes for en verdi den som kjører, holder. Ingen profil:
    // biblioteket vet ikke hvilken den som kalte, brukte, og CLI-ens verify går gjennom flytverifiseringen (re-review av #59).
    private static readonly string StoreAgain =
        "Store a new secret under the same name. " + new CredentialStoring(environment: null, profile: null).HowToStore(CredentialStoring.Placeholder, null);

    private static string WhatToFix(EventAttemptResponse? a) => WhatToFixFor(a?.DecisionReason) ?? a?.Class switch
    {
        // Uten svar kom feilen fra Queuey sitt oppsett av autentiseringen (credential eller identitetsleverandør), ikke fra
        // mottakeren (review 2026-10-05).
        "AuthenticationFailed" when a.ResponseCode is null =>
            "Queuey could not set up the credentials for this delivery, so it never contacted the receiver. Check " +
            "delivery.credentialRef and the stored credential, or for OAuth2 the identity provider it asks for a token. " +
            StoreAgain,
        "AuthenticationFailed" =>
            "The receiver rejected the credentials. Check delivery.authMode and delivery.credentialRef (or signing) " +
            "against what the receiver expects. " + StoreAgain,
        "AuthorizationFailed" =>
            "The receiver refused the request (403). Check its permissions or IP allowlist for Queuey's deliveries.",
        // Uten svar stoppet Queuey sin egen sperre for utgående trafikk adressen (destination_not_allowed).
        "RouteOrConfigError" when a.ResponseCode is null =>
            $"Queuey does not send to {a.TargetEndpoint ?? "this address"}: it is a private, local or blocked address. Point " +
            "the delivery URL at a public address, or receive on your machine with `queuey listen`.",
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
        // En årsak verify ikke kjenner, også no_retry_origin_not_verified, som backenden nå bare bruker for en kropp Stripe
        // aldri sender, og som eldre forsøk har uansett årsak. Rådet før sa «just after the signing secret changed», en
        // frist på 30 sekunder backenden ikke har lenger (#354, re-review 2026-10-05).
        "OriginNotVerified" =>
            "Queuey re-signs this queue's deliveries as Stripe, and only for events it can vouch Stripe sent through the " +
            "queue's ingress. It could not vouch for this one, and the attempt's error says why. " + ResendFromStripe,
        "ProtocolOrSecurityIssue" =>
            "The secure connection to the receiver failed. Check the delivery URL's scheme, and the receiver's certificate and the TLS version it requires.",
        "PermanentTargetError" =>
            "The receiver says this request will never succeed as it is. Have whoever runs it accept it, or point the delivery URL at a receiver that does.",
        _ when a?.ResponseCode is null =>
            "Check that the delivery URL is right and reachable from the internet.",
        _ => "Look at this attempt in the Queuey console for the receiver's response.",
    };

    private const string ResendFromStripe =
        "Resend it from Stripe: the event's Resend button in the Dashboard, or `stripe events resend`.";

    /// <summary>
    /// What to fix for a decision reason that says more than its class: why Queuey could not sign the event as Stripe, or
    /// why the receiver refused the signature it made. Null for any other reason, so the class decides.
    /// </summary>
    // Backend #354 (2026-10-05): hver nekting har sin årsak, og ingen frist etter et bytte av secret. #427: mottakeren
    // avviser et event signert med et tidligere secret. Rådet er det samme som konsollets, for hver årsak.
    private static string? WhatToFixFor(string? reason) => reason switch
    {
        "no_retry_origin_before_recalculation" =>
            "This event arrived before Stripe signature recalculation was switched on for the queue, so Queuey does not sign " +
            "it as Stripe, and sending it again cannot change that. " + ResendFromStripe + " It arrives again, and is signed.",
        "no_retry_origin_no_ingress_record" =>
            "Queuey signs an event as Stripe only with the secret that verified it at the queue's ingress, and it has no record " +
            "of which secret verified this one: the ingress accepted it without a Stripe signature check, it was copied from " +
            "another queue, its body was changed, or it arrived before Queuey kept the record. " + ResendFromStripe +
            " It arrives through the ingress again, which records the secret.",
        "no_retry_origin_other_secret" =>
            "This event was verified with an earlier Stripe secret of the queue's ingress, and that secret can no longer be " +
            "used: it was deleted, revoked or has expired. Queuey signs an event only with the secret that verified it. " +
            ResendFromStripe + " It arrives again, verified with the secret the ingress has now.",
        // Queuey F2.9: credentialen som verifiserte eventet, har fått en ny hemmelighet under samme id siden, så Queuey
        // signerer det ikke med den som står nå.
        "no_retry_origin_secret_replaced" =>
            "The credential that verified this event at the queue's ingress has had its secret replaced since, so the secret it " +
            "holds now is not the one that verified the event, and Queuey signs an event only with that one. " + ResendFromStripe +
            " It arrives again, verified with the secret the credential holds now.",
        "no_retry_origin_test_event" =>
            "This is a test or sandbox event that never came through the queue's ingress, so Stripe never sent it and Queuey " +
            "does not sign it as Stripe. To try the receiver, have Stripe send an event to the queue's ingress, for example " +
            "with `stripe trigger` in test mode.",
        SignedWithEarlierSecret =>
            "The receiver refused the Stripe signature Queuey made with the earlier secret that verified this event at the " +
            "queue's ingress: it has moved on to the secret the ingress has now, and nothing is wrong with its setup. Sending " +
            "the event again signs it with the same earlier secret, so resend it from Stripe instead: the event's Resend " +
            "button in the Dashboard, or `stripe events resend`. Revoke the earlier secret in the Queuey console once the " +
            "receiver no longer accepts it, so no event it verified is signed with it again; after a leaked secret, revoke it at once.",
        _ => null,
    };

    /// <summary>Why Queuey holds the event before sending it, from the held row's reason.</summary>
    private static string HeldBecause(EventAttemptResponse held)
    {
        string until = held.DecisionUntilUtc is { } at ? $", until {at.UtcDateTime:HH:mm:ss} UTC" : "";
        return held.DecisionReason switch
        {
            "rate_limit_budget" =>
                $"The send budget for this endpoint (delivery.rateLimit) is spent for this window, so Queuey holds it without contacting the receiver{until}.",
            "target_open" or "target_probe_in_flight" =>
                "The receiver failed several times in a row, so Queuey holds the queue's deliveries and probes it.",
            "target_requires_action" =>
                "An earlier failure parked the receiver, so Queuey holds the queue's deliveries until a person resumes the queue.",
            { Length: > 0 } reason => $"Queuey holds it ({reason}).",
            _ => "Queuey holds it.",
        };
    }

    private static string HeldAction(EventAttemptResponse held) => held.DecisionReason switch
    {
        "rate_limit_budget" =>
            "The event goes out when the window resets. Verify again then, or raise delivery.rateLimit if the pacing is too slow.",
        "target_open" or "target_probe_in_flight" =>
            "Find out why the receiver fails. Queuey sends the held events when a probe gets through.",
        "target_requires_action" =>
            "Fix what the earlier failure names, then a person resumes the queue in the Queuey console (Verify & resume).",
        _ => "Look at the event in the Queuey console.",
    };
}
