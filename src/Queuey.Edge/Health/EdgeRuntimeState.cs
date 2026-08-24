using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Queuey.Edge;

/// <summary>
/// The loop's live signals — last Cloud contact, last failure, and the
/// degraded flag that drives probe-before-throttle. Everything else about
/// health is DERIVED from the spool on demand, so this holds only what a
/// restart may honestly forget (a fresh process probes anyway).
/// </summary>
internal sealed class EdgeRuntimeState
{
    private long _lastContactTicks;
    private volatile TransferFailure? _lastFailure;
    private volatile bool _degraded;
    private long _acceptedFresh;
    private long _acceptedReplayed;
    private readonly ConcurrentDictionary<(TransferClass Class, TransferReason Reason), long> _failures = new();

    public bool Degraded => _degraded;

    public long AcceptedFreshCount => Interlocked.Read(ref _acceptedFresh);

    public long AcceptedReplayedCount => Interlocked.Read(ref _acceptedReplayed);

    /// <summary>Failure totals shaped for the observable counter — one measurement per (class, reason).</summary>
    public IEnumerable<Measurement<long>> FailureMeasurements()
    {
        foreach (var pair in _failures)
        {
            yield return new Measurement<long>(pair.Value,
                new KeyValuePair<string, object?>("class", pair.Key.Class.ToString()),
                new KeyValuePair<string, object?>("reason", pair.Key.Reason.ToString()));
        }
    }

    public DateTimeOffset? LastSuccessfulCloudContact
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastContactTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public TransferFailure? LastTransferFailure => _lastFailure;

    public void RecordSuccess(DateTimeOffset atUtc, bool replayed)
    {
        Interlocked.Exchange(ref _lastContactTicks, atUtc.UtcTicks);
        if (replayed) Interlocked.Increment(ref _acceptedReplayed);
        else Interlocked.Increment(ref _acceptedFresh);
        _degraded = false;
    }

    public void RecordFailure(TransferOutcome outcome)
    {
        _lastFailure = new TransferFailure(
            outcome.Class,
            outcome.Reason,
            outcome.Evidence?.StatusCode,
            outcome.Evidence?.AtUtc ?? DateTimeOffset.UtcNow,
            outcome.Evidence?.Snippet);
        _failures.AddOrUpdate((outcome.Class, outcome.Reason), 1, static (_, count) => count + 1);
        _degraded = true;
    }
}
