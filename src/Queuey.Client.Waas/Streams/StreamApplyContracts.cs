using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>Wire request for <c>PUT /waas/streams</c> (camelCase via <c>QueueyJson.Options</c>).</summary>
internal sealed class StreamApplyRequest
{
    public string ProducerTenantPublicId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }

    /// <summary>Null/empty is sent as <c>null</c>; the backend coalesces it to an empty list.</summary>
    public IReadOnlyList<string>? EventTypes { get; set; }

    public string? PayloadSchema { get; set; }
    public bool IsPublic { get; set; } = true;
}

/// <summary>Wire response for <c>PUT /waas/streams</c>: <c>{ publicId: "cat_…", key, status }</c>.</summary>
internal sealed class StreamApplyResponse
{
    /// <summary>Catalog entry public id (<c>cat_…</c>).</summary>
    public string? PublicId { get; set; }

    /// <summary>Catalog key (equals the stream name).</summary>
    public string? Key { get; set; }

    /// <summary>Catalog status as a string: <c>Published</c> | <c>Draft</c> | <c>Deprecated</c>.</summary>
    public string? Status { get; set; }
}

/// <summary>Wire request for <c>PUT /waas/packages</c> (idempotent upsert by producer + name).</summary>
internal sealed class PackageApplyRequest
{
    public string ProducerTenantPublicId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
}

/// <summary>Wire response for <c>PUT /waas/packages</c>: <c>{ publicId: "pkg_…", key, name, status }</c>.</summary>
internal sealed class PackageApplyResponse
{
    public string? PublicId { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
}

/// <summary>Wire request to assign a stream to a package (<c>POST …/packages/{pkg}/streams</c>).</summary>
internal sealed class AssignStreamRequest
{
    public string CatalogEntryPublicId { get; set; } = default!;
}
