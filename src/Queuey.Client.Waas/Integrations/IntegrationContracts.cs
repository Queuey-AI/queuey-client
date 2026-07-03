namespace Queuey.Client.Waas;

/// <summary>Wire request for <c>POST /waas/integrations</c> (invite an integration partner by email).</summary>
internal sealed class InviteIntegrationRequest
{
    public string ProducerTenantPublicId { get; set; } = default!;
    public string Email { get; set; } = default!;
}

/// <summary>Wire response for an integration: <c>{ publicId: "int_…", invitedEmail, status }</c>.</summary>
internal sealed class IntegrationResponse
{
    public string? PublicId { get; set; }
    public string? InvitedEmail { get; set; }
    public string? Status { get; set; }
}

/// <summary>Wire request to grant a package to an integration (<c>POST …/packages/{pkg}/grants</c>).</summary>
internal sealed class GrantPackageRequest
{
    public string IntegrationPublicId { get; set; } = default!;
}

/// <summary>Wire request for activation (<c>POST</c>/<c>DELETE /waas/activations</c>).</summary>
internal sealed class ActivationRequest
{
    public string ProducerTenantPublicId { get; set; } = default!;
    public string GroupKey { get; set; } = default!;
    public string IntegrationPublicId { get; set; } = default!;
}

/// <summary>Wire response for an activation: <c>{ publicId: "act_…", groupKey, status }</c>.</summary>
internal sealed class ActivationResponse
{
    public string? PublicId { get; set; }
    public string? GroupKey { get; set; }
    public string? Status { get; set; }
}
