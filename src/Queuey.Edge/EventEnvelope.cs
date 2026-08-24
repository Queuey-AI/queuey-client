using System;

namespace Queuey.Edge;

/// <summary>
/// The persisted shape of one accepted event — what the spool stores and the
/// transfer loop sends. VERSIONED FROM ROW ONE (the house rule for persisted
/// contracts): <see cref="CurrentVersion"/> is written with every row;
/// fields are append-only; an Edge upgrade must read every version it ever
/// wrote (spool-written-by-v1, drained-by-v2 is a release-gate test).
/// </summary>
public sealed record EventEnvelope(
    /// <summary>Envelope schema version this row was written with.</summary>
    int Version,
    /// <summary>The permanent transfer identity — the wire Idempotency-Key.</summary>
    string TransferId,
    /// <summary>Target queue display name (the ingress route segment).</summary>
    string Queue,
    /// <summary>Tenant public id (<c>ten_…</c>) the event publishes under.</summary>
    string TenantPublicId,
    /// <summary>Payload content type as it will be sent.</summary>
    string ContentType,
    /// <summary>Opaque payload bytes. Edge never inspects them.</summary>
    byte[] Payload,
    /// <summary>When the application published (asserted to Cloud as X-Queuey-Occurred-At).</summary>
    DateTimeOffset OccurredAtUtc,
    /// <summary>Optional event-type context header (opaque passthrough).</summary>
    string? EventType = null,
    /// <summary>Optional group/lane key (opaque passthrough; also Edge's ordering lane).</summary>
    string? GroupKey = null,
    /// <summary>Optional trace source (X-Queuey-Source).</summary>
    string? Source = null)
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Edge's ordering unit: <c>(queue, groupKey)</c>, with a null group key
    /// forming the queue's default lane. Strict FIFO holds within a lane
    /// (at most one in-flight transfer per lane); concurrency applies across
    /// lanes.
    /// </summary>
    public string Lane => GroupKey is null ? Queue : Queue + "\u001f" + GroupKey;
}
