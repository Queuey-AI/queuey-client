using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// Thrown when a sync run did not fully converge. Carries the whole run — what applied, what failed,
/// and what was never attempted — so a caller can report the exact state the workspace was left in.
/// </summary>
public sealed class QueueySyncException : QueueyException
{
    /// <summary>Creates the exception from an aggregate message and the run's result.</summary>
    public QueueySyncException(string message, SyncResult result)
        : base(message, statusCode: null, errorCode: null, innerException: FirstError(result?.Applied))
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>The full run: applied streams, packages, and the streams never attempted.</summary>
    public SyncResult Result { get; }

    /// <summary>The per-stream outcomes of the sync run (both succeeded and failed).</summary>
    public IReadOnlyList<StreamApplyResult> Results => Result.Applied;

    private static Exception? FirstError(IReadOnlyList<StreamApplyResult>? results)
        => results?.FirstOrDefault(r => !r.Succeeded && r.Error != null)?.Error;
}
