using System;

namespace Queuey.Client.Waas;

/// <summary>Options controlling a <c>SyncModels</c> run.</summary>
public sealed class SyncOptions
{
    /// <summary>Compute the plan and return per-stream results without calling the API. No credentials required.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// When <c>true</c>, stop at the first failing stream and rethrow. Default <c>false</c>: apply every
    /// stream, collect failures, and surface them together via <see cref="SyncResult.ThrowIfAnyFailed"/>.
    /// </summary>
    public bool StopOnFirstError { get; set; }

    /// <summary>Optional predicate to sync only a subset of streams (e.g. the CLI's <c>--only</c>).</summary>
    public Func<StreamDefinition, bool>? Filter { get; set; }
}
