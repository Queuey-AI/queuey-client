using System;
using System.Collections.Generic;
using System.Linq;
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

    public async Task<IReadOnlyList<QueueListItem>> ListQueuesAsync(string tenantPublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));

        List<QueueListItemResponse> rows = await _controlPlane.ListQueuesAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new QueueListItem
        {
            PublicId = r.PublicId,
            DisplayName = r.DisplayName,
            Mode = r.Mode,
            HasDeliveryTarget = r.HasDeliveryTarget,
            IngressClosed = r.IngressClosed,
            DeliveryHeld = r.DeliveryHeld,
            Suspended = r.Suspended,
        }).ToArray();
    }

    public Task SetWorkspaceDeliveryAsync(string tenantPublicId, WorkspaceDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (delivery is null) throw new ArgumentNullException(nameof(delivery));

        return _controlPlane.PatchTenantDeliveryAsync(tenantPublicId, WireOf(delivery), cancellationToken);
    }

    // Samme body for apply og for planen (?dryRun=true), så planen spør om nøyaktig det apply sender.
    internal static PatchTenantDeliveryWireRequest WireOf(WorkspaceDelivery delivery) => new()
    {
        BaseUrl = delivery.BaseUrl,
        AuthMode = delivery.AuthMode,
        CredentialRef = delivery.CredentialRef,
        AuthHeaderName = delivery.AuthHeaderName,
        Method = delivery.Method,
        TimeoutMs = delivery.TimeoutMs,
        Signing = ToWire(delivery.Signing),
        RateLimit = ToWire(delivery.RateLimit),
    };

    public Task SetQueueDeliveryAsync(string queuePublicId, QueueDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queuePublicId)) throw new ArgumentException("A queue public id is required.", nameof(queuePublicId));
        if (delivery is null) throw new ArgumentNullException(nameof(delivery));

        return _controlPlane.PatchQueueDeliveryAsync(queuePublicId, WireOf(delivery), cancellationToken);
    }

    internal static PatchQueueDeliveryWireRequest WireOf(QueueDelivery delivery) => new()
    {
        Url = delivery.Url,
        Inherit = delivery.Inherit,
        AuthMode = delivery.AuthMode,
        CredentialRef = delivery.CredentialRef,
        AuthHeaderName = delivery.AuthHeaderName,
        TimeoutMs = delivery.TimeoutMs,
        Signing = ToWire(delivery.Signing),
        RateLimit = ToWire(delivery.RateLimit),
    };

    public async Task<CredentialResult> CreateCredentialAsync(
        string tenantPublicId, string name, string type, string secret,
        string? keyId = null, string? username = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A credential name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("A credential type is required.", nameof(type));
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("A secret is required.", nameof(secret));

        CredentialWireResponse r = await _controlPlane.CreateCredentialAsync(tenantPublicId, new CreateCredentialWireRequest
        {
            Name = name.Trim(),
            Type = type.Trim(),
            Secret = secret,
            KeyId = keyId,
            Username = username,
        }, cancellationToken).ConfigureAwait(false);

        return ToResult(r);
    }

    public async Task<IReadOnlyList<CredentialResult>> ListCredentialsAsync(string tenantPublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));

        List<CredentialWireResponse> rows = await _controlPlane.ListCredentialsAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);
        return rows.Select(ToResult).ToArray();
    }

    public async Task<IngressSigningKey> MintIngressKeyAsync(string queuePublicId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queuePublicId)) throw new ArgumentException("A queue public id is required.", nameof(queuePublicId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A key name is required.", nameof(name));

        CreateQueueHmacClientWireResponse r = await _controlPlane
            .MintIngressKeyAsync(queuePublicId, new CreateQueueHmacClientWireRequest { Name = name.Trim() }, cancellationToken)
            .ConfigureAwait(false);

        return new IngressSigningKey
        {
            ClientPublicId = r.ClientPublicId,
            ClientName = r.ClientName,
            KeyId = r.KeyId,
            Secret = r.Secret,
            QueuePublicId = r.QueuePublicId,
        };
    }

    public Task SetWorkspacePolicyAsync(string tenantPublicId, DeploymentWorkspace policy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        return _controlPlane.PatchTenantPolicyAsync(tenantPublicId, WireOf(policy), cancellationToken);
    }

    internal static PatchTenantPolicyWireRequest WireOf(DeploymentWorkspace policy) => new()
    {
        Ordering = policy.Ordering,
        DlqEnabled = policy.DlqEnabled,
        RetentionDays = policy.RetentionDays,
        Idempotent = policy.Idempotent,
        MaxAttempts = policy.MaxAttempts,
        DlqAfterAttempts = policy.DlqAfterAttempts,
        Backoff = RetryBackoffWire.From(policy.Backoff),
    };

    public Task SetIngressAsync(string publicId, bool isQueue, DeploymentIngress ingress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicId)) throw new ArgumentException("A public id is required.", nameof(publicId));
        if (ingress is null) throw new ArgumentNullException(nameof(ingress));

        var request = WireOf(ingress);

        return isQueue
            ? _controlPlane.PatchQueueIngressAsync(publicId, request, cancellationToken)
            : _controlPlane.PatchTenantIngressAsync(publicId, request, cancellationToken);
    }

    internal static PatchIngressWireRequest WireOf(DeploymentIngress ingress) => new()
    {
        AuthMode = ingress.AuthMode,
        EventType = ToWire(ingress.EventType),
        GroupKey = ToWire(ingress.GroupKey),
        SuccessStatusCode = ingress.SuccessStatusCode,
    };

    private static ContextSourceWire? ToWire(ContextSource? s)
        => s is null ? null : new ContextSourceWire { From = s.From, Name = s.Name };

    private static CredentialResult ToResult(CredentialWireResponse r)
        => new() { PublicId = r.PublicId, Name = r.Name, Type = r.Type, KeyId = r.KeyId };

    private static PatchSigningWire? ToWire(DeliverySigning? s)
        => s is null ? null : new PatchSigningWire { Enabled = s.Enabled, CredentialRef = s.CredentialRef, TemplateKey = s.TemplateKey };

    private static PatchRateLimitWire? ToWire(DeliveryRateLimit? r)
        => r is null ? null : new PatchRateLimitWire { MaxRequests = r.MaxRequests, PerSeconds = r.PerSeconds };

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

    public Task<ReplayResult> ReplayToListenerAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queuePublicId)) throw new ArgumentException("A queue public id is required.", nameof(queuePublicId));
        if (string.IsNullOrWhiteSpace(eventPublicId)) throw new ArgumentException("An event public id is required.", nameof(eventPublicId));
        return _controlPlane.ReplayToListenerAsync(queuePublicId, eventPublicId, cancellationToken);
    }
}
