using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Health;

/// <summary>
/// The health surface: state derivation over the real spool, and the OTel
/// meter's instruments. Queuey exposes; the customer monitors — so what is
/// frozen here is exactly what a monitoring system would wire.
/// </summary>
public class EdgeHealthServiceTests
{
    [Fact]
    public async Task Healthy_then_backlogged_then_healthy_again()
    {
        using var fx = new HealthFixture();

        Assert.Equal(EdgeState.Healthy, fx.Health.Snapshot().State);

        var accept = await fx.Spool.Spool.EnqueueAsync(fx.Spool.Envelope(), CancellationToken.None);
        fx.InvalidateCache();
        var backlogged = fx.Health.Snapshot();
        Assert.Equal(EdgeState.Backlogged, backlogged.State);
        Assert.Equal(1, backlogged.PendingCount);

        await fx.Spool.Spool.ClaimReadyAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None);
        await fx.Spool.Spool.SettleAsync(accept.SpoolId,
            new CloudAck("evt_1", false, fx.Spool.Clock.UtcNow), CancellationToken.None);
        fx.State.RecordSuccess(fx.Spool.Clock.UtcNow, replayed: false);

        fx.InvalidateCache();
        var healthy = fx.Health.Snapshot();
        Assert.Equal(EdgeState.Healthy, healthy.State);
        Assert.Equal(fx.Spool.Clock.UtcNow, healthy.LastSuccessfulCloudContact);
    }

    [Fact]
    public async Task Storage_full_clears_the_moment_the_backlog_settles()
    {
        using var fx = new HealthFixture(o =>
        {
            o.MaxSpoolBytes = 96 * 1024;
            o.HeadroomBytes = 32 * 1024;
        });

        var accepted = new List<SpoolAccept>();
        QueueySpoolFullException? full = null;
        for (var i = 0; i < 200 && full is null; i++)
        {
            try
            {
                accepted.Add(await fx.Spool.Spool.EnqueueAsync(fx.Spool.Envelope(payloadBytes: 4096), CancellationToken.None));
            }
            catch (QueueySpoolFullException ex)
            {
                full = ex;
            }
        }
        Assert.NotNull(full);
        fx.InvalidateCache();
        Assert.Equal(EdgeState.StorageFull, fx.Health.Snapshot().State);

        foreach (var accept in accepted)
        {
            await fx.Spool.Spool.SettleAsync(accept.SpoolId,
                new CloudAck("evt_x", false, fx.Spool.Clock.UtcNow), CancellationToken.None);
        }
        fx.State.RecordSuccess(fx.Spool.Clock.UtcNow, replayed: false);

        // No sweep, no retention window — Cloud custody alone clears the state.
        fx.InvalidateCache();
        Assert.Equal(EdgeState.Healthy, fx.Health.Snapshot().State);
    }

    [Fact]
    public async Task Requires_action_shows_what_happened_not_just_that_something_did()
    {
        using var fx = new HealthFixture();
        await fx.Spool.Spool.EnqueueAsync(fx.Spool.Envelope(), CancellationToken.None);
        fx.State.RecordFailure(new TransferOutcome(
            TransferClass.RequiresAction, TransferReason.AuthenticationRejected,
            TransferEvidence.Create(401, "invalid_api_key", fx.Spool.Clock.UtcNow, 3)));

        var health = fx.Health.Snapshot();

        Assert.Equal(EdgeState.RequiresAction, health.State);
        Assert.Equal(TransferReason.AuthenticationRejected, health.LastTransferFailure!.Reason);
        Assert.Equal(401, health.LastTransferFailure.StatusCode);
    }

    [Fact]
    public async Task Faulted_spool_reports_storage_faulted_instead_of_throwing()
    {
        using var fx = new HealthFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fx.Spool.Options.Path)!);
        await File.WriteAllBytesAsync(fx.Spool.Options.Path, new byte[2048]); // garbage
        await Assert.ThrowsAnyAsync<Exception>(
            () => fx.Spool.Spool.EnqueueAsync(fx.Spool.Envelope(), CancellationToken.None));

        var health = fx.Health.Snapshot();

        Assert.Equal(EdgeState.StorageFaulted, health.State);
    }

    [Fact]
    public void Ephemeral_spool_path_raises_the_durability_warning()
    {
        using var fx = new HealthFixture(); // SpoolFixture lives under the temp dir

        Assert.True(fx.Health.Snapshot().StorageDurabilityWarning,
            "a temp-dir spool must be flagged — the customer chooses durability, Queuey makes the choice visible");
    }

    [Fact]
    public async Task The_meter_publishes_the_promised_instruments()
    {
        using var fx = new HealthFixture();
        await fx.Spool.Spool.EnqueueAsync(fx.Spool.Envelope(), CancellationToken.None);
        fx.State.RecordSuccess(fx.Spool.Clock.UtcNow, replayed: true);
        fx.State.RecordFailure(new TransferOutcome(TransferClass.Transient, TransferReason.Timeout, null));

        var seen = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == EdgeHealthService.MeterName)
                {
                    seen.Add(instrument.Name);
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Superset(new HashSet<string>
        {
            "queuey.edge.spool.pending",
            "queuey.edge.spool.oldest_age_seconds",
            "queuey.edge.spool.bytes",
            "queuey.edge.spool.quarantined",
            "queuey.edge.cloud.last_contact_seconds",
            "queuey.edge.state",
            "queuey.edge.transfer.accepted",
            "queuey.edge.transfer.failed",
            "queuey.edge.intake.connected",
            "queuey.edge.intake.paused",
            "queuey.edge.intake.last_message_age_seconds",
            "queuey.edge.intake.messages",
            "queuey.edge.intake.without_id"
        }, seen);
    }

    [Fact]
    public void An_intake_shows_in_the_snapshot_and_on_the_meter_by_name()
    {
        var intake = new FixedIntake(new EdgeIntakeHealth("mqtt:localhost:1883", Connected: false, Paused: true,
            LastMessageAtUtc: null, LastConnectedAtUtc: null, LastError: "spool full",
            new EdgeIntakeCounters(Received: 7, Accepted: 3, Deferred: 2, Rejected: 0, Unrouted: 0, RetainedSkipped: 0, Lost: 0,
                Duplicates: 2, WithoutId: 4)));
        using var fx = new HealthFixture(intake: intake);

        var snapshot = Assert.Single(fx.Health.Snapshot().Intakes!);
        Assert.Equal("mqtt:localhost:1883", snapshot.Name);
        Assert.True(snapshot.Paused);

        var measured = new List<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == EdgeHealthService.MeterName && instrument.Name.StartsWith("queuey.edge.intake."))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => measured.Add((i.Name, v, Tags(tags))));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => measured.Add((i.Name, v, Tags(tags))));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.connected" && m.Value == 0 && (string?)m.Tags["intake"] == "mqtt:localhost:1883");
        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.paused" && m.Value == 1);
        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.last_message_age_seconds" && m.Value == -1);
        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.messages" && m.Value == 2 && (string?)m.Tags["outcome"] == "deferred");
        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.messages" && m.Value == 3 && (string?)m.Tags["outcome"] == "accepted");
        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.messages" && m.Value == 2 && (string?)m.Tags["outcome"] == "duplicate");
        Assert.Contains(measured, m => m.Instrument == "queuey.edge.intake.without_id" && m.Value == 4 && (string?)m.Tags["intake"] == "mqtt:localhost:1883");

        static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            return d;
        }
    }

    [Fact]
    public void The_storage_limit_is_the_ceiling_the_spool_accepts_up_to()
    {
        using var fx = new HealthFixture(o =>
        {
            o.MaxSpoolBytes = 96 * 1024;
            o.HeadroomBytes = 32 * 1024;
        });

        Assert.Equal(64 * 1024, fx.Health.Snapshot().StorageLimitBytes);
    }

    [Fact]
    public async Task A_one_megabyte_spool_takes_events_up_to_three_quarters_and_then_reports_full()
    {
        // --spool-max-mb 1: the default headroom shrinks to a quarter, so a test can fill the spool in minutes.
        using var fx = new HealthFixture(o => o.MaxSpoolBytes = 1024 * 1024);
        Assert.Equal(768 * 1024, fx.Health.Snapshot().StorageLimitBytes);

        var accepted = 0;
        QueueySpoolFullException? full = null;
        for (var i = 0; i < 2000 && full is null; i++)
        {
            try
            {
                await fx.Spool.Spool.EnqueueAsync(fx.Spool.Envelope(payloadBytes: 1024), CancellationToken.None);
                accepted++;
            }
            catch (QueueySpoolFullException ex)
            {
                full = ex;
            }
        }

        Assert.NotNull(full);
        Assert.InRange(accepted, 300, 767); // 1 KB events in 768 KB, less row overhead and the spool's own pages
        fx.InvalidateCache();
        Assert.Equal(EdgeState.StorageFull, fx.Health.Snapshot().State);
    }

    [Theory]
    [InlineData(EdgeState.Healthy, 60, 40, true)]
    [InlineData(EdgeState.Backlogged, 60, 41, false)]
    [InlineData(EdgeState.StorageFull, 100, 0, true)]
    [InlineData(EdgeState.StorageFaulted, 0, 1, false)]
    public void Room_for_a_payload_follows_the_spools_accept_rule(EdgeState state, long usage, long payload, bool room)
    {
        var health = new EdgeHealth(state, 0, null, usage, null, null, 0, false, StorageLimitBytes: 100);

        Assert.Equal(room, health.HasRoomFor(payload));
    }

    [Fact]
    public void Without_a_known_limit_room_is_decided_by_state_alone()
    {
        var health = new EdgeHealth(EdgeState.Healthy, 0, null, long.MaxValue, null, null, 0, false);

        Assert.True(health.HasRoomFor(1));
        Assert.False((health with { State = EdgeState.StorageFaulted }).HasRoomFor(1));
    }

    [Fact]
    public void Counters_split_fresh_from_replayed_and_failures_by_class_and_reason()
    {
        var state = new EdgeRuntimeState();
        state.RecordSuccess(DateTimeOffset.UtcNow, replayed: false);
        state.RecordSuccess(DateTimeOffset.UtcNow, replayed: true);
        state.RecordSuccess(DateTimeOffset.UtcNow, replayed: true);
        state.RecordFailure(new TransferOutcome(TransferClass.Transient, TransferReason.Timeout, null));
        state.RecordFailure(new TransferOutcome(TransferClass.Transient, TransferReason.Timeout, null));
        state.RecordFailure(new TransferOutcome(TransferClass.RequiresAction, TransferReason.Forbidden, null));

        Assert.Equal(1, state.AcceptedFreshCount);
        Assert.Equal(2, state.AcceptedReplayedCount);
        var failures = state.FailureMeasurements().ToDictionary(
            m => string.Join("|", m.Tags.ToArray().Select(t => t.Value)),
            m => m.Value);
        Assert.Equal(2, failures["Transient|Timeout"]);
        Assert.Equal(1, failures["RequiresAction|Forbidden"]);
    }

    private sealed class HealthFixture : IDisposable
    {
        public SpoolFixture Spool { get; }
        public EdgeRuntimeState State { get; } = new();
        public EdgeHealthService Health { get; }

        public HealthFixture(Action<EdgeStorageOptions>? configure = null, IEdgeIntake? intake = null)
        {
            Spool = new SpoolFixture(configure);
            var options = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_test" };
            options.Storage.Path = Spool.Options.Path;
            options.Storage.MaxSpoolBytes = Spool.Options.MaxSpoolBytes;
            options.Storage.HeadroomBytes = Spool.Options.HeadroomBytes;
            var intakes = new EdgeIntakeRegistry();
            if (intake is not null) intakes.Register(intake);
            Health = new EdgeHealthService(
                Spool.Spool, State, options, Spool.Clock, NullLogger<EdgeHealthService>.Instance, intakes);
        }

        /// <summary>Stats carry a 1 s cache keyed on the monotonic clock — advance it past the TTL.</summary>
        public void InvalidateCache() => Spool.Clock.MonotonicMilliseconds += 2000;

        public void Dispose()
        {
            Health.Dispose();
            Spool.Dispose();
        }
    }

    private sealed class FixedIntake(EdgeIntakeHealth health) : IEdgeIntake
    {
        public EdgeIntakeHealth Snapshot() => health;
    }
}
