using System;
using System.Text.Json.Serialization;

namespace Queuey.Edge;

/// <summary>
/// The wire shape of one health report — <c>POST
/// {ingress}/edge/{tenantPublicId}/nodes/{nodeId}/health</c>. Additive
/// forever: Cloud stores the document as-is and reads the fields it knows,
/// so an older Cloud never rejects a newer node and vice versa. Time fields
/// are absolute UTC instants (never "seconds ago") because the report may
/// be read long after it was written.
/// </summary>
public sealed record EdgeHealthReport(
    [property: JsonPropertyName("nodeName")] string NodeName,
    [property: JsonPropertyName("edgeVersion")] string EdgeVersion,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("reportedAtUtc")] DateTimeOffset ReportedAtUtc,
    [property: JsonPropertyName("reportIntervalSeconds")] int ReportIntervalSeconds,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("pendingCount")] long PendingCount,
    [property: JsonPropertyName("quarantinedCount")] long QuarantinedCount,
    [property: JsonPropertyName("oldestPendingAgeSeconds")] double? OldestPendingAgeSeconds,
    [property: JsonPropertyName("storageUsageBytes")] long StorageUsageBytes,
    [property: JsonPropertyName("storageDurabilityWarning")] bool StorageDurabilityWarning,
    [property: JsonPropertyName("lastSuccessfulCloudContactUtc")] DateTimeOffset? LastSuccessfulCloudContactUtc,
    [property: JsonPropertyName("nextTransferAttemptUtc")] DateTimeOffset? NextTransferAttemptUtc,
    [property: JsonPropertyName("lastFailure")] EdgeHealthReportFailure? LastFailure)
{
    public static EdgeHealthReport From(
        EdgeHealth health, string nodeName, string edgeVersion, string platform,
        DateTimeOffset reportedAtUtc, TimeSpan reportInterval)
        => new(
            NodeName: nodeName,
            EdgeVersion: edgeVersion,
            Platform: platform,
            ReportedAtUtc: reportedAtUtc,
            ReportIntervalSeconds: (int)Math.Max(1, reportInterval.TotalSeconds),
            State: health.State.ToString(),
            PendingCount: health.PendingCount,
            QuarantinedCount: health.QuarantinedCount,
            OldestPendingAgeSeconds: health.OldestPendingAge?.TotalSeconds,
            StorageUsageBytes: health.StorageUsageBytes,
            StorageDurabilityWarning: health.StorageDurabilityWarning,
            LastSuccessfulCloudContactUtc: health.LastSuccessfulCloudContact,
            NextTransferAttemptUtc: health.NextTransferAttemptUtc,
            LastFailure: health.LastTransferFailure is { } f
                ? new EdgeHealthReportFailure(f.Class.ToString(), f.Reason.ToString(), f.StatusCode, f.AtUtc, f.Message)
                : null);
}

public sealed record EdgeHealthReportFailure(
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("statusCode")] int? StatusCode,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("message")] string? Message);
