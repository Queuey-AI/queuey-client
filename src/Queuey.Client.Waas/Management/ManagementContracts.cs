namespace Queuey.Client.Waas;

/// <summary>Wire request for <c>POST /tenants</c>. <c>LicenseId = 0</c> → backend uses <c>X-License-PublicId</c>.</summary>
internal sealed class CreateTenantWireRequest
{
    public long LicenseId { get; set; }
    public string DisplayName { get; set; } = default!;
    public bool CreateAsProducer { get; set; }
    public bool CreateDefaultQueue { get; set; }
}

/// <summary>Wire response for a tenant: <c>{ publicId: "ten_…", displayName, status, kind }</c>.</summary>
internal sealed class TenantSummaryResponse
{
    public string? PublicId { get; set; }
    public string? DisplayName { get; set; }
    public string? Status { get; set; }
    public string? Kind { get; set; }
}

/// <summary>Wire request for <c>POST /queues</c>.</summary>
internal sealed class CreateQueueWireRequest
{
    public string TenantPublicId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
}

/// <summary>Wire response for a queue: <c>{ publicId: "que_…", tenantPublicId, displayName, … }</c>.</summary>
internal sealed class QueueReadResponse
{
    public string? PublicId { get; set; }
    public string? TenantPublicId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>Wire request for <c>PUT /waas/producer/{tenant}/packages/{pkg}</c> (update name/description).</summary>
internal sealed class UpdatePackageWireRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}
