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
    public QueueySyncException(string message, ISyncRunResult run)
        : base(message, statusCode: null, errorCode: null, innerException: FirstError(run))
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
    }

    /// <summary>The run, whatever it applied. Cast to <see cref="SyncResult"/> or <see cref="QueueSyncResult"/> for detail.</summary>
    public ISyncRunResult Run { get; }

    /// <summary>The stream run, when this came from <c>SyncStreams</c>; otherwise <c>null</c>.</summary>
    public SyncResult? Streams => Run as SyncResult;

    /// <summary>The queue run, when this came from <c>SyncQueues</c>; otherwise <c>null</c>.</summary>
    public QueueSyncResult? Queues => Run as QueueSyncResult;

    /// <summary>The per-stream outcomes, when this came from a stream run; otherwise empty.</summary>
    public IReadOnlyList<StreamApplyResult> Results => Streams?.Applied ?? Array.Empty<StreamApplyResult>();

    private static Exception? FirstError(ISyncRunResult? run) => run switch
    {
        SyncResult s => s.Applied.FirstOrDefault(r => !r.Succeeded && r.Error != null)?.Error,
        QueueSyncResult q => q.Applied.FirstOrDefault(r => !r.Succeeded && r.Error != null)?.Error,
        _ => null,
    };
}
