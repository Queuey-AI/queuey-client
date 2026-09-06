using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Queuey.Edge;

/// <summary>
/// The node "checks in": OUTBOUND health reports to Queuey Cloud so the
/// console can show the fleet. Three rules keep this honest:
/// <list type="bullet">
/// <item>Outbound only. Cloud never reaches into a node; the loopback
///   endpoint stays loopback. A node that stops reporting is "silent" —
///   Cloud derives that from <c>last_seen</c>, the node never escalates.</item>
/// <item>Reports are not events. They bypass the spool, are never retried
///   (the next one supersedes), never share the lane with real work, and
///   are never billed.</item>
/// <item>Identity is stable and local: a UUID minted once into
///   <c>edge_meta</c>, so a reinstall on the same disk is the same node and
///   a fresh spool is a new one — which is exactly the truth.</item>
/// </list>
/// Cadence: a report at startup, one whenever <see cref="EdgeState"/>
/// changes, and one every <see cref="EdgeHealthReportOptions.ReportInterval"/>
/// otherwise.
/// </summary>
internal sealed class EdgeHealthReporter : BackgroundService
{
    internal const string NodeIdMetaKey = EdgeMetaKeys.NodeId;
    private static readonly TimeSpan StateCheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    private static readonly string EdgeVersion =
        typeof(EdgeHealthReporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(EdgeHealthReporter).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly HttpClient _http;
    private readonly QueueyEdgeOptions _options;
    private readonly IEventSpool _spool;
    private readonly IQueueyEdgeHealth _health;
    private readonly IEdgeClock _clock;
    private readonly ILogger<EdgeHealthReporter> _logger;
    private readonly Uri _ingressBase;

    private string? _nodeId;

    public EdgeHealthReporter(
        HttpClient http,
        QueueyEdgeOptions options,
        IEventSpool spool,
        IQueueyEdgeHealth health,
        IEdgeClock clock,
        ILogger<EdgeHealthReporter> logger)
    {
        _http = http;
        _options = options;
        _spool = spool;
        _health = health;
        _clock = clock;
        _logger = logger;
        _ingressBase = options.ResolveIngressBaseAddress();
    }

    /// <summary>The node's stable identity (minted on first use). Exposed for the CLI's status readout.</summary>
    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        if (_nodeId is not null) return _nodeId;
        var existing = await _spool.GetMetaAsync(NodeIdMetaKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existing))
        {
            existing = Uuid7.NewString(_clock.UtcNow);
            await _spool.SetMetaAsync(NodeIdMetaKey, existing, cancellationToken).ConfigureAwait(false);
        }
        return _nodeId = existing;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Health.ReportToCloud)
            return; // opt-in only

        string nodeId;
        try
        {
            nodeId = await GetNodeIdAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // A faulted spool cannot mint an identity; reporting is the least
            // important thing on this node right now. The transfer loop and
            // health service already scream about the fault.
            _logger.LogDebug(ex, "Health reporting disabled: could not resolve node identity.");
            return;
        }

        var nodeName = string.IsNullOrWhiteSpace(_options.Health.NodeName)
            ? Environment.MachineName
            : _options.Health.NodeName!;

        _logger.LogInformation(EdgeLogEvents.HealthReportStarted,
            "Edge health reporting to Queuey Cloud is ON (node {NodeName}, id {NodeId}, every {Interval}). " +
            "Outbound only; reports are not events and are never billed.",
            nodeName, nodeId, _options.Health.ReportInterval);

        EdgeState? lastReportedState = null;
        DateTimeOffset? lastReportedAt = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            EdgeHealth snapshot;
            try { snapshot = _health.Snapshot(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health snapshot failed; skipping this report.");
                snapshot = null!;
            }

            if (snapshot is not null)
            {
                var now = _clock.UtcNow;
                var due = lastReportedAt is null
                    || snapshot.State != lastReportedState
                    || now - lastReportedAt.Value >= _options.Health.ReportInterval;

                if (due)
                {
                    var sent = await SendAsync(nodeId, EdgeHealthReport.From(
                        snapshot, nodeName, EdgeVersion, RuntimeInformation.RuntimeIdentifier,
                        now, _options.Health.ReportInterval), stoppingToken).ConfigureAwait(false);
                    // Even a failed send counts as "attempted" for cadence:
                    // hammering an unreachable Cloud every 10 s helps nobody,
                    // and the NEXT interval (or state change) tries again.
                    lastReportedAt = now;
                    if (sent) lastReportedState = snapshot.State;
                }
            }

            try
            {
                await Task.Delay(StateCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One send. Never throws; true when Cloud acknowledged with a 2xx.</summary>
    internal async Task<bool> SendAsync(string nodeId, EdgeHealthReport report, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(_ingressBase,
                $"edge/{Uri.EscapeDataString(_options.TenantPublicId!)}/nodes/{Uri.EscapeDataString(nodeId)}/health");

            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(JsonSerializer.Serialize(report, Json), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Queuey-Edge-Version", EdgeVersion);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return true;

            _logger.LogDebug(EdgeLogEvents.HealthReportFailed,
                "Health report rejected by Cloud with HTTP {Status}; the next report supersedes it.",
                (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(EdgeLogEvents.HealthReportFailed, ex,
                "Health report could not be sent; the next report supersedes it.");
            return false;
        }
    }
}
