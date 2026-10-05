using System.Collections.Generic;
using System.Linq;

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
    public bool? DlqEnabled { get; set; }
    public int? RetentionDays { get; set; }
    public bool? Idempotent { get; set; }
    public int? MaxAttempts { get; set; }
    public int? DlqAfterAttempts { get; set; }
    public RetryBackoffWire? Backoff { get; set; }
    public DeliveryFilterWire? Filter { get; set; }
}

/// <summary>Wire shape of a retry backoff, in the patch and in the config read-back.</summary>
internal sealed class RetryBackoffWire
{
    public int? BaseDelayMs { get; set; }
    public int? MaxDelayMs { get; set; }
    public string? Jitter { get; set; }

    internal static RetryBackoffWire? From(RetryBackoff? b)
        => b is null || b.IsEmpty ? null : new RetryBackoffWire { BaseDelayMs = b.BaseDelayMs, MaxDelayMs = b.MaxDelayMs, Jitter = b.Jitter };

    internal RetryBackoff ToModel() => new() { BaseDelayMs = BaseDelayMs, MaxDelayMs = MaxDelayMs, Jitter = Jitter };
}

/// <summary>
/// Wire shape of a delivery filter. An empty condition list is sent as it is: that is how a file
/// that stops declaring conditions removes the filter it had.
/// </summary>
internal sealed class DeliveryFilterWire
{
    public string? Match { get; set; }
    public List<FilterConditionWire>? Conditions { get; set; }

    internal static DeliveryFilterWire? From(DeliveryFilter? f)
        => f is null ? null : new DeliveryFilterWire
        {
            Match = f.Match,
            Conditions = f.Conditions.Select(c => new FilterConditionWire { Field = c.Field, Op = c.Op, Value = c.Value }).ToList(),
        };

    internal DeliveryFilter ToModel() => new()
    {
        Match = Match,
        Conditions = (Conditions ?? new List<FilterConditionWire>())
            .Select(c => new DeliveryFilterCondition { Field = c.Field ?? string.Empty, Op = c.Op ?? string.Empty, Value = c.Value })
            .ToList(),
    };
}

internal sealed class FilterConditionWire
{
    public string? Field { get; set; }
    public string? Op { get; set; }
    public string? Value { get; set; }
}

/// <summary>
/// Wire request for <c>PATCH /queues/{que}/mode-change</c>. The mode travels as its number, like every
/// enum this SDK sends — see <see cref="DeploymentQueueModes"/>.
/// </summary>
internal sealed class QueueModeChangeRequest
{
    public int Mode { get; set; }
}

/// <summary>Wire shape of one row from <c>GET /tenants/{ten}/queues</c>.</summary>
internal sealed class QueueListItemResponse
{
    public string? PublicId { get; set; }
    public string? DisplayName { get; set; }
    public string? Mode { get; set; }
    public bool HasDeliveryTarget { get; set; }
    public bool IngressClosed { get; set; }
    public bool DeliveryHeld { get; set; }
    public bool Suspended { get; set; }
}
