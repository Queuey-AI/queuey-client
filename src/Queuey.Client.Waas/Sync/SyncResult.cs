using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>The aggregate result of a <c>SyncModels</c> run — one <see cref="StreamApplyResult"/> per stream.</summary>
public sealed class SyncResult
{
    /// <summary>Creates a result from the per-stream and per-package outcomes.</summary>
    public SyncResult(IReadOnlyList<StreamApplyResult> applied, IReadOnlyList<PackageApplyResult>? packages = null)
    {
        Applied = applied ?? throw new ArgumentNullException(nameof(applied));
        Packages = packages ?? Array.Empty<PackageApplyResult>();
    }

    /// <summary>Per-stream outcomes, in apply order.</summary>
    public IReadOnlyList<StreamApplyResult> Applied { get; }

    /// <summary>Per-package outcomes (create + stream assignments), in apply order.</summary>
    public IReadOnlyList<PackageApplyResult> Packages { get; }

    /// <summary>Total number of streams processed.</summary>
    public int Total => Applied.Count;

    /// <summary>Number of streams that applied successfully.</summary>
    public int Succeeded => Applied.Count(r => r.Succeeded);

    /// <summary>Number of streams that failed.</summary>
    public int Failed => Total - Succeeded;

    /// <summary>Whether every stream and every package succeeded.</summary>
    public bool AllSucceeded => Failed == 0 && Packages.All(p => p.Succeeded);

    /// <summary>Throws a <see cref="QueueySyncException"/> aggregating every failure, if anything failed.</summary>
    public void ThrowIfAnyFailed()
    {
        if (AllSucceeded)
            return;

        var failedStreams = Applied.Where(r => !r.Succeeded).Select(r => r.Name).ToArray();
        var failedPackages = Packages.Where(p => !p.Succeeded).Select(p => p.Name).ToArray();

        var parts = new List<string>();
        if (failedStreams.Length > 0) parts.Add($"{failedStreams.Length} stream(s): {string.Join(", ", failedStreams)}");
        if (failedPackages.Length > 0) parts.Add($"{failedPackages.Length} package(s): {string.Join(", ", failedPackages)}");

        throw new QueueySyncException($"Queuey sync had failures — {string.Join("; ", parts)}.", Applied);
    }
}
