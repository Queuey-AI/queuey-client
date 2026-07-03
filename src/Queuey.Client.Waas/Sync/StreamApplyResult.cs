namespace Queuey.Client.Waas;

/// <summary>The outcome of applying one stream during <c>SyncModels</c>.</summary>
public sealed class StreamApplyResult
{
    /// <summary>The model type's full name, or empty for a name-only stream.</summary>
    public string ModelType { get; init; } = string.Empty;

    /// <summary>The stream name (echoes <see cref="StreamDefinition.Name"/>).</summary>
    public string Name { get; init; } = default!;

    /// <summary>Whether the apply succeeded (always <c>true</c> for a dry run).</summary>
    public bool Succeeded { get; init; }

    /// <summary>The catalog public id (<c>cat_…</c>) from the response, when applied.</summary>
    public string? PublicId { get; init; }

    /// <summary>The catalog status string (<c>Published</c>/<c>Draft</c>/<c>Deprecated</c>), when applied.</summary>
    public string? Status { get; init; }

    /// <summary>The typed error when the apply failed; otherwise <c>null</c>.</summary>
    public QueueyException? Error { get; init; }

    /// <summary>The packages this stream is declared to belong to (assigned in the package phase).</summary>
    public System.Collections.Generic.IReadOnlyList<string> Packages { get; init; } = System.Array.Empty<string>();

    /// <summary>Whether this result came from a dry run (no API call was made).</summary>
    public bool DryRun { get; init; }
}
