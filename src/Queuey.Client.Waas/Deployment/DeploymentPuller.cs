using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Reads a workspace back into a <see cref="DeploymentFile"/> — the inverse of <c>apply</c>. Configure
/// Queuey in the console once, pull it, commit it, and every other environment converges from the
/// same file.
/// </summary>
/// <remarks>
/// <para>
/// The result is <b>safe to commit by construction</b>. Queuey's read surfaces never return secret
/// values — only <c>HasCredential</c> flags and the <c>cred_…</c> reference — so there is no path by
/// which a pull could emit one. References are translated back to credential names, because ids are
/// minted per workspace and a file carrying one would apply only where it was written.
/// </para>
/// <para>
/// It is also <b>inherit-aware</b>: a queue that inherits a section contributes nothing for it. The
/// point of the file is to say what the code owns, and writing out every inherited value would turn
/// today's workspace defaults into permanent per-queue overrides the first time it was applied.
/// </para>
/// </remarks>
internal sealed class DeploymentPuller
{
    private readonly QueueyControlPlaneClient _controlPlane;
    private readonly IQueueyManagement _management;

    public DeploymentPuller(QueueyControlPlaneClient controlPlane, IQueueyManagement management)
    {
        _controlPlane = controlPlane;
        _management = management;
    }

    public async Task<DeploymentFile> PullAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        var file = new DeploymentFile { Tenant = tenantPublicId };

        Dictionary<string, string> nameByRef = await LoadCredentialNamesAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);

        TenantConfigResponse config = await _controlPlane.GetTenantConfigAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);
        if (config.Delivery is { } wd && !string.IsNullOrWhiteSpace(wd.BaseUrl))
        {
            file.Workspace = new WorkspaceDelivery
            {
                BaseUrl = wd.BaseUrl,
                AuthMode = NullIfNone(wd.AuthMode),
                CredentialRef = NameFor(nameByRef, wd.CredentialRef),
                AuthHeaderName = wd.AuthHeaderName,
                Method = wd.Method,
                TimeoutMs = wd.TimeoutMs,
                Signing = ToSigning(wd.Signing, nameByRef),
                RateLimit = ToRateLimit(wd.RateLimit),
            };
        }

        IReadOnlyList<QueueListItem> queues = await _management.ListQueuesAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);

        foreach (QueueListItem queue in queues.OrderBy(q => q.DisplayName, StringComparer.Ordinal))
        {
            if (queue.DisplayName is not { } name || queue.PublicId is not { } id)
                continue;

            // Names that predate the server's own validator still exist and still route. They cannot
            // be expressed in a file the apply path would accept, so skip them rather than emit a
            // file that fails the moment anyone runs it.
            if (!QueueyName.IsValid(name))
                continue;

            QueueConfigResponse qc = await _controlPlane.GetQueueConfigAsync(id, cancellationToken).ConfigureAwait(false);
            file.Queues[name] = ToDeploymentQueue(qc, nameByRef);
        }

        return file;
    }

    private static DeploymentQueue ToDeploymentQueue(QueueConfigResponse qc, Dictionary<string, string> nameByRef)
    {
        var declared = new DeploymentQueue();

        // Behaviour: only when the queue actually overrides it. An inheriting queue writes nothing,
        // so the file keeps saying "the workspace decides".
        if (qc.Inherited?.Behavior == false && qc.Policy is { } p)
        {
            declared.Ordering = p.Ordering;
            declared.MaxAttempts = p.MaxAttempts;
            declared.DlqEnabled = p.DlqEnabled;
            declared.DlqAfterAttempts = p.DlqAfterAttempts;
            declared.RetentionDays = p.RetentionDays;
            declared.Idempotent = p.Idempotent;
        }

        bool ownsDestination = qc.Inherited?.Destination == false;
        bool ownsAuth = qc.Inherited?.Auth == false;
        bool ownsSigning = qc.Inherited?.Signing == false;
        bool ownsRateLimit = qc.Inherited?.RateLimit == false;

        if (ownsDestination || ownsAuth || ownsSigning || ownsRateLimit)
        {
            var d = qc.Delivery;
            declared.Delivery = new QueueDelivery
            {
                // The queue config read-back returns the queue's RAW override here — a relative path
                // for an append, an absolute URL for a full override, null when it inherits.
                Url = ownsDestination ? d?.BaseUrl : null,
                AuthMode = ownsAuth ? NullIfNone(d?.AuthMode) : null,
                CredentialRef = ownsAuth ? NameFor(nameByRef, d?.CredentialRef) : null,
                AuthHeaderName = ownsAuth ? d?.AuthHeaderName : null,
                Signing = ownsSigning ? ToSigning(d?.Signing, nameByRef) : null,
                RateLimit = ownsRateLimit ? ToRateLimit(d?.RateLimit) : null,
            };
        }

        return declared;
    }

    private async Task<Dictionary<string, string>> LoadCredentialNamesAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        var byRef = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (CredentialResult c in await _management.ListCredentialsAsync(tenantPublicId, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(c.PublicId) && !string.IsNullOrWhiteSpace(c.Name))
                    byRef[c.PublicId!] = c.Name!;
            }
        }
        catch (QueueyForbiddenException)
        {
            // Listing credentials needs a permission a read-only pull may not hold. Falling back to
            // the raw cred_… reference keeps the pull working; it just yields a file pinned to this
            // workspace, which the emitted comment says out loud.
        }

        return byRef;
    }

    /// <summary>A credential's name when we could read it, else the raw reference.</summary>
    private static string? NameFor(Dictionary<string, string> nameByRef, string? reference)
        => string.IsNullOrWhiteSpace(reference) ? null
         : nameByRef.TryGetValue(reference!, out string? name) ? name
         : reference;

    private static DeliverySigning? ToSigning(DeliverySigningResponse? s, Dictionary<string, string> nameByRef)
        => s is null || !s.Enabled ? null : new DeliverySigning
        {
            Enabled = true,
            CredentialRef = NameFor(nameByRef, s.CredentialRef),
            TemplateKey = s.TemplateKey,
        };

    private static DeliveryRateLimit? ToRateLimit(DeliveryRateLimitResponse? r)
        => r is null || (r.MaxRequests is null && r.PerSeconds is null)
            ? null
            : new DeliveryRateLimit { MaxRequests = r.MaxRequests, PerSeconds = r.PerSeconds };

    /// <summary>"None" is the absence of auth, not a value worth writing into a file.</summary>
    private static string? NullIfNone(string? authMode)
        => string.IsNullOrWhiteSpace(authMode) || string.Equals(authMode, "None", StringComparison.OrdinalIgnoreCase)
            ? null
            : authMode;
}
