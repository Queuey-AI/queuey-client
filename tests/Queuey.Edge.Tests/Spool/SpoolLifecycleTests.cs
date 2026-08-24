using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Spool;

/// <summary>
/// The event state machine over the real store: lane-FIFO claims, lease
/// expiry as restart recovery, quarantine step-aside, and a deletion
/// surface of exactly two doors (settle-and-sweep, explicit discard).
/// </summary>
public class SpoolLifecycleTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Claims_are_fifo_and_at_most_one_per_lane()
    {
        using var fx = new SpoolFixture();
        var first = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "laneA"), CancellationToken.None);
        await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "laneA"), CancellationToken.None);
        var other = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "laneB"), CancellationToken.None);

        var claims = await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);

        // laneA yields only its HEAD (the second laneA row must wait behind
        // it — parallel sends within a lane could reorder arrival at Cloud);
        // laneB yields independently.
        Assert.Equal(2, claims.Count);
        Assert.Contains(claims, c => c.SpoolId == first.SpoolId);
        Assert.Contains(claims, c => c.SpoolId == other.SpoolId);

        // While the heads are in flight, their lanes are busy.
        Assert.Empty(await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None));
    }

    [Fact]
    public async Task A_scheduled_head_blocks_its_lane_that_is_what_fifo_means()
    {
        using var fx = new SpoolFixture();
        var head = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "lane"), CancellationToken.None);
        await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "lane"), CancellationToken.None);

        var claimed = await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.RescheduleAsync(head.SpoolId,
            Transient(), fx.Clock.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(1), CancellationToken.None);

        // The head is waiting out its backoff — the row behind it must NOT
        // overtake (Transient blocks the lane; only EventRejected steps aside).
        Assert.Single(claimed);
        Assert.Empty(await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None));

        fx.Clock.Advance(TimeSpan.FromMinutes(6));
        var after = await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        Assert.Equal(head.SpoolId, Assert.Single(after).SpoolId);
    }

    [Fact]
    public async Task Quarantined_head_steps_aside_and_the_lane_continues()
    {
        using var fx = new SpoolFixture();
        var poisoned = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "lane"), CancellationToken.None);
        var next = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "lane"), CancellationToken.None);

        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.QuarantineAsync(poisoned.SpoolId, Rejected(), CancellationToken.None);

        // Cloud never accepted the quarantined event, so no accepted-event
        // ordering is violated by letting the lane continue — and one
        // malformed payload must not freeze a unit's stream for days.
        var claims = await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        Assert.Equal(next.SpoolId, Assert.Single(claims).SpoolId);

        var stats = await fx.Spool.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.QuarantinedCount);
    }

    [Fact]
    public async Task Expired_leases_return_to_ready_on_sweep_which_is_restart_recovery()
    {
        using var fx = new SpoolFixture();
        var accept = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, TimeSpan.FromSeconds(30), CancellationToken.None);

        // The holder crashes; nothing settles the claim. Time passes.
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await fx.Spool.SweepAsync(CancellationToken.None);

        var reclaimed = await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        Assert.Equal(accept.SpoolId, Assert.Single(reclaimed).SpoolId);
    }

    [Fact]
    public async Task Settled_rows_are_swept_only_after_retention()
    {
        using var fx = new SpoolFixture();
        var accept = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.SettleAsync(accept.SpoolId, new CloudAck("evt_1", false, fx.Clock.UtcNow), CancellationToken.None);

        await fx.Spool.SweepAsync(CancellationToken.None);
        Assert.Equal(0, (await fx.Spool.GetStatsAsync(CancellationToken.None)).PendingCount);

        fx.Clock.Advance(fx.Options.SettledRetention + TimeSpan.FromMinutes(1));
        var touched = await fx.Spool.SweepAsync(CancellationToken.None);
        Assert.True(touched >= 1, "the settled row should be deleted after retention");
    }

    [Fact]
    public async Task Sweep_never_deletes_pending_or_quarantined_rows_however_old()
    {
        using var fx = new SpoolFixture();
        await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        var poisoned = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "q"), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.QuarantineAsync(poisoned.SpoolId, Rejected(), CancellationToken.None);

        // A year passes. Age alone never deletes (rev 4 F4).
        fx.Clock.Advance(TimeSpan.FromDays(365));
        await fx.Spool.SweepAsync(CancellationToken.None);

        var stats = await fx.Spool.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.PendingCount);
        Assert.Equal(1, stats.QuarantinedCount);
        Assert.True(stats.OldestPendingAge >= TimeSpan.FromDays(365));
    }

    [Fact]
    public async Task Quarantine_exits_only_via_explicit_retry_or_discard()
    {
        using var fx = new SpoolFixture();
        var poisoned = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.QuarantineAsync(poisoned.SpoolId, Rejected(), CancellationToken.None);

        // Operator retries after remediating: back into the drain.
        Assert.True(await fx.Spool.RetryQuarantinedAsync(poisoned.SpoolId, CancellationToken.None));
        var claims = await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        Assert.Equal(poisoned.SpoolId, Assert.Single(claims).SpoolId);

        // Re-quarantined, then explicitly discarded — the operator's door.
        await fx.Spool.QuarantineAsync(poisoned.SpoolId, Rejected(), CancellationToken.None);
        Assert.True(await fx.Spool.DiscardQuarantinedAsync(poisoned.SpoolId, CancellationToken.None));
        Assert.Equal(0, (await fx.Spool.GetStatsAsync(CancellationToken.None)).QuarantinedCount);

        // Discard reaches ONLY quarantined rows — a pending event cannot be
        // deleted through any API.
        var pending = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        Assert.False(await fx.Spool.DiscardQuarantinedAsync(pending.SpoolId, CancellationToken.None));
    }

    [Fact]
    public async Task Reschedule_persists_the_outcome_for_diagnosis()
    {
        using var fx = new SpoolFixture();
        var accept = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);

        await fx.Spool.RescheduleAsync(accept.SpoolId,
            new TransferOutcome(TransferClass.RequiresAction, TransferReason.AuthenticationRejected,
                TransferEvidence.Create(401, "unauthorized", fx.Clock.UtcNow, 1)),
            fx.Clock.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(5), CancellationToken.None);

        fx.Clock.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = Assert.Single(await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None));
        Assert.Equal(1, reclaimed.Attempts);
        Assert.Equal(TimeSpan.FromMinutes(5), reclaimed.LastDelay);
    }

    private static TransferOutcome Transient() =>
        new(TransferClass.Transient, TransferReason.Timeout, null);

    private static TransferOutcome Rejected() =>
        new(TransferClass.EventRejected, TransferReason.PayloadTooLarge,
            TransferEvidence.Create(413, "payload too large", DateTimeOffset.UtcNow, 1));
}
