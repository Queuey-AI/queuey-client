using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// Thrown by <see cref="SyncResult.ThrowIfAnyFailed"/> when one or more streams failed to sync. Carries the
/// full per-stream results so callers can inspect which streams failed and why.
/// </summary>
public sealed class QueueySyncException : QueueyException
{
    /// <summary>Creates the exception from an aggregate message and the per-stream results.</summary>
    public QueueySyncException(string message, IReadOnlyList<StreamApplyResult> results)
        : base(message, statusCode: null, errorCode: null, innerException: FirstError(results))
        => Results = results ?? Array.Empty<StreamApplyResult>();

    /// <summary>The per-stream outcomes of the sync run (both succeeded and failed).</summary>
    public IReadOnlyList<StreamApplyResult> Results { get; }

    private static Exception? FirstError(IReadOnlyList<StreamApplyResult>? results)
        => results?.FirstOrDefault(r => !r.Succeeded && r.Error != null)?.Error;
}
