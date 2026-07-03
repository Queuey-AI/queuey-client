using System;
using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>A point-in-time snapshot of a queue's traffic (from <c>GET /queues/{q}/metrics/snapshot</c>).</summary>
public sealed class QueueMetricsSnapshot
{
    public int WindowSeconds { get; init; }
    public int Received { get; init; }
    public int Delivered { get; init; }
    public int Failed { get; init; }
    public int Retries { get; init; }
    public double SuccessRate { get; init; }
    public long? P95LatencyMs { get; init; }
    public int DlqSample { get; init; }
    public int InFlightSample { get; init; }
    public int DepthSample { get; init; }
    public long E2eAvgMs { get; init; }
}

/// <summary>Issue status. Integer-backed to match the wire (management enums serialize as integers).</summary>
public enum IssueStatus { Open = 0, Resolved = 1 }

/// <summary>Issue severity. Integer-backed to match the wire.</summary>
public enum IssueSeverity { Critical = 0, Warning = 1, Info = 2 }

/// <summary>Filter for <c>ListIssuesAsync</c>.</summary>
public sealed class IssueQuery
{
    public IssueStatus? Status { get; set; }
    public IssueSeverity? Severity { get; set; }
    public string? QueuePublicId { get; set; }
    public int? Limit { get; set; }
    public string? Cursor { get; set; }
}

/// <summary>A summarized issue in a list page.</summary>
public sealed class IssueSummary
{
    public string? PublicId { get; init; }
    public IssueSeverity Severity { get; init; }
    public IssueStatus Status { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset OpenedAtUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public DateTimeOffset? ResolvedAtUtc { get; init; }
    public string? QueuePublicId { get; init; }
}

/// <summary>A cursor-paged list of issues.</summary>
public sealed class IssueListPage
{
    public IReadOnlyList<IssueSummary> Items { get; init; } = Array.Empty<IssueSummary>();
    public string? NextCursor { get; init; }
}

/// <summary>Full detail for one issue.</summary>
public sealed class IssueDetails
{
    public string? PublicId { get; init; }
    public IssueSeverity Severity { get; init; }
    public IssueStatus Status { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? QueuePublicId { get; init; }
    public DateTimeOffset OpenedAtUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public DateTimeOffset? ResolvedAtUtc { get; init; }
}
