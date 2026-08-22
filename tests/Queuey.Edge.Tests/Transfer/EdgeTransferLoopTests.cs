using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Transfer;

/// <summary>
/// The loop against the REAL spool with a scripted channel — the chaos
/// matrix from plan rev 4 §18 at unit scale: drain in order, resolve a lost
/// ACK to one logical event, retain through RequiresAction and recover
/// after the fix, quarantine without stalling the lane.
/// </summary>
public class EdgeTransferLoopTests
{
    [Fact]
    public async Task Backlog_drains_in_publish_order()
    {
        using var fx = new LoopFixture(_ => Accept());
        for (var i = 0; i < 5; i++)
            await fx.Publish($"evt-{i}");

        await fx.RunUntilAsync(async () =>
            (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount == 0);

        Assert.Equal(["evt-0", "evt-1", "evt-2", "evt-3", "evt-4"], fx.Channel.Sent.ToArray());
    }

    [Fact]
    public async Task Lost_ack_resolves_to_exactly_one_logical_event()
    {
        using var fx = new LoopFixture(seen => seen == 1
            ? Fail(TransferClass.Transient, TransferReason.Timeout)   // the ACK "never arrived"
            : Accept(replayed: true));                                 // Cloud already held custody
        await fx.Publish("evt-lost-ack");

        await fx.RunUntilAsync(async () =>
            (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount == 0);

        // Same transfer identity on both attempts; settled once, as a replay.
        Assert.Equal(2, fx.Channel.Sent.Count);
        Assert.Equal(fx.Channel.Sent[0], fx.Channel.Sent[1]);
    }

    [Fact]
    public async Task Requires_action_retains_and_recovers_after_the_fix()
    {
        var fixedAt = new TaskCompletionSource();
        using var fx = new LoopFixture(
            _ => fixedAt.Task.IsCompleted
                ? Accept()
                : Fail(TransferClass.RequiresAction, TransferReason.AuthenticationRejected, 401),
            configure: o => o.Transfer.RequiresActionProbeInitial = TimeSpan.FromMilliseconds(50));
        await fx.Publish("evt-held");

        // While credentials are broken the event is RETAINED — never dropped.
        await fx.RunUntilAsync(() => Task.FromResult(fx.Channel.Sent.Count >= 1));
        Assert.Equal(1, (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount);
        Assert.True(fx.State.Degraded);

        // The operator rotates the key; Edge resumes on its own — no app code.
        fixedAt.SetResult();
        await fx.RunUntilAsync(async () =>
            (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount == 0);
        Assert.False(fx.State.Degraded);
    }

    [Fact]
    public async Task Rejected_event_quarantines_and_its_lane_continues()
    {
        using var fx = new LoopFixture(seen => seen == 1
            ? Fail(TransferClass.EventRejected, TransferReason.PayloadTooLarge, 413)
            : Accept());
        await fx.Publish("evt-poison", groupKey: "lane");
        await fx.Publish("evt-next", groupKey: "lane");

        await fx.RunUntilAsync(async () =>
        {
            var stats = await fx.Spool.Spool.GetStatsAsync(CancellationToken.None);
            return stats.PendingCount == 0 && stats.QuarantinedCount == 1;
        });

        // One malformed payload must not freeze the unit's stream.
        Assert.Equal(["evt-poison", "evt-next"], fx.Channel.Sent.ToArray());
    }

    [Fact]
    public async Task After_a_failure_the_loop_probes_with_a_single_event()
    {
        // Round one: EVERY lane fails (a network cut). From then on: success.
        // Deterministic — whatever the in-batch ordering, all four first
        // sends fail, so the flag is set before any retry can clear it.
        using var fx = new LoopFixture(seen => seen <= 4
            ? Fail(TransferClass.Transient, TransferReason.ConnectionRefused)
            : Accept());
        for (var i = 0; i < 4; i++)
            await fx.Publish($"evt-{i}", groupKey: $"lane-{i}"); // 4 independent lanes

        await fx.RunUntilAsync(async () =>
            (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount == 0);

        Assert.True(fx.Channel.SawDegradedProbe,
            "after a failure the loop must claim a single probe lane, not reopen the full throttle");
    }

    private static TransferAttempt Accept(bool replayed = false) => new(
        new TransferOutcome(TransferClass.Accepted,
            replayed ? TransferReason.Replayed : TransferReason.Accepted, null),
        new CloudAck(replayed ? "evt_original" : "evt_fresh", replayed, DateTimeOffset.UtcNow));

    private static TransferAttempt Fail(TransferClass cls, TransferReason reason, int? status = null) => new(
        new TransferOutcome(cls, reason,
            TransferEvidence.Create(status, reason.ToString(), DateTimeOffset.UtcNow, 1)), null);

    // ── fixture ─────────────────────────────────────────────────────────

    private sealed class LoopFixture : IDisposable
    {
        public SpoolFixture Spool { get; }
        public ScriptedChannel Channel { get; }
        public EdgeRuntimeState State { get; } = new();
        private readonly IQueueyPublisher _publisher;
        private readonly EdgeTransferLoop _loop;
        private readonly QueueyEdgeOptions _options;

        public LoopFixture(
            Func<int, TransferAttempt> script,
            Action<QueueyEdgeOptions>? configure = null)
        {
            Spool = new SpoolFixture();
            // Loop tests run on the REAL clock with tight timings — the
            // FakeClock would freeze next_attempt due-ness.
            var clock = SystemEdgeClock.Instance;
            var spool = new SqliteEventSpool(Spool.Options, clock);

            _options = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_test" };
            _options.Storage.Path = Spool.Options.Path;
            _options.Transfer.BackoffBase = TimeSpan.FromMilliseconds(10);
            _options.Transfer.BackoffCap = TimeSpan.FromMilliseconds(50);
            _options.Transfer.RequiresActionProbeInitial = TimeSpan.FromMilliseconds(50);
            _options.Transfer.IdlePollInterval = TimeSpan.FromMilliseconds(20);
            configure?.Invoke(_options);

            var wake = new EdgeWake();
            Channel = new ScriptedChannel(script, State);
            _publisher = new QueueyEdgePublisher(_options, spool, clock, wake);
            _loop = new EdgeTransferLoop(
                spool, Channel, new BackoffPolicy(_options.Transfer), State, wake,
                _options, clock, NullLogger<EdgeTransferLoop>.Instance);
        }

        public Task Publish(string idempotencyKey, string? groupKey = null)
            => _publisher.PublishAsync("orders", new { key = idempotencyKey },
                new Client.PublishOptions { IdempotencyKey = idempotencyKey, GroupKey = groupKey });

        public async Task RunUntilAsync(Func<Task<bool>> condition)
        {
            await _loop.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    if (await condition()) return;
                    await Task.Delay(25);
                }
                Assert.Fail("condition not reached within 15s");
            }
            finally
            {
                await _loop.StopAsync(CancellationToken.None);
            }
        }

        public void Dispose() => Spool.Dispose();
    }

    private sealed class ScriptedChannel : ITransferChannel
    {
        private readonly Func<int, TransferAttempt> _script;
        private readonly EdgeRuntimeState _state;
        private int _seen;

        public List<string> Sent { get; } = new();
        public bool SawDegradedProbe { get; private set; }
        private readonly object _lock = new();

        public ScriptedChannel(Func<int, TransferAttempt> script, EdgeRuntimeState state)
        {
            _script = script;
            _state = state;
        }

        public Task<TransferAttempt> SendAsync(EventEnvelope envelope, int attemptNumber, CancellationToken ct)
        {
            int seen;
            lock (_lock)
            {
                seen = ++_seen;
                Sent.Add(envelope.TransferId);
                if (_state.Degraded) SawDegradedProbe = true;
            }
            return Task.FromResult(_script(seen));
        }
    }
}
