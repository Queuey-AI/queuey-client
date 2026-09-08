using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Health;

/// <summary>
/// The check-in contract: outbound POST to the health route with the
/// publish key, a stable node identity that lives in the spool, a report
/// that carries every health field, and failures that never escape.
/// </summary>
public class EdgeHealthReporterTests
{
    [Fact]
    public async Task Node_identity_is_minted_once_and_survives_reopen()
    {
        using var fx = new SpoolFixture();
        var first = await Reporter(fx, _ => new HttpResponseMessage(HttpStatusCode.NoContent)).Reporter
            .GetNodeIdAsync(CancellationToken.None);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new SqliteEventSpool(fx.Options, fx.Clock);
        var again = await new EdgeHealthReporter(
            new HttpClient(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))),
            Options(), reopened, new StaticHealth(Healthy()), fx.Clock, NullLogger<EdgeHealthReporter>.Instance)
            .GetNodeIdAsync(CancellationToken.None);

        Assert.Equal(first, again);
        Assert.True(Guid.TryParse(first, out _)); // UUIDv7 — the same identity rules as transfer ids
    }

    [Fact]
    public async Task Report_goes_to_the_health_route_with_the_publish_key_and_every_field()
    {
        using var fx = new SpoolFixture();
        var health = new EdgeHealth(
            State: EdgeState.RequiresAction,
            PendingCount: 12,
            OldestPendingAge: TimeSpan.FromMinutes(90),
            StorageUsageBytes: 4096,
            LastSuccessfulCloudContact: fx.Clock.UtcNow.AddHours(-2),
            LastTransferFailure: new TransferFailure(TransferClass.RequiresAction, TransferReason.AuthenticationRejected,
                401, fx.Clock.UtcNow.AddMinutes(-1), "invalid_api_key"),
            QuarantinedCount: 1,
            StorageDurabilityWarning: true,
            NextTransferAttemptUtc: fx.Clock.UtcNow.AddMinutes(5));
        var (reporter, handler) = Reporter(fx, _ => new HttpResponseMessage(HttpStatusCode.NoContent), health);

        var nodeId = await reporter.GetNodeIdAsync(CancellationToken.None);
        var ok = await reporter.SendAsync(nodeId, EdgeHealthReport.From(
            health, "barge-07", "1.2.3", "linux-arm64", fx.Clock.UtcNow, TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.True(ok.Sent);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/edge/ten_test/nodes/{nodeId}/health", request.Uri.AbsolutePath);
        Assert.Equal("qak_id.secret", request.Headers["X-Api-Key"]);
        Assert.False(string.IsNullOrWhiteSpace(request.Headers["X-Queuey-Edge-Version"]));

        using var doc = JsonDocument.Parse(request.Body);
        var root = doc.RootElement;
        Assert.Equal("barge-07", root.GetProperty("nodeName").GetString());
        Assert.Equal("RequiresAction", root.GetProperty("state").GetString());
        Assert.Equal(12, root.GetProperty("pendingCount").GetInt64());
        Assert.Equal(1, root.GetProperty("quarantinedCount").GetInt64());
        Assert.Equal(5400d, root.GetProperty("oldestPendingAgeSeconds").GetDouble());
        Assert.True(root.GetProperty("storageDurabilityWarning").GetBoolean());
        Assert.Equal(300, root.GetProperty("reportIntervalSeconds").GetInt32());
        Assert.Equal("AuthenticationRejected", root.GetProperty("lastFailure").GetProperty("reason").GetString());
        Assert.Equal(401, root.GetProperty("lastFailure").GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("nextTransferAttemptUtc").ValueKind);
    }

    [Fact]
    public async Task A_rejected_or_unreachable_cloud_never_throws()
    {
        using var fx = new SpoolFixture();
        var (rejecting, _) = Reporter(fx, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (exploding, _) = Reporter(fx, _ => throw new HttpRequestException("boom"));
        var report = EdgeHealthReport.From(Healthy(), "n", "v", "p", fx.Clock.UtcNow, TimeSpan.FromMinutes(5));

        Assert.False((await rejecting.SendAsync("node", report, CancellationToken.None)).Sent);
        Assert.False((await exploding.SendAsync("node", report, CancellationToken.None)).Sent);
    }

    [Fact]
    public async Task A_429_carries_cloud_s_retry_after_into_the_outcome()
    {
        using var fx = new SpoolFixture();
        var (throttled, _) = Reporter(fx, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        });
        var report = EdgeHealthReport.From(Healthy(), "n", "v", "p", fx.Clock.UtcNow, TimeSpan.FromMinutes(5));

        var outcome = await throttled.SendAsync("node", report, CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Equal(TimeSpan.FromSeconds(7), outcome.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_date_is_honoured_like_the_transfer_path_does()
    {
        using var fx = new SpoolFixture();
        var (throttled, _) = Reporter(fx, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(fx.Clock.UtcNow.AddSeconds(30));
            return r;
        });
        var report = EdgeHealthReport.From(Healthy(), "n", "v", "p", fx.Clock.UtcNow, TimeSpan.FromMinutes(5));

        var outcome = await throttled.SendAsync("node", report, CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.NotNull(outcome.RetryAfter);
        Assert.InRange(outcome.RetryAfter!.Value.TotalSeconds, 25, 30);
    }

    [Fact]
    public async Task Disabled_by_default_sends_nothing()
    {
        using var fx = new SpoolFixture();
        var (reporter, handler) = Reporter(fx, _ => new HttpResponseMessage(HttpStatusCode.NoContent), enabled: false);

        await reporter.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await reporter.StopAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Enabled_reports_at_startup_without_waiting_for_the_interval()
    {
        using var fx = new SpoolFixture();
        var (reporter, handler) = Reporter(fx, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await reporter.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Requests.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await reporter.StopAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/health", request.Uri.AbsolutePath);
        Assert.Equal("Healthy", JsonDocument.Parse(request.Body).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_refused_report_is_visible_as_a_rejection_with_advice_and_a_success_clears_it()
    {
        using var fx = new SpoolFixture();
        var state = new EdgeRuntimeState();
        var status = HttpStatusCode.Forbidden;
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(status));
        var reporter = new EdgeHealthReporter(new HttpClient(handler), Options(), fx.Spool,
            new StaticHealth(Healthy()), fx.Clock, NullLogger<EdgeHealthReporter>.Instance, state);
        var report = EdgeHealthReport.From(Healthy(), "n", "v", "p", fx.Clock.UtcNow, TimeSpan.FromMinutes(5));

        await reporter.SendAsync("node", report, CancellationToken.None);
        var rejection = state.HealthReportRejection;
        Assert.NotNull(rejection);
        Assert.Equal(403, rejection!.StatusCode);
        Assert.Contains("workspace-scoped", rejection.Advice);

        status = HttpStatusCode.NoContent;
        await reporter.SendAsync("node", report, CancellationToken.None);
        Assert.Null(state.HealthReportRejection);
    }

    // ── support ──────────────────────────────────────────────────────

    private static EdgeHealth Healthy() => new(EdgeState.Healthy, 0, null, 0, null, null, 0, false);

    private static QueueyEdgeOptions Options(bool enabled = true)
    {
        var o = new QueueyEdgeOptions
        {
            ApiKey = "qak_id.secret",
            TenantPublicId = "ten_test",
            IngressBaseAddress = new Uri("https://ingress.test/")
        };
        o.Health.ReportToCloud = enabled;
        o.Health.NodeName = "barge-07";
        return o;
    }

    private static (EdgeHealthReporter Reporter, ScriptedHandler Handler) Reporter(
        SpoolFixture fx, Func<HttpRequestMessage, HttpResponseMessage> script, EdgeHealth? health = null, bool enabled = true)
    {
        var handler = new ScriptedHandler(script);
        var reporter = new EdgeHealthReporter(new HttpClient(handler), Options(enabled), fx.Spool,
            new StaticHealth(health ?? Healthy()), fx.Clock, NullLogger<EdgeHealthReporter>.Instance);
        return (reporter, handler);
    }

    private sealed class StaticHealth : IQueueyEdgeHealth
    {
        private readonly EdgeHealth _health;
        public StaticHealth(EdgeHealth health) => _health = health;
        public EdgeHealth Snapshot() => _health;
    }

    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;
        public List<(Uri Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script) => _script = script;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, headers, body));
            return _script(request);
        }
    }
}
