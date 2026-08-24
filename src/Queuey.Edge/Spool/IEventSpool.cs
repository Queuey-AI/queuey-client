using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Edge;

/// <summary>
/// The durable event spool — Edge's custody store. The delivery engine
/// never knows the backing store; the SQLite implementation is the boring
/// default, and "boring" is a feature.
///
/// Event lifecycle (no <c>Delivered</c> state exists here, ever — delivery
/// to the destination is Cloud's lifecycle, and Edge's terminates at
/// Transferred):
/// <code>
///  Accepted → Claimed → (send) → Transferred → swept after SettledRetention
///     ▲          │               RetryScheduled → Claimed
///     └── lease expiry           Quarantined → explicit retry | discard
/// </code>
/// </summary>
public interface IEventSpool
{
    /// <summary>
    /// Null while storage is healthy; the fault description once the spool
    /// has faulted (corruption detected, file preserved, operator recovery
    /// required). Part of the health contract — the snapshot derives
    /// <see cref="EdgeState.StorageFaulted"/> from it.
    /// </summary>
    string? FaultReason { get; }

    /// <summary>
    /// Durably persists one envelope at the configured
    /// <see cref="SpoolDurability"/> level. Returns only after the commit —
    /// this call IS the <c>PublishAsync</c> success boundary. Throws
    /// <see cref="QueueySpoolFullException"/> /
    /// <see cref="QueueyStorageFaultedException"/> per their contracts.
    /// </summary>
    Task<SpoolAccept> EnqueueAsync(EventEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the ready HEAD of up to <paramref name="maxLanes"/> lanes,
    /// each under a lease. Lane discipline is the ordering contract:
    /// oldest-first within a lane, at most one claim per lane at a time
    /// (parallel sends within a lane could reorder arrival at Cloud, which
    /// orders by receive time). Quarantined rows are never ready and never
    /// block the rows behind them (step-aside). A crashed holder's claims
    /// return on lease expiry — that is the whole of restart recovery.
    /// </summary>
    Task<IReadOnlyList<ClaimedEvent>> ClaimReadyAsync(int maxLanes, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Records Cloud custody (fresh or replayed — both are success) and
    /// releases Edge's ownership. The row becomes Transferred and is swept
    /// after <c>SettledRetention</c>; the ack's cloud event id is kept on
    /// the row until then purely for correlation/support.
    /// </summary>
    Task SettleAsync(long spoolId, CloudAck ack, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a claimed row to the retry schedule after a non-permanent
    /// outcome. The outcome (class + reason + evidence) is persisted on the
    /// row; <paramref name="nextAttemptUtc"/>/<paramref name="attemptDelay"/>
    /// come from the loop's single backoff policy — the spool stores
    /// schedule, it never invents one.
    /// </summary>
    Task RescheduleAsync(long spoolId, TransferOutcome outcome, DateTimeOffset nextAttemptUtc, TimeSpan attemptDelay, CancellationToken cancellationToken);

    /// <summary>
    /// Parks a claimed row outside the automatic retry path
    /// (<see cref="TransferClass.EventRejected"/>: retrying the same
    /// unchanged bytes cannot succeed). Quarantined rows are durable, never
    /// expire, never auto-transfer, and exit only via explicit operator
    /// action (retry/discard).
    /// </summary>
    Task QuarantineAsync(long spoolId, TransferOutcome outcome, CancellationToken cancellationToken);

    /// <summary>Point-in-time counters for health derivation. Cheap; called often.</summary>
    Task<SpoolStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Housekeeping: expire stale leases back to ready, delete Transferred
    /// rows past <c>SettledRetention</c>. Returns rows touched. The ONLY
    /// deletion here is settled cleanup — accepted-but-untransferred events
    /// are never deleted by any sweep (rev 4 F4).
    /// </summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The operator's "I fixed the cause — try NOW": makes every pending
    /// (Accepted) row due immediately and resets its backoff ladder.
    /// Ordering is untouched (rows keep their ids, lanes keep FIFO) — this
    /// only collapses WAITING, so it can never cause a duplicate or an
    /// overtake. Returns rows kicked. Quarantined rows are deliberately NOT
    /// included — they exit only via explicit retry/discard.
    /// </summary>
    Task<int> KickAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns a quarantined row to the retry schedule (operator action),
    /// or removes it (explicit discard — logged and counted by the caller).
    /// </summary>
    Task<bool> RetryQuarantinedAsync(long spoolId, CancellationToken cancellationToken);

    /// <inheritdoc cref="RetryQuarantinedAsync"/>
    Task<bool> DiscardQuarantinedAsync(long spoolId, CancellationToken cancellationToken);
}

/// <summary>The durable-accept receipt the publisher builds its <see cref="PublishReceipt"/> from.</summary>
public sealed record SpoolAccept(long SpoolId, string TransferId, DateTimeOffset AcceptedAtUtc);

/// <summary>A leased row handed to the transfer loop.</summary>
public sealed record ClaimedEvent(long SpoolId, EventEnvelope Envelope, int Attempts, TimeSpan LastDelay);

/// <summary>
/// Counters the health surface derives state from. NextAttemptUtc is the
/// earliest scheduled attempt among LANE HEADS — a row queued behind a
/// backing-off head does not count, so "due now" never lies about a lane
/// that is in fact waiting. Null when nothing is waiting (empty, or every
/// eligible head is in flight right now).
/// </summary>
public sealed record SpoolStats(
    long PendingCount,
    long QuarantinedCount,
    TimeSpan? OldestPendingAge,
    long StorageUsageBytes,
    DateTimeOffset? LastSettledAtUtc,
    DateTimeOffset? NextAttemptUtc = null);
