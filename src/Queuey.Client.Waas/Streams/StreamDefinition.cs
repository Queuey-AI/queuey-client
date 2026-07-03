using System;
using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>
/// The resolved, immutable definition of a Queuey stream — the desired state that <c>SyncModels</c>
/// applies via <c>PUT /waas/streams</c>. Produced from a <see cref="QueueyModelAttribute"/>, inline
/// registration options, or convention.
/// </summary>
public sealed class StreamDefinition
{
    /// <summary>The CLR model type this stream was derived from, or <c>null</c> for a name-only stream.</summary>
    public Type? ModelType { get; init; }

    /// <summary>Stream name — the queue display name and catalog key; the value passed to <c>PushEvent</c>.</summary>
    public string Name { get; init; } = default!;

    /// <summary>Optional catalog description.</summary>
    public string? Description { get; init; }

    /// <summary>Advertised event types (catalog metadata only).</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = Array.Empty<string>();

    /// <summary>Whether the stream is publicly discoverable (Published) vs internal (Draft/Deprecated).</summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>Packages this stream belongs to (created + assigned on sync).</summary>
    public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();

    /// <summary>Optional pre-rendered JSON-schema string stored verbatim on the catalog entry. Null in Phase 1.</summary>
    public string? PayloadSchema { get; init; }
}
