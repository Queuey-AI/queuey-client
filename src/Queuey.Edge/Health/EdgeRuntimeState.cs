using System;
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

    public bool Degraded => _degraded;

    public DateTimeOffset? LastSuccessfulCloudContact
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastContactTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public TransferFailure? LastTransferFailure => _lastFailure;

    public void RecordSuccess(DateTimeOffset atUtc)
    {
        Interlocked.Exchange(ref _lastContactTicks, atUtc.UtcTicks);
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
        _degraded = true;
    }
}
