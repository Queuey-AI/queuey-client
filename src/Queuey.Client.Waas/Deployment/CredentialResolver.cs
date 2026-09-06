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
            (byName.Count == 0
                ? "This workspace has no credentials yet."
                : $"Available: {string.Join(", ", byName.Keys.OrderBy(k => k, StringComparer.Ordinal))}."));
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
