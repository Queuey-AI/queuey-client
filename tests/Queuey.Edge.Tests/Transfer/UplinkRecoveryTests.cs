using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Transfer;

/// <summary>
/// When the uplink comes back, every lane resumes with the first successful transfer, not on its own retry
/// schedule. The loop runs against the real spool on a FAKE clock: no row comes due unless the test moves time, so
/// a lane that transfers while time stands still was resumed by the recovery and nothing else. Backoff draws a fixed
/// 0.5, so every delay is exact.
/// </summary>
public class UplinkRecoveryTests
{
    private static readonly TimeSpan FarAhead = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task When_a_probe_gets_through_after_the_uplink_was_down_every_waiting_lane_resumes_at_once()
    {
        await using var fx = new RecoveryFixture();
        await fx.SeedWaitingAsync(
            ("lane-1", Transient(), TimeSpan.FromMinutes(3)),
            ("lane-2", Transient(), FarAhead),
            ("lane-3", Transient(), TimeSpan.FromMinutes(5)));

        await fx.ProbeThroughAnOutageAsync();

        // Klokka står: lanene går fordi gjenopprettingen gjorde dem klare, ikke fordi planen deres ringte.
        await fx.UntilAsync(async () => (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount == 0);
        var resumed = fx.Channel.Sends.Where(s => s.TransferId.StartsWith("lane-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["lane-1", "lane-2", "lane-3"], resumed.Select(s => s.TransferId).Order().ToArray());
        Assert.All(resumed, s => Assert.False(s.WhileDegraded, "the resumed lanes go together, not one probe at a time"));
        Assert.Single(fx.Logger.Lines, l => l.Level == LogLevel.Information
                                            && l.Text == "Uplink recovered: 3 pending event(s) resumed at once.");
    }

    [Fact]
    public async Task What_Cloud_or_an_operator_must_clear_keeps_its_wait_when_the_uplink_recovers()
    {
        await using var fx = new RecoveryFixture();
        await fx.SeedWaitingAsync(
            ("transient", Transient(), FarAhead),
            ("throttled", new TransferOutcome(TransferClass.Throttled, TransferReason.RateLimited, null)
                { RetryAfter = TimeSpan.FromMinutes(2) }, TimeSpan.FromMinutes(2)),
            ("auth", new TransferOutcome(TransferClass.RequiresAction, TransferReason.AuthenticationRejected, null), TimeSpan.FromMinutes(5)),
            ("paused", new TransferOutcome(TransferClass.RequiresAction, TransferReason.QueuePaused, null), TimeSpan.FromMinutes(1)),
            ("skew", new TransferOutcome(TransferClass.RequiresAction, TransferReason.ClockSkew, null), TimeSpan.FromMinutes(5)));
        var seededAt = fx.Clock.UtcNow;

        await fx.ProbeThroughAnOutageAsync();
        await fx.UntilAsync(() => Task.FromResult(fx.IsSettled("transient")));

        // At forbindelsen er tilbake, løser ikke Retry-After, auth, pause eller klokke.
        Assert.Equal(seededAt + TimeSpan.FromMinutes(2), fx.Row("throttled").NextAttemptUtc);
        Assert.Equal(seededAt + TimeSpan.FromMinutes(5), fx.Row("auth").NextAttemptUtc);
        Assert.Equal(seededAt + TimeSpan.FromMinutes(1), fx.Row("paused").NextAttemptUtc);
        Assert.Equal(seededAt + TimeSpan.FromMinutes(5), fx.Row("skew").NextAttemptUtc);
        Assert.DoesNotContain(fx.Channel.Sends, s => s.TransferId is "throttled" or "auth" or "paused" or "skew");
        Assert.Single(fx.Logger.Lines, l => l.Text == "Uplink recovered: 1 pending event(s) resumed at once.");
    }

    [Fact]
    public async Task If_the_link_drops_again_after_the_resume_backoff_continues_from_the_wait_each_lane_had_earned()
    {
        // Nettet flapper: proben kommer gjennom, så faller det igjen før lanene er tømt.
        await using var fx = new RecoveryFixture(laneRoute: _ => Fail(TransferClass.Transient, TransferReason.ConnectionReset));
        await fx.SeedWaitingAsync(("lane-1", Transient(), FarAhead), ("lane-2", Transient(), FarAhead));

        await fx.ProbeThroughAnOutageAsync();
        await fx.UntilAsync(() => Task.FromResult(fx.Row("lane-1").Attempts == 2 && fx.Row("lane-2").Attempts == 2));

        // Fra 4 min opptjent: 1 s + 0,5 × (min(3 × 4 min, taket 5 min) − 1 s) = 150,5 s. Fra base hadde det vært 1 s.
        var expected = TimeSpan.FromSeconds(150.5);
        foreach (var lane in new[] { "lane-1", "lane-2" })
        {
            var row = fx.Row(lane);
            Assert.Equal(expected, row.LastDelay);
            Assert.Equal(fx.Clock.UtcNow + expected, row.NextAttemptUtc);
        }
    }

    [Fact]
    public async Task A_success_while_the_loop_is_not_degraded_resumes_nothing()
    {
        await using var fx = new RecoveryFixture(probeRoute: _ => Accept());
        await fx.SeedWaitingAsync(("lane-1", Transient(), FarAhead), ("lane-2", Transient(), FarAhead));
        var seededAt = fx.Clock.UtcNow;
        await fx.EnqueueAsync("probe");

        await fx.StartAsync();
        await fx.UntilAsync(() => Task.FromResult(fx.IsSettled("probe")));
        await Task.Delay(200); // flere runder med stående klokke

        Assert.Equal(["probe"], fx.Channel.Sends.Select(s => s.TransferId).ToArray());
        Assert.Equal(seededAt + FarAhead, fx.Row("lane-1").NextAttemptUtc);
        Assert.Equal(seededAt + FarAhead, fx.Row("lane-2").NextAttemptUtc);
        Assert.DoesNotContain(fx.Logger.Lines, l => l.Text.StartsWith("Uplink recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_lane_that_keeps_failing_is_not_pulled_forward_by_the_other_lanes_successes()
    {
        // Ikke et utfall: én kø svarer 500, de andre går. Ingen probe har funnet forbindelsen nede, så suksessene i de
        // andre lanene gjenopptar ingenting; ellers ville den dårlige lanen blitt prøvd annenhver runde forbi backoff-en.
        await using var fx = new RecoveryFixture(route: e => e.TransferId == "bad"
            ? Fail(TransferClass.Transient, TransferReason.CloudServerError, 500)
            : Accept());
        await fx.SeedWaitingAsync(("lane-1", Transient(), FarAhead));
        var seededAt = fx.Clock.UtcNow;
        await fx.EnqueueAsync("bad");
        await fx.EnqueueAsync("good-1", groupKey: "good");
        await fx.EnqueueAsync("good-2", groupKey: "good");

        await fx.StartAsync();
        await fx.UntilAsync(() => Task.FromResult(fx.IsSettled("good-2")));
        await Task.Delay(200); // flere runder med stående klokke

        Assert.Single(fx.Channel.Sends, s => s.TransferId == "bad");
        Assert.Equal(fx.Clock.UtcNow + TimeSpan.FromSeconds(1), fx.Row("bad").NextAttemptUtc);
        Assert.Equal(seededAt + FarAhead, fx.Row("lane-1").NextAttemptUtc);
        Assert.DoesNotContain(fx.Channel.Sends, s => s.TransferId == "lane-1");
    }

    private static TransferOutcome Transient() => new(TransferClass.Transient, TransferReason.Timeout, null);

    private static TransferAttempt Accept() => new(
        new TransferOutcome(TransferClass.Accepted, TransferReason.Accepted, null),
        new CloudAck("evt_fresh", false, DateTimeOffset.UtcNow));

    private static TransferAttempt Fail(TransferClass cls, TransferReason reason, int? status = null) => new(
        new TransferOutcome(cls, reason, TransferEvidence.Create(status, reason.ToString(), DateTimeOffset.UtcNow, 1)), null);

    // ── fixture ─────────────────────────────────────────────────────────

    private sealed record Send(string TransferId, bool WhileDegraded);

    private sealed record SpoolRow(string State, int Attempts, DateTimeOffset NextAttemptUtc, TimeSpan LastDelay);

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly EdgeTransferLoop _loop;
        private bool _started;

        /// <summary>The channel's state for the probe lane in <see cref="ProbeThroughAnOutageAsync"/>.</summary>
        public volatile bool Healthy;

        public SpoolFixture Spool { get; } = new();
        public FakeClock Clock => Spool.Clock;
        public EdgeRuntimeState State { get; } = new();
        public RoutedChannel Channel { get; }
        public CapturingLogger Logger { get; } = new();

        /// <param name="route">The whole script; overrides the other two.</param>
        /// <param name="probeRoute">The answer for the "probe" event; by default it fails until <see cref="Healthy"/>.</param>
        /// <param name="laneRoute">The answer for every other event; by default it is accepted.</param>
        public RecoveryFixture(
            Func<EventEnvelope, TransferAttempt>? route = null,
            Func<EventEnvelope, TransferAttempt>? probeRoute = null,
            Func<EventEnvelope, TransferAttempt>? laneRoute = null)
        {
            probeRoute ??= _ => Healthy ? Accept() : Fail(TransferClass.Transient, TransferReason.ConnectionRefused);
            laneRoute ??= _ => Accept();
            route ??= e => e.TransferId == "probe" ? probeRoute(e) : laneRoute(e);

            var options = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_test" };
            options.Storage.Path = Spool.Options.Path;
            options.Transfer.IdlePollInterval = TimeSpan.FromMilliseconds(5);

            Channel = new RoutedChannel(route, State);
            _loop = new EdgeTransferLoop(
                Spool.Spool, Channel, new BackoffPolicy(options.Transfer, new FixedRandom(0.5)), State, new EdgeWake(),
                options, Clock, Logger);
        }

        public Task<SpoolAccept> EnqueueAsync(string transferId, string? groupKey = null)
            => Spool.Spool.EnqueueAsync(Spool.Envelope(groupKey: groupKey ?? transferId, transferId: transferId), CancellationToken.None);

        /// <summary>Lanes whose head already failed with <c>outcome</c> and waits <c>wait</c> (also its earned ladder).</summary>
        public async Task SeedWaitingAsync(params (string Lane, TransferOutcome Outcome, TimeSpan Wait)[] lanes)
        {
            var rows = new Dictionary<long, (TransferOutcome Outcome, TimeSpan Wait)>();
            foreach (var (lane, outcome, wait) in lanes)
                rows[(await EnqueueAsync(lane)).SpoolId] = (outcome, wait);

            foreach (var claim in await Spool.Spool.ClaimReadyAsync(rows.Count, TimeSpan.FromMinutes(2), CancellationToken.None))
            {
                var (outcome, wait) = rows[claim.SpoolId];
                await Spool.Spool.RescheduleAsync(claim.SpoolId, outcome, Clock.UtcNow + wait, wait, CancellationToken.None);
            }
        }

        /// <summary>
        /// The outage and its end, through one "probe" event: it fails in a full round (degraded), fails again as the
        /// probe (the uplink is confirmed down), then the channel heals and the next probe is accepted.
        /// </summary>
        public async Task ProbeThroughAnOutageAsync()
        {
            await EnqueueAsync("probe");
            await StartAsync();

            await UntilAsync(() => Task.FromResult(Row("probe") is { State: "Accepted", Attempts: 1 } && State.Degraded));
            Clock.Advance(TimeSpan.FromSeconds(1)); // base 1 s etter første feil

            await UntilAsync(() => Task.FromResult(Row("probe") is { State: "Accepted", Attempts: 2 }));
            Assert.Contains(Channel.Sends, s => s.TransferId == "probe" && s.WhileDegraded);

            Healthy = true;
            Clock.Advance(TimeSpan.FromSeconds(2)); // 1 s + 0,5 × (3 × 1 s − 1 s)
        }

        public async Task StartAsync()
        {
            await _loop.StartAsync(CancellationToken.None);
            _started = true;
        }

        public async Task UntilAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return;
                await Task.Delay(5);
            }
            Assert.Fail("condition not reached within 10s");
        }

        /// <summary>Settlede events har forlatt <c>spool</c>; bare korreleringen i <c>settled</c> står igjen.</summary>
        public bool IsSettled(string transferId)
        {
            using var conn = new SqliteConnection($"Data Source={Spool.Options.Path};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM settled WHERE transfer_id = @t;";
            cmd.Parameters.AddWithValue("@t", transferId);
            return (long)cmd.ExecuteScalar()! == 1;
        }

        public SpoolRow Row(string transferId)
        {
            using var conn = new SqliteConnection($"Data Source={Spool.Options.Path};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT state, attempts, next_attempt_utc, last_delay_ms FROM spool WHERE transfer_id = @t;";
            cmd.Parameters.AddWithValue("@t", transferId);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), $"no spool row {transferId}");
            return new SpoolRow(
                reader.GetString(0),
                reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                TimeSpan.FromMilliseconds(reader.GetInt64(3)));
        }

        public async ValueTask DisposeAsync()
        {
            if (_started) await _loop.StopAsync(CancellationToken.None);
            Spool.Dispose();
        }
    }

    private sealed class RoutedChannel(Func<EventEnvelope, TransferAttempt> route, EdgeRuntimeState state) : ITransferChannel
    {
        private readonly object _lock = new();
        private readonly List<Send> _sends = new();

        public IReadOnlyList<Send> Sends { get { lock (_lock) return _sends.ToArray(); } }

        public Task<TransferAttempt> SendAsync(EventEnvelope envelope, int attemptNumber, CancellationToken ct)
        {
            lock (_lock) _sends.Add(new Send(envelope.TransferId, state.Degraded));
            return Task.FromResult(route(envelope));
        }
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private sealed class CapturingLogger : ILogger<EdgeTransferLoop>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> _lines = new();
        public IReadOnlyCollection<(LogLevel Level, string Text)> Lines => _lines.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
