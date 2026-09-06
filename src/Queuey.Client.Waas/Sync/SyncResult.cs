using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// The aggregate result of a <c>SyncStreams</c> run — one <see cref="StreamApplyResult"/> per stream
/// the run attempted, plus the names it never reached.
/// </summary>
/// <remarks>
/// A sync is not a transaction: each stream is its own <c>PUT</c>, so a failure midway leaves the
/// earlier ones applied. What this type guarantees instead is that such a run is never silent —
/// <see cref="NotAttempted"/> names exactly what did not happen, and <see cref="AllSucceeded"/> is
/// false whenever anything failed <i>or</i> was skipped.
/// </remarks>
public sealed class SyncResult : ISyncRunResult
{
    /// <summary>Creates a result from the per-stream and per-package outcomes.</summary>
    /// <param name="applied">The streams the run attempted, in apply order.</param>
    /// <param name="packages">The package upserts and stream assignments, in apply order.</param>
    /// <param name="notAttempted">
    /// Streams selected by the run but never sent, because an earlier failure stopped it.
    /// </param>
    public SyncResult(
        IReadOnlyList<StreamApplyResult> applied,
        IReadOnlyList<PackageApplyResult>? packages = null,
        IReadOnlyList<string>? notAttempted = null)
    {
        Applied = applied ?? throw new ArgumentNullException(nameof(applied));
        Packages = packages ?? Array.Empty<PackageApplyResult>();
        NotAttempted = notAttempted ?? Array.Empty<string>();
    }

    /// <summary>Per-stream outcomes for the streams the run attempted, in apply order.</summary>
    public IReadOnlyList<StreamApplyResult> Applied { get; }

    /// <summary>Per-package outcomes (create + stream assignments), in apply order.</summary>
    public IReadOnlyList<PackageApplyResult> Packages { get; }

    /// <summary>
    /// Streams the run selected but never sent, because it stopped at an earlier failure. Empty on a
    /// clean run and on a <see cref="SyncOptions.ContinueOnError"/> run.
    /// </summary>
    public IReadOnlyList<string> NotAttempted { get; }

    /// <summary>Total number of streams the run selected — attempted plus not attempted.</summary>
    public int Total => Applied.Count + NotAttempted.Count;

    /// <summary>Number of streams that applied successfully.</summary>
    public int Succeeded => Applied.Count(r => r.Succeeded);

    /// <summary>Number of streams that were attempted and failed.</summary>
    public int Failed => Applied.Count - Succeeded;

    /// <summary>Whether every selected stream and every package succeeded, with nothing left unattempted.</summary>
    public bool AllSucceeded => Failed == 0 && NotAttempted.Count == 0 && Packages.All(p => p.Succeeded);

    /// <inheritdoc />
    /// <remarks>Stream runs raise no readiness warnings today — a stream's queue is created by the apply itself.</remarks>
    public IReadOnlyList<string> Warnings => Array.Empty<string>();

    /// <summary>Throws a <see cref="QueueySyncException"/> aggregating every failure, if anything failed.</summary>
    public void ThrowIfAnyFailed()
    {
        if (AllSucceeded)
            return;

        var failedStreams = Applied.Where(r => !r.Succeeded).Select(r => r.Name).ToArray();
        var failedPackages = Packages.Where(p => !p.Succeeded).Select(p => p.Name).ToArray();

        var parts = new List<string>();
        if (failedStreams.Length > 0) parts.Add($"{failedStreams.Length} stream(s) failed: {string.Join(", ", failedStreams)}");
        if (failedPackages.Length > 0) parts.Add($"{failedPackages.Length} package(s) failed: {string.Join(", ", failedPackages)}");
        if (NotAttempted.Count > 0) parts.Add($"{NotAttempted.Count} stream(s) not attempted: {string.Join(", ", NotAttempted)}");

        throw new QueueySyncException(
            $"Queuey sync did not fully converge — {string.Join("; ", parts)}. " +
            $"{Succeeded} of {Total} stream(s) applied; re-run the sync once the cause is fixed (applying is idempotent).",
            this);
    }
}
