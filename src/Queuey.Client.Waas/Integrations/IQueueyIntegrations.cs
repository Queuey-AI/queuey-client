using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Producer-side management of <b>integration partners</b> (the integrators a producer distributes to) —
/// distinct from the platform-level partner-relation network. Delivery to an integration requires two
/// gates: the integration is <b>granted</b> a package containing the stream <i>and</i> an <b>active
/// activation</b> for the group key (plus the integration's own subscription).
/// </summary>
public interface IQueueyIntegrations
{
    /// <summary>Invites an integration partner by email. Returns the new integration (<c>int_…</c>).</summary>
    Task<IntegrationResult> InviteAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Grants a package to an integration (the access boundary — the integration may then see/subscribe). Idempotent.</summary>
    Task GrantPackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken = default);

    /// <summary>Revokes a package grant from an integration. Idempotent.</summary>
    Task RevokePackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken = default);

    /// <summary>Activates a group key for an integration — the second routing gate. Returns the activation (<c>act_…</c>).</summary>
    Task<ActivationResult> ActivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken = default);

    /// <summary>Deactivates a group key for an integration.</summary>
    Task DeactivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken = default);
}

/// <summary>An invited integration partner.</summary>
public sealed class IntegrationResult
{
    /// <summary>Integration public id (<c>int_…</c>).</summary>
    public string? PublicId { get; init; }

    /// <summary>The email the integration was invited with.</summary>
    public string? InvitedEmail { get; init; }

    /// <summary>Integration status (e.g. <c>Invited</c>, <c>Linked</c>).</summary>
    public string? Status { get; init; }
}

/// <summary>An activation of a group key for an integration.</summary>
public sealed class ActivationResult
{
    /// <summary>Activation public id (<c>act_…</c>).</summary>
    public string? PublicId { get; init; }

    /// <summary>The activated group key.</summary>
    public string? GroupKey { get; init; }

    /// <summary>Activation status.</summary>
    public string? Status { get; init; }
}
