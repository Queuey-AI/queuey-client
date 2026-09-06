using System;

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
}
