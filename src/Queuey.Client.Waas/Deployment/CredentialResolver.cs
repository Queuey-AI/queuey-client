using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Turns the credential <b>names</b> a deployment file carries into the <c>cred_…</c> public ids the
/// API stores, resolved once per run against the workspace's credentials.
/// </summary>
/// <remarks>
/// Names are the contract a committed file needs: ids are minted per workspace, so a file holding
/// one applies only to the environment it was written in. Resolving here is what lets a single file
/// converge staging and production.
/// </remarks>
internal sealed class CredentialResolver
{
    private const string IdPrefix = "cred_";

    private readonly IQueueyManagement _management;
    private readonly string _tenantPublicId;
    private Dictionary<string, string>? _byName;

    public CredentialResolver(IQueueyManagement management, string tenantPublicId)
    {
        _management = management;
        _tenantPublicId = tenantPublicId;
    }

    /// <summary>
    /// Resolves every credential name a deployment names — the workspace's delivery and each
    /// queue's, signing included — before anything is written. Every missing name is reported at
    /// once, and nothing has been sent when it is.
    /// </summary>
    public async Task<ResolvedDeliveries> ResolveAllAsync(
        WorkspaceDelivery? workspace, IEnumerable<DeploymentQueuePlan> plans, CancellationToken cancellationToken)
    {
        // Før 2026-09-24 ble en køs credential-navn slått opp først etter at køen var opprettet. Et navn
        // som manglet, feilet køen der og lot den ligge i logOnly, og neste apply beholdt modusen og ga
        // exit 0. Derfor slås alle navn opp her, før første skriving.
        var references = new List<(string Name, string Where)>();
        Collect(workspace?.CredentialRef, "workspace.delivery.credentialRef");
        Collect(workspace?.Signing?.CredentialRef, "workspace.delivery.signing.credentialRef");
        foreach (DeploymentQueuePlan plan in plans)
        {
            Collect(plan.Delivery?.CredentialRef, $"queues.{plan.Definition.Name}.delivery.credentialRef");
            Collect(plan.Delivery?.Signing?.CredentialRef, $"queues.{plan.Definition.Name}.delivery.signing.credentialRef");
        }

        if (references.Count > 0)
        {
            Dictionary<string, string> byName = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var missing = references.Where(r => !byName.ContainsKey(r.Name)).ToList();
            if (missing.Count > 0)
                throw Missing(missing, byName);
        }

        var queues = new Dictionary<string, QueueDelivery>(StringComparer.Ordinal);
        foreach (DeploymentQueuePlan plan in plans)
        {
            if (plan.Delivery is { } delivery)
                queues[plan.Definition.Name] = await ResolveAsync(delivery, cancellationToken).ConfigureAwait(false);
        }

        return new ResolvedDeliveries(
            workspace is null ? null : await ResolveAsync(workspace, cancellationToken).ConfigureAwait(false),
            queues);

        void Collect(string? reference, string where)
        {
            if (string.IsNullOrWhiteSpace(reference)) return;
            string name = reference!.Trim();
            if (!name.StartsWith(IdPrefix, StringComparison.Ordinal))
                references.Add((name, where));
        }
    }

    private QueueyConfigurationException Missing(List<(string Name, string Where)> missing, Dictionary<string, string> byName)
    {
        var names = missing.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();

        return new QueueyConfigurationException(
            (names.Count == 1
                ? $"No credential named '{names[0]}' in workspace {_tenantPublicId}"
                : $"No credentials named {string.Join(", ", names.Select(n => $"'{n}'"))} in workspace {_tenantPublicId}")
            + $" ({string.Join("; ", missing.Select(m => m.Where))}). Nothing was changed. "
            + (names.Count == 1
                ? $"Store it first: queuey credentials set --name {names[0]} --from-env <ENV_VAR>. "
                : "Store each first: queuey credentials set --name <name> --from-env <ENV_VAR>. ")
            + Available(byName));
    }

