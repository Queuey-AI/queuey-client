using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Queuey.Edge;

/// <summary>
/// The whole observability deliverable: the in-process snapshot and the
/// OpenTelemetry meter <c>Queuey.Edge</c>. Queuey EXPOSES health; the
/// customer operates monitoring with whatever they already run — and if
/// only one signal gets an alert, it should be
/// <c>queuey.edge.spool.oldest_age_seconds</c>.
///
/// Runs as a hosted service purely to (a) publish the meter eagerly and
/// (b) log the ephemeral-storage warning at startup, where the operator is
/// looking. It is never load-bearing for delivery.
/// </summary>
internal sealed class EdgeHealthService : IQueueyEdgeHealth, IHostedService, IDisposable
{
    public const string MeterName = "Queuey.Edge";

    private static readonly TimeSpan StatsCacheTtl = TimeSpan.FromSeconds(1);

    private readonly IEventSpool _spool;
    private readonly EdgeRuntimeState _state;
    private readonly QueueyEdgeOptions _options;
    private readonly IEdgeClock _clock;
    private readonly ILogger<EdgeHealthService> _logger;
    private readonly Meter _meter = new(MeterName);
    private readonly bool _ephemeralPath;

    private SpoolStats? _cachedStats;
    private long _cachedAtMono = long.MinValue;

    public EdgeHealthService(
        IEventSpool spool,
        EdgeRuntimeState state,
        QueueyEdgeOptions options,
        IEdgeClock clock,
        ILogger<EdgeHealthService> logger)
    {
        _spool = spool;
        _state = state;
        _options = options;
        _clock = clock;
        _logger = logger;
        _ephemeralPath = LooksEphemeral(options.Storage.Path);

        _meter.CreateObservableGauge("queuey.edge.spool.pending",
            () => Stats()?.PendingCount ?? 0, description: "Events accepted locally, not yet transferred to Queuey Cloud.");
        _meter.CreateObservableGauge("queuey.edge.spool.oldest_age_seconds",
            () => Stats()?.OldestPendingAge?.TotalSeconds ?? 0d, unit: "s",
            description: "Age of the oldest pending event. The one signal worth alerting on.");
        _meter.CreateObservableGauge("queuey.edge.spool.bytes",
            () => Stats()?.StorageUsageBytes ?? 0, unit: "By", description: "Live bytes in the spool (file size minus freed pages) — what the storage limit is measured against.");
        _meter.CreateObservableGauge("queuey.edge.spool.quarantined",
            () => Stats()?.QuarantinedCount ?? 0, description: "Events parked outside the retry path, awaiting operator retry/discard.");
        _meter.CreateObservableGauge("queuey.edge.cloud.last_contact_seconds",
            () => _state.LastSuccessfulCloudContact is { } at ? (_clock.UtcNow - at).TotalSeconds : -1d, unit: "s",
            description: "Seconds since the last successful Cloud contact; -1 before first contact.");
        _meter.CreateObservableGauge("queuey.edge.state",
            () => (long)Snapshot().State, description: "EdgeState as a number (0 Healthy … 4 StorageFaulted). Values frozen.");
        _meter.CreateObservableCounter("queuey.edge.transfer.accepted",
            () => new[]
            {
                new Measurement<long>(_state.AcceptedFreshCount, new KeyValuePair<string, object?>("replayed", "false")),
                new Measurement<long>(_state.AcceptedReplayedCount, new KeyValuePair<string, object?>("replayed", "true"))
            },
            description: "Transfers Cloud has taken custody of (a replay is a success).");
        _meter.CreateObservableCounter("queuey.edge.transfer.failed",
            () => _state.FailureMeasurements(),
            description: "Failed transfer attempts by class (what Edge did) and reason (what happened).");
    }

    public EdgeHealth Snapshot()
    {
        var stats = Stats();
        var faultReason = _spool.FaultReason;

        var state = faultReason is not null
            ? EdgeState.StorageFaulted
            : stats is null
                ? EdgeState.StorageFaulted
                : stats.StorageUsageBytes >= _options.Storage.MaxSpoolBytes - _options.Storage.HeadroomBytes
                    ? EdgeState.StorageFull
                    : _state.Degraded && _state.LastTransferFailure?.Class == TransferClass.RequiresAction
                        ? EdgeState.RequiresAction
                        : stats.PendingCount > 0
                            ? EdgeState.Backlogged
                            : EdgeState.Healthy;

        return new EdgeHealth(
            State: state,
            PendingCount: stats?.PendingCount ?? 0,
            OldestPendingAge: stats?.OldestPendingAge,
            StorageUsageBytes: stats?.StorageUsageBytes ?? 0,
            LastSuccessfulCloudContact: _state.LastSuccessfulCloudContact,
            LastTransferFailure: _state.LastTransferFailure,
            QuarantinedCount: stats?.QuarantinedCount ?? 0,
            StorageDurabilityWarning: _ephemeralPath,
            NextTransferAttemptUtc: stats?.NextAttemptUtc);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_ephemeralPath)
        {
            // A warning, not a refusal: storage durability is the CUSTOMER'S
            // responsibility to choose — Queuey's job is to make the choice
            // visible, here and on the health flag.
            _logger.LogWarning(EdgeLogEvents.EphemeralPath,
                "The Edge spool path '{Path}' looks EPHEMERAL (temp directory). Accepted events will not " +
                "survive whatever clears it. Point Storage.Path at durable local disk (and in containers, " +
                "a persistent volume) for the durability PublishAsync promises.", _options.Storage.Path);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();

    /// <summary>
    /// Spool stats with a 1 s cache so a metrics scrape (eight instruments)
    /// costs one local read, and a faulted spool yields null instead of
    /// throwing out of a metrics callback.
    /// </summary>
    private SpoolStats? Stats()
    {
        var nowMono = _clock.MonotonicMilliseconds;
        if (_cachedAtMono != long.MinValue && nowMono - _cachedAtMono < StatsCacheTtl.TotalMilliseconds)
            return _cachedStats;

        try
        {
            // Local SQLite point read — sub-millisecond; sync-over-async is
            // acceptable in a metrics callback that fires per scrape.
            _cachedStats = _spool.GetStatsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Faulted spool, unreadable file, vanished directory: a metrics
            // callback must never throw into the listener. Null derives
            // StorageFaulted in the snapshot, which is the honest reading.
            _cachedStats = null;
        }
        _cachedAtMono = nowMono;
        return _cachedStats;
    }

    private static bool LooksEphemeral(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var temp = Path.GetFullPath(Path.GetTempPath());
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith("/tmp/", StringComparison.Ordinal)
                   || full.StartsWith("/dev/shm/", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
