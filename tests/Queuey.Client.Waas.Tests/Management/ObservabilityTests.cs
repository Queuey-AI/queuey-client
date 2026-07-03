using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

public class ObservabilityTests
{
    [Fact]
    public async Task GetQueueMetricsSnapshot_reads_the_snapshot()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            queueId = 1,
            windowSeconds = 60,
            received = 120,
            delivered = 118,
            failed = 2,
            retries = 3,
            successRate = 0.983,
            p95LatencyMs = 240L,
            dlqSample = 0,
            inFlightSample = 1,
            depthSample = 4,
            e2eAvgMs = 88L,
        }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        QueueMetricsSnapshot snap = await svc.Management.GetQueueMetricsSnapshotAsync("que_1");

        Assert.Equal("https://api.example/queues/que_1/metrics/snapshot", api.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, api.LastRequest!.Method);
        Assert.Equal(120, snap.Received);
        Assert.Equal(118, snap.Delivered);
        Assert.Equal(240L, snap.P95LatencyMs);
    }

    [Fact]
    public async Task ListIssues_builds_the_query_and_maps_integer_enums()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            items = new[]
            {
                new { publicId = "iss_1", severity = 1, status = 0, title = "Deliveries failing",
                      summary = "…", openedAtUtc = DateTimeOffset.UnixEpoch, lastSeenUtc = DateTimeOffset.UnixEpoch, queuePublicId = "que_1" },
            },
            nextCursor = "c2",
        }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        IssueListPage page = await svc.Management.ListIssuesAsync("ten_abc",
            new IssueQuery { Status = IssueStatus.Open, Severity = IssueSeverity.Warning, Limit = 10 });

        string uri = api.LastRequest!.RequestUri!.ToString();
        Assert.StartsWith("https://api.example/issues/ten_abc?", uri);
        Assert.Contains("status=Open", uri);
        Assert.Contains("severity=Warning", uri);
        Assert.Contains("limit=10", uri);

        Assert.Equal("c2", page.NextCursor);
        IssueSummary item = Assert.Single(page.Items);
        Assert.Equal("iss_1", item.PublicId);
        Assert.Equal(IssueSeverity.Warning, item.Severity); // 1 → Warning
        Assert.Equal(IssueStatus.Open, item.Status);        // 0 → Open
        Assert.Equal("que_1", item.QueuePublicId);
    }

    [Fact]
    public async Task GetIssue_reads_the_detail()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "iss_1", severity = 0, status = 1, title = "Loop detected", summary = "…",
            queuePublicId = "que_1", openedAtUtc = DateTimeOffset.UnixEpoch, lastSeenUtc = DateTimeOffset.UnixEpoch,
            resolvedAtUtc = DateTimeOffset.UnixEpoch,
        }));
        QueueyService svc = WaasTestHost.Build(apiStub: api);

        IssueDetails d = await svc.Management.GetIssueAsync("ten_abc", "iss_1");

        Assert.Equal("https://api.example/issues/ten_abc/iss_1", api.LastRequest!.RequestUri!.ToString());
        Assert.Equal(IssueSeverity.Critical, d.Severity); // 0 → Critical
        Assert.Equal(IssueStatus.Resolved, d.Status);     // 1 → Resolved
        Assert.NotNull(d.ResolvedAtUtc);
    }
}
