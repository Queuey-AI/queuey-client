using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// The aggregate result of a <c>SyncQueues</c> run. Same contract as the stream run: a sync is not a
/// transaction, so it is built never to look like one — <see cref="NotAttempted"/> names what did not
/// happen, and anything short of full convergence throws.
/// </summary>
public sealed class QueueSyncResult : ISyncRunResult
{
    /// <summary>Creates a result from the per-queue outcomes and the names never reached.</summary>
    public QueueSyncResult(IReadOnlyList<QueueApplyResult> applied, IReadOnlyList<string>? notAttempted = null)
    {
        Applied = applied ?? throw new ArgumentNullException(nameof(applied));
        NotAttempted = notAttempted ?? Array.Empty<string>();
    }

    /// <summary>Per-queue outcomes for the queues the run attempted, in apply order.</summary>
    public IReadOnlyList<QueueApplyResult> Applied { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> NotAttempted { get; }

    /// <inheritdoc />
    public int Total => Applied.Count + NotAttempted.Count;

    /// <inheritdoc />
    public int Succeeded => Applied.Count(r => r.Succeeded);

    /// <inheritdoc />
    public int Failed => Applied.Count - Succeeded;

    /// <inheritdoc />
    public bool AllSucceeded => Failed == 0 && NotAttempted.Count == 0;

    /// <summary>Queues this run created, as opposed to found already there.</summary>
    public int Created => Applied.Count(r => r.Created);

    /// <inheritdoc />
    public IReadOnlyList<string> Warnings => Applied.SelectMany(r => r.Warnings).ToArray();

    /// <summary>Throws a <see cref="QueueySyncException"/> aggregating every failure, if anything failed.</summary>
    public void ThrowIfAnyFailed()
    {
        if (AllSucceeded)
            return;

        var failed = Applied.Where(r => !r.Succeeded).Select(r => r.Name).ToArray();

        var parts = new List<string>();
        if (failed.Length > 0) parts.Add($"{failed.Length} queue(s) failed: {string.Join(", ", failed)}");
        if (NotAttempted.Count > 0) parts.Add($"{NotAttempted.Count} queue(s) not attempted: {string.Join(", ", NotAttempted)}");

        throw new QueueySyncException(
            $"Queuey queue sync did not fully converge — {string.Join("; ", parts)}. " +
            $"{Succeeded} of {Total} queue(s) applied; re-run the sync once the cause is fixed (applying is idempotent).",
            this);
    }
}
