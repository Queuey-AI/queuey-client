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
    /// THE way an envelope comes into existence — one place for the identity
    /// rules, shared by the in-process publisher and the CLI's
    /// <c>queuey edge publish</c> (the shell/IoT path where a non-.NET
    /// program hands events to a co-resident Edge via the spool file):
    /// a caller-supplied idempotency key IS the transfer identity (one key,
    /// one dedup domain); absent one, a UUIDv7 minted at accept so ids sort
    /// by publish time. Occurrence defaults to now.
    /// </summary>
    public static EventEnvelope Create(
        string queue,
        string tenantPublicId,
        byte[] payload,
        string contentType,
        string? idempotencyKey = null,
        string? eventType = null,
        string? groupKey = null,
        string? source = null,
        DateTimeOffset? occurredAtUtc = null,
        DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(queue))
            throw new Client.QueueyConfigurationException("A queue name is required.");
        if (string.IsNullOrWhiteSpace(tenantPublicId))
            throw new Client.QueueyConfigurationException("A tenant public id is required.");
        if (payload is null)
            throw new QueueyPayloadRejectedException("The payload cannot be null.");
        if (string.IsNullOrWhiteSpace(contentType))
            throw new Client.QueueyConfigurationException("A content type is required.");

        var now = nowUtc ?? DateTimeOffset.UtcNow;

        return new EventEnvelope(
            Version: CurrentVersion,
            TransferId: string.IsNullOrWhiteSpace(idempotencyKey)
                ? Uuid7.NewString(now)
                : idempotencyKey!.Trim(),
            Queue: queue.Trim(),
            TenantPublicId: tenantPublicId.Trim(),
            ContentType: contentType.Trim(),
            Payload: payload,
            OccurredAtUtc: occurredAtUtc ?? now,
            EventType: eventType,
            GroupKey: groupKey,
            Source: source);
    }

    /// <summary>
    /// Edge's ordering unit: <c>(queue, groupKey)</c>, with a null group key
    /// forming the queue's default lane. Strict FIFO holds within a lane
    /// (at most one in-flight transfer per lane); concurrency applies across
    /// lanes.
    /// </summary>
    public string Lane => GroupKey is null ? Queue : Queue + "\u001f" + GroupKey;
}
