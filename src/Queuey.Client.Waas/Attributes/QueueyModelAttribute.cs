using System;

namespace Queuey.Client.Waas;

/// <summary>
/// Marks a class or struct as a Queuey <b>stream</b> (a producer integration endpoint). When the type is
/// registered (<c>AddStream&lt;T&gt;()</c>) and <c>SyncModels</c> runs, the SDK applies a stream by this
/// definition via <c>PUT /waas/streams</c>: it ensures a backing queue named <see cref="Name"/> exists,
/// is public/discoverable per <see cref="IsPublic"/>, and extracts the canonical context headers so that
/// <c>PushEvent</c> to the same stream carries <c>eventType</c>/<c>key</c>.
/// </summary>
/// <remarks>
/// Every value is optional. When omitted, <see cref="Name"/> falls back to the CLR type name and
/// <see cref="IsPublic"/> defaults to <c>true</c> (mirroring the backend). Inline registration options
/// (<c>AddStream&lt;T&gt;(cfg =&gt; …)</c>) take precedence over this attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class QueueyModelAttribute : Attribute
{
    /// <summary>Creates the attribute, optionally setting the stream <see cref="Name"/>.</summary>
    public QueueyModelAttribute(string? name = null) => Name = name;

    /// <summary>
    /// Stream name — becomes the queue display name and the catalog key. Null/empty falls back to the
    /// CLR type name. This is the string you pass to <c>PushEvent</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Optional human-readable description stored on the catalog entry.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Event types advertised on the catalog entry (metadata only — not enforced at publish time).
    /// Null/empty is sent as <c>null</c>; the backend coalesces it to an empty list.
    /// </summary>
    public string[]? EventTypes { get; set; }

    /// <summary>
    /// Whether the stream is publicly discoverable by integration partners. Defaults to <c>true</c>
    /// (Published); <c>false</c> yields Draft (new) / Deprecated (existing).
    /// </summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>
    /// Names of the packages this stream belongs to. A stream can be in several packages (e.g. shared
    /// across tiers). On sync each package is idempotently created and the stream assigned to it.
    /// A stream is only visible to a partner once one of its packages is granted to that partner.
    /// </summary>
    public string[]? Packages { get; set; }

    /// <summary>
    /// Generate an advisory JSON <c>payloadSchema</c> from this type on sync (honoring
    /// <see cref="QueueyIgnoreAttribute"/>/<c>[JsonIgnore]</c>). Off by default.
    /// </summary>
    public bool GenerateSchema { get; set; }
}
