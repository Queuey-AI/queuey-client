using System;
using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>
/// A network-free preview of what <c>SyncStreams</c> would apply for one stream (used by
/// <c>--dry-run</c> and tests). Mirrors a <see cref="StreamDefinition"/> without touching the API.
/// </summary>
public sealed class StreamPlan
{
    /// <summary>The model type's full name, or <c>null</c> for a name-only stream.</summary>
    public string? ModelType { get; init; }

    /// <summary>The stream name that would be applied.</summary>
    public string Name { get; init; } = default!;

    /// <summary>The catalog description that would be applied.</summary>
    public string? Description { get; init; }

    /// <summary>The event types that would be advertised.</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = Array.Empty<string>();

    /// <summary>Whether the stream would be public (Published).</summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>The packages this stream would be created in / assigned to.</summary>
    public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();

    /// <summary>Whether a payload schema would be sent.</summary>
    public bool HasPayloadSchema { get; init; }
}
