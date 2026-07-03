using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>
/// Inline overrides supplied when registering a stream (<c>AddStream&lt;T&gt;(cfg =&gt; …)</c>). Any value
/// left unset inherits from the type's <see cref="QueueyModelAttribute"/>, then convention.
/// </summary>
public sealed class StreamOptions
{
    /// <summary>Overrides the stream name (else attribute name, else the CLR type name).</summary>
    public string? Name { get; set; }

    /// <summary>Overrides the catalog description.</summary>
    public string? Description { get; set; }

    /// <summary>Advertised event types. When non-empty, replaces the attribute's event types.</summary>
    public IList<string> EventTypes { get; } = new List<string>();

    /// <summary>Overrides public visibility. Null inherits the attribute value (default <c>true</c>).</summary>
    public bool? IsPublic { get; set; }

    /// <summary>Packages this stream belongs to. When non-empty, replaces the attribute's packages.</summary>
    public IList<string> Packages { get; } = new List<string>();

    /// <summary>Overrides schema generation for this stream. Null inherits the attribute/builder default.</summary>
    public bool? GenerateSchema { get; set; }

    /// <summary>Supplies a pre-rendered JSON-schema string directly (wins over generation).</summary>
    public string? PayloadSchema { get; set; }
}
