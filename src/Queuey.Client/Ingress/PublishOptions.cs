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
}
