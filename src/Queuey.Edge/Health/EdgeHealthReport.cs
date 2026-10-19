using System;
using System.Collections.Generic;
using System.Linq;
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
    [property: JsonPropertyName("lastFailure")] EdgeHealthReportFailure? LastFailure,
    [property: JsonPropertyName("storageLimitBytes")] long? StorageLimitBytes = null,
    [property: JsonPropertyName("intakes")] IReadOnlyList<EdgeHealthReportIntake>? Intakes = null)
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
                : null,
            StorageLimitBytes: health.StorageLimitBytes,
            Intakes: health.Intakes is { Count: > 0 } intakes
                ? intakes.Select(EdgeHealthReportIntake.From).ToArray()
                : null);
}

public sealed record EdgeHealthReportFailure(
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("statusCode")] int? StatusCode,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// One intake in a health report: whether its source is connected, whether it
/// stepped back because the spool was full, and what happened to the messages
/// (<see cref="EdgeIntakeCounters"/>; <c>duplicates</c> and <c>withoutId</c> were
/// added later and read as 0 from older nodes).
/// </summary>
public sealed record EdgeHealthReportIntake(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("lastMessageAtUtc")] DateTimeOffset? LastMessageAtUtc,
    [property: JsonPropertyName("lastConnectedAtUtc")] DateTimeOffset? LastConnectedAtUtc,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("received")] long Received,
    [property: JsonPropertyName("accepted")] long Accepted,
    [property: JsonPropertyName("deferred")] long Deferred,
    [property: JsonPropertyName("rejected")] long Rejected,
    [property: JsonPropertyName("unrouted")] long Unrouted,
    [property: JsonPropertyName("retainedSkipped")] long RetainedSkipped,
    [property: JsonPropertyName("lost")] long Lost,
    [property: JsonPropertyName("duplicates")] long Duplicates = 0,
    [property: JsonPropertyName("withoutId")] long WithoutId = 0)
{
    internal static EdgeHealthReportIntake From(EdgeIntakeHealth intake)
        => new(intake.Name, intake.Connected, intake.Paused, intake.LastMessageAtUtc, intake.LastConnectedAtUtc,
            intake.LastError, intake.Counters.Received, intake.Counters.Accepted, intake.Counters.Deferred,
            intake.Counters.Rejected, intake.Counters.Unrouted, intake.Counters.RetainedSkipped, intake.Counters.Lost,
            intake.Counters.Duplicates, intake.Counters.WithoutId);
}
