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

    /// <param name="tenantPublicId">The workspace to read.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="effective">
    /// Every queue with the values it actually runs with, inherited or not, and its current mode —
    /// what the drift check compares against. The default is the inherit-aware file a pull writes,
    /// which leaves out whatever a queue has from its workspace; compared against a file that
    /// declares such a value, it reported drift right after a clean apply.
    /// </param>
    public async Task<DeploymentFile> PullAsync(string tenantPublicId, CancellationToken cancellationToken, bool effective = false)
    {
        var file = new DeploymentFile { Tenant = tenantPublicId };

        Dictionary<string, string> nameByRef = await LoadCredentialNamesAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);

        TenantConfigResponse config = await _controlPlane.GetTenantConfigAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);

        var workspace = new DeploymentWorkspace
        {
            // The workspace has nothing above it, so everything it resolves to IS its own — no
            // baseline to compare against, unlike a queue.
            Ordering = config.Policy?.Ordering,
            DlqEnabled = config.Policy?.DlqEnabled,
            RetentionDays = config.Policy?.RetentionDays,
            Idempotent = config.Policy?.Idempotent,
            MaxAttempts = config.Policy?.MaxAttempts,
            DlqAfterAttempts = config.Policy?.DlqAfterAttempts,
            Backoff = config.Policy?.Backoff?.ToModel(),
            RetryOnNetworkErrors = config.Policy?.RetryOnNetworkErrors,
            RetryOnTimeouts = config.Policy?.RetryOnTimeouts,
            Ingress = ToIngress(config.Ingress, effective),
        };

        if (config.Delivery is { } wd && !string.IsNullOrWhiteSpace(wd.BaseUrl))
        {
            workspace.Delivery = new WorkspaceDelivery
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

        file.Workspace = workspace;

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
            DeploymentQueue pulled = effective ? ToEffectiveQueue(qc, nameByRef) : ToDeploymentQueue(qc, nameByRef);
            pulled.Mode = effective ? EffectiveMode(queue.Mode) : DeclaredMode(queue);
            file.Queues[name] = pulled;
        }

        return file;
    }

    /// <summary>
    /// The mode a pulled file writes: only what the default would get wrong. A queue that is created
    /// from the file delivers when it has a destination and logs when it has none, so the mode is
    /// written only for a queue that logs even though it has somewhere to deliver.
    /// </summary>
    private static string? DeclaredMode(QueueListItem queue)
        => DeploymentQueueModes.FromBackend(queue.Mode) == DeploymentQueueMode.LogOnly && queue.HasDeliveryTarget
            ? DeploymentQueueMode.LogOnly.ToFileText()
            : null;

    /// <summary>The mode as the drift check compares it: the file's words, and <c>paused</c> for the old Paused mode.</summary>
    private static string? EffectiveMode(string? backendMode)
        => DeploymentQueueModes.FromBackend(backendMode)?.ToFileText()
           ?? (string.IsNullOrWhiteSpace(backendMode) ? null : backendMode!.ToLowerInvariant());

    /// <summary>What a queue runs with — every policy field and its whole ingress, inherited or not.</summary>
    private static DeploymentQueue ToEffectiveQueue(QueueConfigResponse qc, Dictionary<string, string> nameByRef)
    {
        DeploymentQueue queue = ToDeploymentQueue(qc, nameByRef);

        if (qc.Policy is { } p)
        {
            queue.Ordering = p.Ordering;
            queue.DlqEnabled = p.DlqEnabled;
            queue.RetentionDays = p.RetentionDays;
            queue.Idempotent = p.Idempotent;
            queue.MaxAttempts = p.MaxAttempts;
            queue.DlqAfterAttempts = p.DlqAfterAttempts;
            queue.Backoff = p.Backoff?.ToModel();
            queue.RetryOnNetworkErrors = p.RetryOnNetworkErrors;
            queue.RetryOnTimeouts = p.RetryOnTimeouts;
            queue.Filter = p.Filter?.ToModel();
        }

        queue.Ingress = ToIngress(qc.Ingress, effective: true);
        return queue;
    }

    private static DeploymentQueue ToDeploymentQueue(QueueConfigResponse qc, Dictionary<string, string> nameByRef)
    {
        var declared = new DeploymentQueue();

        // Behaviour, per field. The server's Inherited.Behavior is a single flag for the WHOLE policy
        // block, so a queue that overrides one field reports every field as owned — trusting it would
        // write today's workspace defaults into the file and freeze them as permanent per-queue
        // overrides on the next apply, which is exactly what inherit-awareness is for. Compare each
        // field against the workspace baseline the same response carries, and emit only what differs.
        if (qc.Inherited?.Behavior == false && qc.Policy is { } p)
        {
            QueuePolicyResponse? baseline = qc.TenantBaseline?.Policy;

            declared.Ordering = DifferentOrNull(p.Ordering, baseline?.Ordering);
            declared.DlqEnabled = DifferentOrNull(p.DlqEnabled, baseline?.DlqEnabled);
            declared.RetentionDays = DifferentOrNull(p.RetentionDays, baseline?.RetentionDays);
            declared.Idempotent = DifferentOrNull(p.Idempotent, baseline?.Idempotent);
            declared.MaxAttempts = DifferentOrNull(p.MaxAttempts, baseline?.MaxAttempts);
            declared.DlqAfterAttempts = DifferentOrNull(p.DlqAfterAttempts, baseline?.DlqAfterAttempts);
            declared.RetryOnNetworkErrors = DifferentOrNull(p.RetryOnNetworkErrors, baseline?.RetryOnNetworkErrors);
            declared.RetryOnTimeouts = DifferentOrNull(p.RetryOnTimeouts, baseline?.RetryOnTimeouts);

            // Backoff and filter are small objects: written whole when they differ, left out when equal.
            if (p.Backoff is { } backoff && !SameBackoff(backoff, baseline?.Backoff))
                declared.Backoff = backoff.ToModel();
            if (p.Filter is { } filter && DeploymentDrift.DescribeFilter(filter.ToModel()) != DeploymentDrift.DescribeFilter(baseline?.Filter?.ToModel()))
                declared.Filter = filter.ToModel();
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

        DeploymentIngress? queueIngress = ToIngress(qc.Ingress);
        DeploymentIngress? baselineIngress = ToIngress(qc.TenantBaseline?.Ingress);
        if (queueIngress is not null && !SameIngress(queueIngress, baselineIngress))
            declared.Ingress = queueIngress;

        return declared;
    }

    /// <summary>
    /// Whether a queue's effective ingress is just the workspace's. The read-back is effective, not
    /// raw, so without this every queue would write out the workspace's sources as its own — the
    /// same trap the per-field policy comparison exists to avoid.
    /// </summary>
    private static bool SameIngress(DeploymentIngress a, DeploymentIngress? b)
        => b is not null
        && string.Equals(a.AuthMode, b.AuthMode, StringComparison.Ordinal)
        && a.SuccessStatusCode == b.SuccessStatusCode
        && SameSource(a.EventType, b.EventType)
        && SameSource(a.GroupKey, b.GroupKey);

    private static bool SameBackoff(RetryBackoffWire a, RetryBackoffWire? b)
        => b is not null && a.BaseDelayMs == b.BaseDelayMs && a.MaxDelayMs == b.MaxDelayMs
           && string.Equals(a.Jitter, b.Jitter, StringComparison.OrdinalIgnoreCase);

    private static bool SameSource(ContextSource? a, ContextSource? b)
        => a is null
            ? b is null
            : b is not null && string.Equals(a.From, b.From, StringComparison.Ordinal)
                            && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    /// <summary>
    /// The queue's value when it differs from the workspace's, else null. No baseline to compare
    /// against means we cannot tell inherited from owned — emit it, since a file that says too much
    /// is recoverable and one that silently drops an override is not.
    /// </summary>
    private static string? DifferentOrNull(string? queue, string? baseline)
        => queue is null || (baseline is not null && string.Equals(queue, baseline, StringComparison.Ordinal))
            ? null
            : queue;

    private static int? DifferentOrNull(int? queue, int? baseline)
        => queue is null || (baseline is not null && queue == baseline) ? null : queue;

    private static bool? DifferentOrNull(bool? queue, bool? baseline)
        => queue is null || (baseline is not null && queue == baseline) ? null : queue;

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

    /// <summary>
    /// The read-back is the EFFECTIVE ingress, so a queue's equals the workspace's unless it
    /// overrode something — the caller compares to decide whether to write it out.
    /// </summary>
    /// <remarks>
    /// The success status was not read before 2026-09-23, so a file declaring 200 drifted forever.
    /// A pulled file writes it only when it is not the default 202; the effective read always does.
    /// </remarks>
    private static DeploymentIngress? ToIngress(IngressResponse? r, bool effective = false)
    {
        if (r is null) return null;

        var ingress = new DeploymentIngress
        {
            AuthMode = NullIfNone(r.AuthMode),
            EventType = ToSource(r.EventType),
            GroupKey = ToSource(r.GroupKey),
            SuccessStatusCode = r.SuccessStatusCode == 0 || (!effective && r.SuccessStatusCode == DefaultSuccessStatusCode)
                ? null
                : r.SuccessStatusCode,
        };

        return ingress.IsEmpty ? null : ingress;
    }

    private const int DefaultSuccessStatusCode = 202;

    private static ContextSource? ToSource(ContextSourceWire? w)
        => w is null || string.IsNullOrWhiteSpace(w.Name) ? null : new ContextSource(w.From, w.Name);

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
