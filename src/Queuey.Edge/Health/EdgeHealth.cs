using System;

namespace Queuey.Edge;

/// <summary>
/// Node state, DERIVED from spool contents plus the last transfer outcome —
/// never stored, so it cannot drift from reality across a restart. Only the
/// two storage states change what <c>PublishAsync</c> does; everything else
/// is invisible to the application.
/// </summary>
public enum EdgeState
{
    /// <summary>Last transfer succeeded; backlog under threshold.</summary>
    Healthy = 0,

    /// <summary>Pending work / transient failures / a drain in progress. Accepts; drains FIFO.</summary>
    Backlogged = 1,

    /// <summary>Non-transient rejection (auth/route/billing/paused/TLS). Accepts; slow-probes; never discards.</summary>
    RequiresAction = 2,

    /// <summary>Storage limit or disk full. PublishAsync THROWS; draining continues.</summary>
    StorageFull = 3,

    /// <summary>Spool corrupt/unreadable. Halted; PublishAsync throws until explicit operator recovery.</summary>
    StorageFaulted = 4
}

/// <summary>
/// The health snapshot — everything a customer's existing monitoring needs.
/// Queuey EXPOSES health; the customer operates monitoring (the boundary
/// from plan rev 4 D6). The one signal worth alerting on, if only one is
/// wired: <see cref="OldestPendingAge"/>.
/// </summary>
public sealed record EdgeHealth(
    EdgeState State,
    long PendingCount,
    TimeSpan? OldestPendingAge,
    long StorageUsageBytes,
    DateTimeOffset? LastSuccessfulCloudContact,
    TransferFailure? LastTransferFailure,
    long QuarantinedCount,
    bool StorageDurabilityWarning,
    DateTimeOffset? NextTransferAttemptUtc = null,
    HealthReportRejection? HealthReportRejection = null);

/// <summary>
/// Cloud refused the node's health report. <see cref="Advice"/> says what to
/// do about the status it answered with.
/// </summary>
public sealed record HealthReportRejection(int StatusCode, DateTimeOffset AtUtc)
{
    public string Advice => StatusCode switch
    {
        401 => "Cloud does not accept the key: check ApiKey.",
        403 => "The key cannot check in: Edge needs a workspace-scoped publish key, not a queue-scoped one.",
        404 => "Cloud does not know this workspace: check TenantPublicId and the ingress address.",
        429 => "Cloud is rate-limiting reports; the next one waits as told.",
        _ => $"Cloud answered {StatusCode} to the health report.",
    };
}

/// <summary>The most recent transfer failure, kept for diagnosis (what happened, not just what Edge did).</summary>
public sealed record TransferFailure(
    TransferClass Class,
    TransferReason Reason,
    int? StatusCode,
    DateTimeOffset AtUtc,
    string? Message);

/// <summary>In-process health access. Also exposed via the OpenTelemetry meter <c>Queuey.Edge</c>.</summary>
public interface IQueueyEdgeHealth
{
    EdgeHealth Snapshot();
}
