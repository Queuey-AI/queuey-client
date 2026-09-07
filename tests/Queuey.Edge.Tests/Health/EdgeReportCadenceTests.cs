using System;
using Xunit;

namespace Queuey.Edge.Tests.Health;

/// <summary>
/// The reporter's timing, pinned without a clock: startup, state change and
/// interval send; a failed state-change report backs off with doubling
/// instead of hammering every tick; Cloud's Retry-After is the floor; a
/// success resets everything.
/// </summary>
public sealed class EdgeReportCadenceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sends_at_startup_on_state_change_and_at_the_interval()
    {
        var c = new EdgeReportCadence(Interval, Tick);

        Assert.True(c.IsDue(EdgeState.Healthy, T0));
        c.Attempted(EdgeState.Healthy, T0, sent: true, retryAfter: null);

        Assert.False(c.IsDue(EdgeState.Healthy, T0 + Tick));
        Assert.True(c.IsDue(EdgeState.Backlogged, T0 + Tick), "a state change is due at the next tick");
        c.Attempted(EdgeState.Backlogged, T0 + Tick, sent: true, retryAfter: null);

        Assert.False(c.IsDue(EdgeState.Backlogged, T0 + Tick + Interval - Tick));
        Assert.True(c.IsDue(EdgeState.Backlogged, T0 + Tick + Interval), "the interval report is due");
    }

    [Fact]
    public void A_failed_state_change_report_backs_off_with_doubling_up_to_the_interval()
    {
        var c = new EdgeReportCadence(Interval, Tick);
        c.Attempted(EdgeState.Healthy, T0, sent: true, retryAfter: null);

        // Backlogged, and Cloud refuses. Old behaviour: retry every tick.
        var t = T0 + Tick;
        c.Attempted(EdgeState.Backlogged, t, sent: false, retryAfter: null);
        Assert.False(c.IsDue(EdgeState.Backlogged, t + Tick - TimeSpan.FromSeconds(1)));
        Assert.True(c.IsDue(EdgeState.Backlogged, t + Tick), "first retry after one tick");

        t += Tick;
        c.Attempted(EdgeState.Backlogged, t, sent: false, retryAfter: null);
        Assert.False(c.IsDue(EdgeState.Backlogged, t + 2 * Tick - TimeSpan.FromSeconds(1)));
        Assert.True(c.IsDue(EdgeState.Backlogged, t + 2 * Tick), "second retry after two ticks");

        t += 2 * Tick;
        c.Attempted(EdgeState.Backlogged, t, sent: false, retryAfter: null);
        Assert.False(c.IsDue(EdgeState.Backlogged, t + 4 * Tick - TimeSpan.FromSeconds(1)));
        Assert.True(c.IsDue(EdgeState.Backlogged, t + 4 * Tick), "then four");

        // Keep failing: the hold never exceeds the interval.
        for (var i = 0; i < 10; i++)
        {
            t += Interval;
            c.Attempted(EdgeState.Backlogged, t, sent: false, retryAfter: null);
        }
        Assert.False(c.IsDue(EdgeState.Backlogged, t + Interval - Tick));
        Assert.True(c.IsDue(EdgeState.Backlogged, t + Interval), "capped at the interval");
    }

    [Fact]
    public void Retry_after_from_cloud_is_the_floor_and_a_success_resets_the_backoff()
    {
        var c = new EdgeReportCadence(Interval, Tick);
        c.Attempted(EdgeState.Healthy, T0, sent: true, retryAfter: null);

        var t = T0 + Tick;
        c.Attempted(EdgeState.Backlogged, t, sent: false, retryAfter: TimeSpan.FromSeconds(45));
        Assert.False(c.IsDue(EdgeState.Backlogged, t + TimeSpan.FromSeconds(44)));
        Assert.True(c.IsDue(EdgeState.Backlogged, t + TimeSpan.FromSeconds(45)), "Retry-After wins over the 10 s backoff");

        t += TimeSpan.FromSeconds(45);
        c.Attempted(EdgeState.Backlogged, t, sent: true, retryAfter: null);
        Assert.False(c.IsDue(EdgeState.Backlogged, t + Tick), "reported; nothing due");

        // A later failure starts the backoff from one tick again.
        t += Tick;
        c.Attempted(EdgeState.Healthy, t, sent: false, retryAfter: null);
        Assert.True(c.IsDue(EdgeState.Healthy, t + Tick));
    }

    [Fact]
    public void A_new_state_change_during_a_hold_waits_for_the_hold()
    {
        var c = new EdgeReportCadence(Interval, Tick);
        c.Attempted(EdgeState.Healthy, T0, sent: true, retryAfter: null);

        var t = T0 + Tick;
        c.Attempted(EdgeState.Backlogged, t, sent: false, retryAfter: TimeSpan.FromSeconds(45));

        // The spool faults while Cloud asked us to wait: still wait. A fresh
        // state that bypassed the hold would let a flapping node bypass it
        // every tick.
        Assert.False(c.IsDue(EdgeState.StorageFaulted, t + TimeSpan.FromSeconds(30)));
        Assert.True(c.IsDue(EdgeState.StorageFaulted, t + TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void An_interval_under_the_tick_still_backs_off_by_at_least_a_tick()
    {
        var c = new EdgeReportCadence(TimeSpan.FromSeconds(1), Tick);
        c.Attempted(EdgeState.Healthy, T0, sent: false, retryAfter: null);
        Assert.False(c.IsDue(EdgeState.Healthy, T0 + TimeSpan.FromSeconds(9)));
        Assert.True(c.IsDue(EdgeState.Healthy, T0 + Tick));

        c.Attempted(EdgeState.Healthy, T0 + Tick, sent: false, retryAfter: null);
        Assert.False(c.IsDue(EdgeState.Healthy, T0 + Tick + TimeSpan.FromSeconds(9)), "the cap is the tick, never less");
        Assert.True(c.IsDue(EdgeState.Healthy, T0 + 2 * Tick));
    }

    [Fact]
    public void An_absurd_retry_after_is_capped_at_an_hour()
    {
        var c = new EdgeReportCadence(Interval, Tick);
        c.Attempted(EdgeState.Healthy, T0, sent: false, retryAfter: TimeSpan.FromDays(3));

        Assert.False(c.IsDue(EdgeState.Healthy, T0 + TimeSpan.FromMinutes(59)));
        Assert.True(c.IsDue(EdgeState.Healthy, T0 + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_hold_also_delays_the_regular_interval_report()
    {
        var c = new EdgeReportCadence(Interval, Tick);
        c.Attempted(EdgeState.Healthy, T0, sent: true, retryAfter: null);

        var t = T0 + Interval;
        c.Attempted(EdgeState.Healthy, t, sent: false, retryAfter: TimeSpan.FromMinutes(10));
        Assert.False(c.IsDue(EdgeState.Healthy, t + Interval), "Cloud said ten minutes; five is too soon");
        Assert.True(c.IsDue(EdgeState.Healthy, t + TimeSpan.FromMinutes(10)));
    }
}
