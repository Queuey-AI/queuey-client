using System;
using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>Options controlling a <c>SyncStreams</c> run.</summary>
public sealed class SyncOptions
{
    /// <summary>Compute the plan and return per-stream results without calling the API. No credentials required.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Keep applying after a stream fails, collecting every failure instead of stopping.
    /// <para>
    /// Default <c>false</c> — a sync stops at the first failure and throws, and the streams it never
    /// reached are reported as <see cref="SyncResult.NotAttempted"/>. Applying each remaining stream
    /// after one has already failed only widens the gap between what the code declares and what the
    /// workspace holds; a deploy that half-converged is harder to reason about than one that stopped.
    /// </para>
    /// <para>
    /// Set it to <c>true</c> when you deliberately want the full damage report in one run (the CLI's
    /// <c>--continue-on-error</c>). The run still throws at the end if anything failed.
    /// </para>
    /// </summary>
    public bool ContinueOnError { get; set; }

    /// <summary>Optional predicate to sync only a subset of streams (e.g. the CLI's <c>--only</c>).</summary>
    public Func<StreamDefinition, bool>? Filter { get; set; }

    /// <summary>Optional predicate to sync only a subset of queues. The queue twin of <see cref="Filter"/>.</summary>
    public Func<QueueDefinition, bool>? QueueFilter { get; set; }

    /// <summary>
    /// Where the deployment file lives, for <see cref="IQueueyService.ApplyDeploymentAsync"/>: Queuey marks every queue and
    /// workspace the apply writes as managed from it (Queuey F2.4). User info, query and fragment are stripped from the
    /// repository before it is sent. Null marks them without a source.
    /// </summary>
    public DeploymentFileSource? Source { get; set; }

    /// <summary>
    /// What a deployment apply takes back from a detach (<c>queuey apply --adopt</c>): <c>workspace</c>, and queue names. A
    /// detached queue or workspace not named here is skipped and reported in <see cref="QueueSyncResult.Skipped"/>.
    /// </summary>
    public IReadOnlyList<string>? Adopt { get; set; }

    /// <summary>
    /// The stored plan a deployment apply writes (Queuey F3.11), from <see cref="IQueueyPlans.StorePlanAsync"/> or
    /// <see cref="IQueueyPlans.GetStoredPlanAsync"/>: the apply starts with its id and hash, takes its source and what it
    /// adopts from it (so <see cref="Source"/> and <see cref="Adopt"/> are not sent), and each write in it is one of the plan's
    /// steps, sent once. Null writes the file as before.
    /// </summary>
    public StoredPlan? Plan { get; set; }
}
