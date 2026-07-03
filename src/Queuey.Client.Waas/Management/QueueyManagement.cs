using System;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>Default <see cref="IQueueyManagement"/> over the control-plane client.</summary>
internal sealed class QueueyManagement : IQueueyManagement
{
    private readonly QueueyControlPlaneClient _controlPlane;

    public QueueyManagement(QueueyControlPlaneClient controlPlane)
        => _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));

    public async Task<TenantResult> CreateTenantAsync(string displayName, bool asProducer = false, bool withDefaultQueue = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));

        TenantSummaryResponse r = await _controlPlane
            .CreateTenantAsync(displayName.Trim(), asProducer, withDefaultQueue, cancellationToken)
            .ConfigureAwait(false);

        return new TenantResult { PublicId = r.PublicId, DisplayName = r.DisplayName, Status = r.Status, Kind = r.Kind };
    }

    public async Task<QueueResult> CreateQueueAsync(string tenantPublicId, string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));

        QueueReadResponse r = await _controlPlane
            .CreateQueueAsync(tenantPublicId, displayName.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return new QueueResult { PublicId = r.PublicId, TenantPublicId = r.TenantPublicId, DisplayName = r.DisplayName };
    }

    public Task<QueueMetricsSnapshot> GetQueueMetricsSnapshotAsync(string queuePublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queuePublicId)) throw new ArgumentException("A queue public id is required.", nameof(queuePublicId));
        return _controlPlane.GetQueueMetricsSnapshotAsync(queuePublicId, cancellationToken);
    }

    public Task<IssueListPage> ListIssuesAsync(string tenantPublicId, IssueQuery? query = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        return _controlPlane.ListIssuesAsync(tenantPublicId, query, cancellationToken);
    }

    public Task<IssueDetails> GetIssueAsync(string tenantPublicId, string issuePublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (string.IsNullOrWhiteSpace(issuePublicId)) throw new ArgumentException("An issue public id is required.", nameof(issuePublicId));
        return _controlPlane.GetIssueAsync(tenantPublicId, issuePublicId, cancellationToken);
    }
}
