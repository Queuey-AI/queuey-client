namespace Queuey.Client.Waas;

/// <summary>
/// Wire request for <c>PUT /queues</c> — declarative, idempotent apply by <c>(tenant, name)</c>.
/// Existence only: policy travels separately so that a queue sync never has to round-trip (and risk
/// clearing) the queue's delivery config.
/// </summary>
internal sealed class QueueApplyRequest
{
    public string TenantPublicId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
}

/// <summary>
/// Wire response for <c>PUT /queues</c>:
/// <c>{ publicId: "que_…", displayName, created, hasDeliveryTarget }</c>.
/// </summary>
internal sealed class QueueApplyResponse
{
    /// <summary>Queue public id (<c>que_…</c>).</summary>
    public string? PublicId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>True when this call created the queue; false when it already existed.</summary>
    public bool Created { get; set; }

    /// <summary>
    /// Whether the queue resolves to somewhere to deliver — its own target, or the workspace default
    /// it inherits. False is not an error: a queue created by a sync legitimately has no endpoint yet
    /// and starts in LogOnly. It is the readiness warning the SDK surfaces.
    /// </summary>
    public bool HasDeliveryTarget { get; set; }
}

/// <summary>
/// Wire request for <c>PATCH /queues/{queuePublicId}/policy</c>. Every field is optional, and an
/// undeclared one is <b>omitted from the body entirely</b> (the SDK drops nulls on write) — which is
/// the unambiguous way to say "leave it alone"; an explicit null could just as easily read as
/// "clear it". A field the queue never declared therefore keeps inheriting from the workspace.
/// <para>
/// This is deliberately not the full-state <c>PUT /queues/{que}/config</c>: that one also rewrites
/// delivery, and an empty base URL there clears the queue's whole delivery config.
/// </para>
/// </summary>
internal sealed class QueuePolicyPatchRequest
{
    public string? Ordering { get; set; }
    public int? MaxAttempts { get; set; }
    public bool? DlqEnabled { get; set; }
    public int? DlqAfterAttempts { get; set; }
    public int? RetentionDays { get; set; }
    public bool? Idempotent { get; set; }
}
