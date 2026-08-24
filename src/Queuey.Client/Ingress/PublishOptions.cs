using System;

namespace Queuey.Client;

/// <summary>Per-publish options for <see cref="IQueueyIngress.PublishAsync{T}"/>.</summary>
public sealed class PublishOptions
{
    /// <summary>
    /// Event type, sent as <c>X-Queuey-Event-Type</c>. Only extracted by queues configured for
    /// context extraction (producer/integration streams); a no-op on a plain queue.
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// Group / partition key, sent as <c>X-Queuey-Group-Key</c>. Only extracted by queues configured
    /// for context extraction (producer/integration streams); a no-op on a plain queue.
    /// </summary>
    public string? GroupKey { get; set; }

    /// <summary>
    /// Idempotency key, sent as <c>Idempotency-Key</c>. A repeat within the queue's dedup window
    /// (24h by default) returns the original event with <see cref="PublishResult.Replayed"/> = true.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Trace source, sent as <c>X-Queuey-Source</c>. Overrides <see cref="QueueyOptions.Source"/>.</summary>
    public string? Source { get; set; }

    /// <summary>Overrides the request content type (defaults to <c>application/json</c> for typed payloads).</summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// When the event actually occurred, for producers that publish after
    /// the fact. Honoured by <c>Queuey.Edge</c>, which asserts it to Queuey
    /// Cloud as <c>X-Queuey-Occurred-At</c> during transfer so a drained
    /// backlog keeps honest history. The direct ingress client publishes
    /// immediately (occurrence ≈ receipt) and does not send it. Display and
    /// history context only — never billing or ordering.
    /// </summary>
    public DateTimeOffset? OccurredAtUtc { get; set; }
}
