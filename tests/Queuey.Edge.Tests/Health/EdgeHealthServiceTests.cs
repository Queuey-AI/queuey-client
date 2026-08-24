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
            "queuey.edge.transfer.failed"
        }, seen);
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

        public HealthFixture()
        {
            Spool = new SpoolFixture();
            var options = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_test" };
            options.Storage.Path = Spool.Options.Path;
            Health = new EdgeHealthService(
                Spool.Spool, State, options, Spool.Clock, NullLogger<EdgeHealthService>.Instance);
        }

        /// <summary>Stats carry a 1 s cache keyed on the monotonic clock — advance it past the TTL.</summary>
        public void InvalidateCache() => Spool.Clock.MonotonicMilliseconds += 2000;

        public void Dispose()
        {
            Health.Dispose();
            Spool.Dispose();
        }
    }
}
