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
