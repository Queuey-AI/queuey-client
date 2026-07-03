namespace Queuey.Client.Waas;

/// <summary>The outcome of applying one package (and assigning its streams) during <c>SyncModels</c>.</summary>
public sealed class PackageApplyResult
{
    /// <summary>The package name.</summary>
    public string Name { get; init; } = default!;

    /// <summary>Whether the package applied and all its stream assignments succeeded (always <c>true</c> for a dry run).</summary>
    public bool Succeeded { get; init; }

    /// <summary>The package public id (<c>pkg_…</c>) when applied.</summary>
    public string? PublicId { get; init; }

    /// <summary>How many streams were assigned to this package in this run.</summary>
    public int AssignedStreams { get; init; }

    /// <summary>The typed error when apply or an assignment failed; otherwise <c>null</c>.</summary>
    public QueueyException? Error { get; init; }

    /// <summary>Whether this result came from a dry run (no API call was made).</summary>
    public bool DryRun { get; init; }
}