    private static string Available(Dictionary<string, string> byName) => byName.Count == 0
        ? "This workspace has no credentials yet."
        : $"Available: {string.Join(", ", byName.Keys.OrderBy(k => k, StringComparer.Ordinal))}.";

    /// <summary>
    /// Resolves one reference. Null/blank passes through (blank means "keep the stored secret"), and
    /// so does a literal <c>cred_…</c> id. Anything else is looked up by name.
    /// </summary>
    public async Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return reference;

        string name = reference!.Trim();
        if (name.StartsWith(IdPrefix, StringComparison.Ordinal))
            return name;

        Dictionary<string, string> byName = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (byName.TryGetValue(name, out string? publicId))
            return publicId;

        throw new QueueyConfigurationException(
            $"No credential named '{name}' in workspace {_tenantPublicId}. " +
            $"Store it first: queuey credentials set --name {name} --from-env <ENV_VAR>. " +
            Available(byName));
    }

    /// <summary>Resolves both halves of a workspace delivery patch, returning a copy safe to send.</summary>
    public async Task<WorkspaceDelivery> ResolveAsync(WorkspaceDelivery delivery, CancellationToken cancellationToken)
        => new()
        {
            BaseUrl = delivery.BaseUrl,
            AuthMode = delivery.AuthMode,
            CredentialRef = await ResolveAsync(delivery.CredentialRef, cancellationToken).ConfigureAwait(false),
            AuthHeaderName = delivery.AuthHeaderName,
            Method = delivery.Method,
            TimeoutMs = delivery.TimeoutMs,
            Signing = await ResolveAsync(delivery.Signing, cancellationToken).ConfigureAwait(false),
            RateLimit = delivery.RateLimit,
        };

    /// <summary>Resolves both halves of a queue delivery patch, returning a copy safe to send.</summary>
    public async Task<QueueDelivery> ResolveAsync(QueueDelivery delivery, CancellationToken cancellationToken)
        => new()
        {
            Url = delivery.Url,
            Inherit = delivery.Inherit,
            AuthMode = delivery.AuthMode,
            CredentialRef = await ResolveAsync(delivery.CredentialRef, cancellationToken).ConfigureAwait(false),
            AuthHeaderName = delivery.AuthHeaderName,
            TimeoutMs = delivery.TimeoutMs,
            Signing = await ResolveAsync(delivery.Signing, cancellationToken).ConfigureAwait(false),
            RateLimit = delivery.RateLimit,
        };

    private async Task<DeliverySigning?> ResolveAsync(DeliverySigning? signing, CancellationToken cancellationToken)
        => signing is null ? null : new DeliverySigning
        {
            Enabled = signing.Enabled,
            CredentialRef = await ResolveAsync(signing.CredentialRef, cancellationToken).ConfigureAwait(false),
            TemplateKey = signing.TemplateKey,
        };

    /// <summary>The workspace's credentials, fetched once per run. Later names hit the cache.</summary>
    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_byName is not null)
            return _byName;

        IReadOnlyList<CredentialResult> credentials =
            await _management.ListCredentialsAsync(_tenantPublicId, cancellationToken).ConfigureAwait(false);

        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CredentialResult c in credentials)
        {
            if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.PublicId))
                byName[c.Name!] = c.PublicId!;
        }

        return _byName = byName;
    }
}

/// <summary>The deliveries of a deployment with every credential name turned into the id the API stores.</summary>
internal sealed class ResolvedDeliveries
{
    public ResolvedDeliveries(WorkspaceDelivery? workspace, IReadOnlyDictionary<string, QueueDelivery> queues)
    {
        Workspace = workspace;
        Queues = queues;
    }

    /// <summary>The workspace's delivery patch, or null when the file declares none.</summary>
    public WorkspaceDelivery? Workspace { get; }

    /// <summary>Each queue's delivery patch, by queue name — only the queues that declare one.</summary>
    public IReadOnlyDictionary<string, QueueDelivery> Queues { get; }
}
