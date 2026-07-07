using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Control-plane management: create tenants and queues. Targets the API host with
/// <c>X-Api-Key</c> + <c>X-License-PublicId</c> (needs <c>tenant.write</c>/<c>queue.write</c>).
/// </summary>
public interface IQueueyManagement
{
    /// <summary>
    /// Creates a tenant under the current license. <paramref name="asProducer"/> seeds a WaaS producer
    /// (ProducerSystem); <paramref name="withDefaultQueue"/> seeds a starter queue.
    /// </summary>
    Task<TenantResult> CreateTenantAsync(string displayName, bool asProducer = false, bool withDefaultQueue = false, CancellationToken cancellationToken = default);

    /// <summary>Creates a queue under a tenant.</summary>
    Task<QueueResult> CreateQueueAsync(string tenantPublicId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Reads a queue's traffic snapshot (<c>GET /queues/{q}/metrics/snapshot</c>).</summary>
    Task<QueueMetricsSnapshot> GetQueueMetricsSnapshotAsync(string queuePublicId, CancellationToken cancellationToken = default);

    /// <summary>Lists a tenant's issues (cursor-paged; optional status/severity/queue filter).</summary>
    Task<IssueListPage> ListIssuesAsync(string tenantPublicId, IssueQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one issue's detail.</summary>
    Task<IssueDetails> GetIssueAsync(string tenantPublicId, string issuePublicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays an existing event to a connected <c>queuey listen</c> session for local debugging
    /// (<c>POST /queues/{q}/replay-to-listener/{e}</c>). Read-only: the event is not modified and the real
    /// endpoint is never contacted. Requires a listener connected on the queue's / tenant's scope.
    /// </summary>
    Task<ReplayResult> ReplayToListenerAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a replay-to-listener: whether a listener was connected, and its local response.</summary>
public sealed class ReplayResult
{
    /// <summary>False when no <c>queuey listen</c> session is connected — nothing was forwarded.</summary>
    public bool ListenerConnected { get; init; }
    /// <summary>True when the local listener returned a 2xx.</summary>
    public bool Delivered { get; init; }
    /// <summary>The local listener's HTTP status, if it responded.</summary>
    public int? StatusCode { get; init; }
    /// <summary>Round-trip time to the listener.</summary>
    public long DurationMs { get; init; }
    /// <summary>Error/diagnostic, when not delivered.</summary>
    public string? Error { get; init; }
}

/// <summary>A created/summarized tenant.</summary>
public sealed class TenantResult
{
    /// <summary>Tenant public id (<c>ten_…</c>).</summary>
    public string? PublicId { get; init; }
    public string? DisplayName { get; init; }
    public string? Status { get; init; }
    /// <summary>Tenant kind: <c>Standard</c> | <c>ProducerSystem</c> | <c>Integration</c>.</summary>
    public string? Kind { get; init; }
}

/// <summary>A created queue.</summary>
public sealed class QueueResult
{
    /// <summary>Queue public id (<c>que_…</c>).</summary>
    public string? PublicId { get; init; }
    public string? TenantPublicId { get; init; }
    public string? DisplayName { get; init; }
}
