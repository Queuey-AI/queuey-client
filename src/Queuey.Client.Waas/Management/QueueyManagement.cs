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

        return new TenantResult { PublicId = r.PublicId, DisplayName = r.DisplayName, Status = r.Status, Kind = r.Kind, Environment = r.Environment };
    }

    // Gullflyten 2026-10-09: et dev-workspace kunne bare lages med POST /tenants direkte, og create-tenant laget prod. En nøkkel
    // setter miljøet når den lager workspacet (Queuey F2.2), men senker det aldri etterpå. Bare en intern hjelper, som
    // RotateCredentialAsync: IQueueyManagement er offentlig i en tagget versjon. `queuey create-tenant` og `queuey apply` kaller
    // den.
    internal async Task<TenantResult> CreateWorkspaceAsync(
        string displayName, string environment, bool asProducer = false, bool withDefaultQueue = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(environment)) throw new ArgumentException("An environment is required.", nameof(environment));

        string wanted = environment.Trim().ToLowerInvariant();
        TenantSummaryResponse r = await _controlPlane
            .CreateTenantAsync(displayName.Trim(), asProducer, withDefaultQueue, cancellationToken, wanted)
            .ConfigureAwait(false);

        // En Queuey fra før F2.2 tar ikke imot feltet, og lager et workspace uten merke, som regnes som prod. Det sies, med id-en,
        // i stedet for at kalleren tror den har et dev-workspace.
        if (!string.Equals(r.Environment?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            throw new QueueyException(
                $"Queuey created workspace {r.PublicId} but did not mark it {wanted}: it is "
                + (string.IsNullOrWhiteSpace(r.Environment) ? "unmarked, which counts as prod" : $"marked {r.Environment!.Trim().ToLowerInvariant()}")
                + ". This Queuey does not take an environment when it creates a workspace.", errorCode: "environment_not_set")
            {
                SuggestedAction = $"Ask a person to mark workspace {r.PublicId} {wanted} in the Queuey console, or delete it there.",
            };

        return new TenantResult { PublicId = r.PublicId, DisplayName = r.DisplayName, Status = r.Status, Kind = r.Kind, Environment = r.Environment };
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
            Deployment = r.Deployment?.ToInfo(),
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
        string? keyId = null, string? username = null, bool replace = false, CancellationToken cancellationToken = default)
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
            Replace = replace ? true : null,
        }, cancellationToken).ConfigureAwait(false);

        // Som rotasjonen (Queuey #511): gir policyen lagringen til en person, svarer Queuey 202, og ingenting er lagret. Sjekket
        // defensivt (security-review av #69, K5), så den som kaller, aldri tar et 202 for en lagret verdi.
        if (string.Equals(r.Status, CredentialRotationPendingException.PendingApproval, StringComparison.Ordinal))
            throw new CredentialRotationPendingException(r.Message ?? "A person stores this secret; nothing was stored.",
                r.CredentialRequest, r.ApprovalUrl, r.ExpiresAt, r.PolicyRule);

        return ToResult(r);
    }

    // Queuey F3.7: rotasjonen. Bare en intern hjelper, som WireOfEnvironment: IQueueyManagement er offentlig i en tagget
    // versjon, og et nytt medlem der ville brutt dem som implementerer det. `queuey credentials rotate` kaller den.
    internal async Task<CredentialResult> RotateCredentialAsync(
        string tenantPublicId, string name, string secret, int? graceMinutes, int? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A credential name is required.", nameof(name));
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("A secret is required.", nameof(secret));

        CredentialWireResponse r = await _controlPlane.RotateCredentialAsync(tenantPublicId, new RotateCredentialWireRequest
        {
            Name = name.Trim(),
            Secret = secret,
            GraceMinutes = graceMinutes,
            ExpectedVersion = expectedVersion,
        }, cancellationToken).ConfigureAwait(false);

        // Queuey #511 (2026-10-09): en nøkkels rotasjon uten vindu i prod svarer 202, og en person limer inn verdien. Ingenting er
        // lagret, og verdien som ble sendt, er ikke beholdt.
        if (string.Equals(r.Status, CredentialRotationPendingException.PendingApproval, StringComparison.Ordinal))
            throw new CredentialRotationPendingException(r.Message ?? "A person approves this rotation by pasting the new value.",
                r.CredentialRequest, r.ApprovalUrl, r.ExpiresAt, r.PolicyRule);

        return ToResult(r);
    }

    public async Task<IReadOnlyList<CredentialResult>> ListCredentialsAsync(string tenantPublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));

        List<CredentialWireResponse> rows = await _controlPlane.ListCredentialsAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);
        return rows.Select(ToResult).ToArray();
    }

    public async Task<CredentialRequestResult> RequestCredentialAsync(
        string tenantPublicId, string name, string? type = null, string? keyId = null, string? username = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantPublicId)) throw new ArgumentException("A tenant public id is required.", nameof(tenantPublicId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A credential name is required.", nameof(name));

        CredentialRequestWireResponse r = await _controlPlane.RequestCredentialAsync(tenantPublicId, new CreateCredentialRequestWireRequest
        {
            Name = name.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? null : type!.Trim(),
            KeyId = string.IsNullOrWhiteSpace(keyId) ? null : keyId!.Trim(),
            Username = string.IsNullOrWhiteSpace(username) ? null : username!.Trim(),
        }, cancellationToken).ConfigureAwait(false);

        return new CredentialRequestResult
        {
            RequestId = r.RequestId,
            WorkspaceId = r.WorkspaceId,
            WorkspaceName = r.WorkspaceName,
            OrganizationName = r.OrganizationName,
            Name = r.Name,
            Type = r.Type,
            KeyId = r.KeyId,
            Username = r.Username,
            Status = r.Status,
            Url = r.Url,
            RequestedBy = r.RequestedBy,
            CreatedAt = r.CreatedAt,
            ExpiresAt = r.ExpiresAt,
            FulfilledAt = r.FulfilledAt,
            CredentialId = r.CredentialId,
            CredentialVersion = r.CredentialVersion,
            ReplacesCredentialId = r.ReplacesCredentialId,
        };
    }

    public Task<IngressSigningKey> MintIngressKeyAsync(string queuePublicId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queuePublicId)) throw new ArgumentException("A queue public id is required.", nameof(queuePublicId));
        return MintKeyAsync(queuePublicId, null, name, IngressKeyTypes.Signing, cancellationToken);
    }

    /// <summary>
    /// Mints a key (Queuey #513): for <paramref name="queuePublicId"/>, or without one for every queue of
    /// <paramref name="tenantPublicId"/>; a signing key or, with <see cref="IngressKeyTypes.ApiKey"/>, an API key that can only
    /// publish there. A mint a person must decide on throws: 202 as <see cref="IngressKeyPendingException"/>, and 403
    /// <c>approval_required</c> as <see cref="QueueyForbiddenException"/> with the console link in its action.
    /// </summary>
    internal async Task<IngressSigningKey> MintKeyAsync(
        string? queuePublicId, string? tenantPublicId, string name, string type, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A key name is required.", nameof(name));
        var request = new CreateQueueHmacClientWireRequest { Name = name.Trim(), Type = type == IngressKeyTypes.Signing ? null : type };

        CreateQueueHmacClientWireResponse r = queuePublicId is not null
            ? await _controlPlane.MintIngressKeyAsync(queuePublicId, request, cancellationToken).ConfigureAwait(false)
            : await _controlPlane.MintWorkspaceKeyAsync(tenantPublicId ?? throw new ArgumentException("A queue or a workspace is required."),
                request, cancellationToken).ConfigureAwait(false);

        if (string.Equals(r.Status, CredentialRotationPendingException.PendingApproval, StringComparison.Ordinal))
            throw new IngressKeyPendingException(r.Message ?? "A person decides on this key in Queuey's inbox; nothing was minted.",
                r.ApprovalUrl, r.ExpiresAt, r.PolicyRule);

        return new IngressSigningKey
        {
            ClientPublicId = r.ClientPublicId,
            ClientName = r.ClientName,
            KeyId = r.KeyId,
            Secret = r.Secret,
            QueuePublicId = r.QueuePublicId,
            Type = r.Type ?? type,
            Scope = r.Scope ?? (queuePublicId is null ? "workspace" : "queue"),
            TenantPublicId = r.TenantPublicId ?? tenantPublicId,
            Origin = r.Origin,
        };
    }

    /// <summary>The keys that publish to every queue of <paramref name="tenantPublicId"/>: metadata, never a secret.</summary>
    internal async Task<IReadOnlyList<QueueHmacClientWireResponse>> ListWorkspaceKeysAsync(string tenantPublicId, CancellationToken cancellationToken = default)
        => await _controlPlane.ListWorkspaceKeysAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);

    /// <summary>The signing keys of <paramref name="queuePublicId"/>, active and revoked: metadata, never a secret.</summary>
    internal async Task<IReadOnlyList<QueueHmacClientWireResponse>> ListIngressKeysAsync(string queuePublicId, CancellationToken cancellationToken = default)
        => await _controlPlane.ListIngressKeysAsync(queuePublicId, cancellationToken).ConfigureAwait(false);

    /// <summary>Revokes signing key <paramref name="keyId"/>: every producer that signs with it is refused at once.</summary>
    internal Task RevokeIngressKeyAsync(string keyId, string? reason, CancellationToken cancellationToken = default)
        => _controlPlane.RevokeIngressKeyAsync(keyId, reason, cancellationToken);

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
        Backoff = RetryBackoffWire.From(policy.Backoff),
    };

    // Samme body for apply og for planen (?dryRun=true), og med små bokstaver, slik Queuey lagrer merket. Bare en intern
    // hjelper: IQueueyManagement er offentlig i den taggede v0.1.0-preview.8, og et nytt medlem der ville brutt dem som
    // implementerer det (review av #45, 2026-10-06). apply og plan sender PATCH /tenants/{ten} selv.
    internal static PatchWorkspaceWireRequest WireOfEnvironment(string environment) => new()
    {
        Environment = environment.Trim().ToLowerInvariant(),
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
        SignedRequest = ingress.SignedRequest is { } signed
            ? new SignedRequestWire { Template = signed.Template?.Trim(), CredentialRef = signed.CredentialRef?.Trim() }
            : null,
    };

    // Et tomt navn fjerner kilden, og da leser backenden ikke from. Feltet står likevel i ContextSourceRequest(From, Name),
    // så det sendes som header når fila ikke har skrevet det.
    private static ContextSourceWire? ToWire(ContextSource? s)
        => s is null ? null : new ContextSourceWire
        {
            From = s.From?.Trim() is { Length: > 0 } from ? from : "header",
            Name = s.Name ?? string.Empty,
        };

    private static CredentialResult ToResult(CredentialWireResponse r)
        => new()
        {
            PublicId = r.PublicId, Name = r.Name, Type = r.Type, KeyId = r.KeyId,
            Version = r.Version, Created = r.Created, BoundWorkspace = r.BoundWorkspace, BoundQueues = r.BoundQueues,
            SecretReplaced = r.Created == false ? r.SecretReplaced : null,
            PreviousVersionValidUntil = r.PreviousVersionValidUntil,
            GraceWindowClosed = r.GraceWindowClosed,
        };

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
