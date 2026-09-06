using System;

namespace Queuey.Edge;

/// <summary>
/// What Edge DOES next — six behaviour-keyed members, deliberately not a
/// mirror of HTTP status codes (plan rev 4 F2). The engine dispatches on
/// this and nothing else; the "what actually happened" rides
/// <see cref="TransferReason"/> so diagnosis is never lost even though
/// e.g. 401, 404 and billing all schedule identically.
///
/// FROZEN CONTRACT: values are persisted on spool rows (append-only,
/// never reorder/rename — pinned by an enum-value test).
/// </summary>
public enum TransferClass
{
    /// <summary>2xx, including a dedup replay → settle.</summary>
    Accepted = 0,

    /// <summary>Timeout, DNS, refused, reset, 500/502/503/504/408 → retain, jittered backoff.</summary>
    Transient = 1,

    /// <summary>429, or 503 with Retry-After → retain, wait at least Retry-After (plus a little jitter).</summary>
    Throttled = 2,

    /// <summary>401/403/404/402/409-paused/TLS → retain, slow probe, never hot-loop.</summary>
    RequiresAction = 3,

    /// <summary>400/413/415 — permanent for THIS event → quarantine it; the lane continues.</summary>
    EventRejected = 4,

    /// <summary>Anything unclassifiable → retain, conservative slow retry.</summary>
    Unknown = 5
}

/// <summary>
/// What actually HAPPENED — the diagnosis axis. Logs, health, the CLI and a
/// future fleet surface read this; the engine never does.
///
/// FROZEN CONTRACT: numeric values are persisted (append-only; gaps left
/// between groups on purpose so new reasons join their family).
/// </summary>
public enum TransferReason
{
    Accepted = 0,
    Replayed = 1,

    Timeout = 10,
    DnsFailure = 11,
    ConnectionRefused = 12,
    ConnectionReset = 13,
    CloudServerError = 14,

    RateLimited = 20,

    AuthenticationRejected = 30,
    Forbidden = 31,
    RouteUnknown = 32,
    QueuePaused = 33,
    BillingBlocked = 34,
    TlsFailure = 35,

    MalformedRequest = 40,
    PayloadTooLarge = 41,
    UnsupportedContentType = 42,

    Unclassified = 99
}

/// <summary>
/// Bounded, payload-free proof of what the attempt observed. The snippet is
/// a short excerpt of QUEUEY'S response — never the customer's payload
/// bytes, which Edge does not log anywhere.
/// </summary>
public sealed record TransferEvidence(
    int? StatusCode,
    string? Snippet,
    DateTimeOffset AtUtc,
    int Attempt)
{
    public const int MaxSnippetLength = 256;

    public static TransferEvidence Create(int? statusCode, string? snippet, DateTimeOffset atUtc, int attempt)
        => new(
            statusCode,
            snippet is { Length: > MaxSnippetLength } ? snippet[..MaxSnippetLength] : snippet,
            atUtc,
            attempt);
}

/// <summary>
/// One attempt's classified result: behaviour (<see cref="Class"/>) for the
/// engine, diagnosis (<see cref="Reason"/> + <see cref="Evidence"/>) for
/// humans. Produced in exactly one place (<see cref="ITransferOutcomeClassifier"/>);
/// persisted on the spool row; never re-derived downstream.
/// </summary>
public sealed record TransferOutcome(
    TransferClass Class,
    TransferReason Reason,
    TransferEvidence? Evidence)
{
    /// <summary>Retry-After hint when <see cref="Class"/> is Throttled.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
