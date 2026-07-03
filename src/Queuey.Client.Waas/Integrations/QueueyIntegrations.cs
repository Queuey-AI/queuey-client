using System;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>Default <see cref="IQueueyIntegrations"/> over the control-plane client.</summary>
internal sealed class QueueyIntegrations : IQueueyIntegrations
{
    private readonly QueueyControlPlaneClient _controlPlane;

    public QueueyIntegrations(QueueyControlPlaneClient controlPlane)
        => _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));

    public async Task<IntegrationResult> InviteAsync(string email, CancellationToken cancellationToken = default)
    {
        IntegrationResponse r = await _controlPlane
            .InviteIntegrationAsync(Require(email, nameof(email)), cancellationToken)
            .ConfigureAwait(false);
        return new IntegrationResult { PublicId = r.PublicId, InvitedEmail = r.InvitedEmail, Status = r.Status };
    }

    public Task GrantPackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken = default)
        => _controlPlane.GrantPackageAsync(Require(integrationPublicId, nameof(integrationPublicId)), Require(packagePublicId, nameof(packagePublicId)), cancellationToken);

    public Task RevokePackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken = default)
        => _controlPlane.RevokePackageAsync(Require(integrationPublicId, nameof(integrationPublicId)), Require(packagePublicId, nameof(packagePublicId)), cancellationToken);

    public async Task<ActivationResult> ActivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken = default)
    {
        ActivationResponse r = await _controlPlane
            .ActivateAsync(Require(integrationPublicId, nameof(integrationPublicId)), Require(groupKey, nameof(groupKey)), cancellationToken)
            .ConfigureAwait(false);
        return new ActivationResult { PublicId = r.PublicId, GroupKey = r.GroupKey, Status = r.Status };
    }

    public Task DeactivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken = default)
        => _controlPlane.DeactivateAsync(Require(integrationPublicId, nameof(integrationPublicId)), Require(groupKey, nameof(groupKey)), cancellationToken);

    private static string Require(string value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value;
}
