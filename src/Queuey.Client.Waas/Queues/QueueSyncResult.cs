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
    /// <remarks>A queue whose change waits for a configuration plan (<see cref="QueueApplyResult.NeedsPlan"/>) is not counted.</remarks>
    public int Failed => Applied.Count(r => !r.Succeeded && !r.NeedsPlan);

    /// <summary>
    /// The queues a sync from code left a change out on, because Queuey wants a configuration plan for it
    /// (<c>plan_required</c>, Queuey F3.11). The sync went on with the others and does not throw for them.
    /// </summary>
    public IReadOnlyList<QueueApplyResult> PlanRequired => Applied.Where(r => r.NeedsPlan).ToArray();

    /// <inheritdoc />
    public bool AllSucceeded => Failed == 0 && NotAttempted.Count == 0 && !Applied.Any(r => r.NeedsPlan);

    /// <summary>Queues this run created, as opposed to found already there.</summary>
    public int Created => Applied.Count(r => r.Created);

    /// <summary>
    /// What the run saw about the workspace rather than one queue, such as an ingress every inheriting queue shares that
    /// waits for its credential. Also in <see cref="Warnings"/>, first.
    /// </summary>
    public IReadOnlyList<string> WorkspaceWarnings { get; init; } = Array.Empty<string>();

    /// <inheritdoc />
    /// <remarks>
    /// With <see cref="ServerWarnings"/> last (BØR 3 fra reviewen av #64), so a caller that logs the warnings also logs Queuey's,
    /// such as <c>would_require_approval</c>.
    /// </remarks>
    public IReadOnlyList<string> Warnings
        => WorkspaceWarnings.Concat(Applied.SelectMany(r => r.Warnings)).Concat(ServerWarnings).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The queues, and the workspace, a deployment apply left alone because a person detached them from deployment
    /// management (Queuey F2.4), with who, when and why. Not a failure: <c>--adopt</c> takes them back.
    /// </summary>
    public IReadOnlyList<SkippedResource> Skipped { get; init; } = Array.Empty<SkippedResource>();

    /// <summary>
    /// What Queuey does with a change to a managed resource from outside an apply, as it said when the apply started:
    /// <c>Off</c>, <c>Warn</c> or <c>Enforce</c>. Null when Queuey predates managed resources, or the run was not a deployment apply.
    /// </summary>
    public string? Enforcement { get; init; }

    /// <summary>
    /// Whether Queuey started an apply for this deployment apply (Queuey F2.4): true when it did, so what the apply wrote is
    /// marked as managed by the file; false when it did not (a Queuey that predates managed resources), and nothing is
    /// marked. What a person detached is skipped either way. Null when the run was not a deployment apply, or a dry run.
    /// </summary>
    public bool? ApplyStarted { get; init; }

    /// <summary>
    /// What Queuey warned about during the run, one line per warning as <c>code: message</c> (<c>X-Queuey-Warning</c>), such
    /// as <c>would_require_approval</c>: an apply without a configuration plan that Queuey will refuse once it enforces plans.
    /// </summary>
    public IReadOnlyList<string> ServerWarnings { get; init; } = Array.Empty<string>();

    /// <summary>The stored plan (<c>plan_…</c>) a deployment apply wrote, when it was bound to one (Queuey F3.11).</summary>
    public string? PlanId { get; init; }

    /// <summary>
    /// Throws a <see cref="QueueySyncException"/> aggregating every failure, if anything failed. A queue that only waits for a
    /// configuration plan (<see cref="PlanRequired"/>) is not a failure here, so an app that syncs on start still starts: it
    /// is in <see cref="Warnings"/>, and <see cref="AllSucceeded"/> is false.
    /// </summary>
    public void ThrowIfAnyFailed()
    {
        if (Failed == 0 && NotAttempted.Count == 0)
            return;

        var failed = Applied.Where(r => !r.Succeeded && !r.NeedsPlan).Select(r => r.Name).ToArray();

        var parts = new List<string>();
        if (failed.Length > 0) parts.Add(FormattableString.Invariant($"{failed.Length} queue(s) failed: {string.Join(", ", failed)}"));
        if (NotAttempted.Count > 0) parts.Add(FormattableString.Invariant($"{NotAttempted.Count} queue(s) not attempted: {string.Join(", ", NotAttempted)}"));

        throw new QueueySyncException(
            $"Queuey queue sync did not fully converge — {string.Join("; ", parts)}. " +
            $"{Succeeded} of {Total} queue(s) applied; re-run the sync once the cause is fixed (applying is idempotent).",
            this);
    }
}
