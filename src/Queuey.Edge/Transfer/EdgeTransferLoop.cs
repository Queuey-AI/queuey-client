using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Queuey.Edge;

/// <summary>
/// The delivery mechanics the application stopped owning: claim lane heads,
/// send, classify, settle/reschedule/quarantine — forever, across restarts,
/// without ever discarding an accepted event or hot-looping a hopeless one.
///
/// Concurrency is ACROSS lanes (≤1 in-flight per lane is FIFO, enforced by
/// the spool's claim). After any failure the loop drops to a single probe
/// until a success — never open the throttle on a cloud that just came
/// back.
/// </summary>
internal sealed class EdgeTransferLoop : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FaultedRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IEventSpool _spool;
    private readonly ITransferChannel _channel;
    private readonly BackoffPolicy _backoff;
    private readonly EdgeRuntimeState _state;
    private readonly EdgeWake _wake;
    private readonly QueueyEdgeOptions _options;
    private readonly IEdgeClock _clock;
    private readonly ILogger<EdgeTransferLoop> _logger;

    private long _lastSweepMono = long.MinValue;

    public EdgeTransferLoop(
        IEventSpool spool,
        ITransferChannel channel,
        BackoffPolicy backoff,
        EdgeRuntimeState state,
        EdgeWake wake,
        QueueyEdgeOptions options,
        IEdgeClock clock,
        ILogger<EdgeTransferLoop> logger)
    {
        _spool = spool;
        _channel = channel;
        _backoff = backoff;
        _state = state;
        _wake = wake;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation(EdgeLogEvents.LoopStarted,
            "Queuey Edge transfer loop started. Spool: {SpoolPath}", _options.Storage.Path);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepIfDueAsync(stoppingToken).ConfigureAwait(false);

                // Probe-before-throttle: after a failure period, exactly one
                // event tests the water; success reopens full concurrency.
                var lanes = _state.Degraded ? 1 : _options.Transfer.MaxConcurrency;
                var claims = await _spool.ClaimReadyAsync(lanes, _options.Transfer.ClaimLease, stoppingToken)
                    .ConfigureAwait(false);

                if (claims.Count == 0)
                {
                    await _wake.WaitAsync(_options.Transfer.IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await Task.WhenAll(claims.Select(c => ProcessAsync(c, stoppingToken))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (QueueyStorageFaultedException ex)
            {
                // Storage recovery is an explicit operator action — the loop
                // waits rather than spinning against a faulted spool. The
                // host application is NEVER crashed over Queuey's storage.
                _logger.LogCritical(EdgeLogEvents.StorageFaulted,
                    "Edge spool is FAULTED; transfer halted until operator recovery. {Message}", ex.Message);
                await SafeDelayAsync(FaultedRetryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(EdgeLogEvents.LoopError, ex,
                    "Unexpected transfer-loop error; continuing.");
                await SafeDelayAsync(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(EdgeLogEvents.LoopStopped, "Queuey Edge transfer loop stopped.");
    }

    private async Task ProcessAsync(ClaimedEvent claim, CancellationToken ct)
    {
        var attemptNumber = claim.Attempts + 1;
        var attempt = await _channel.SendAsync(claim.Envelope, attemptNumber, ct).ConfigureAwait(false);
        var outcome = attempt.Outcome;

        switch (outcome.Class)
        {
            case TransferClass.Accepted:
                await _spool.SettleAsync(claim.SpoolId, attempt.Ack!, ct).ConfigureAwait(false);
                _state.RecordSuccess(attempt.Ack!.AtUtc, attempt.Ack.Replayed);
                if (attempt.Ack.Replayed)
                {
                    _logger.LogInformation(EdgeLogEvents.SettledAsReplay,
                        "Transfer {TransferId} settled as a replay — Cloud already held custody (a lost ACK, resolved).",
                        claim.Envelope.TransferId);
                }
                break;

            case TransferClass.EventRejected:
                // This event's bytes cannot succeed; the LANE continues
                // (step-aside). Exits are explicit operator retry/discard.
                await _spool.QuarantineAsync(claim.SpoolId, outcome, ct).ConfigureAwait(false);
                _state.RecordFailure(outcome);
                _logger.LogWarning(EdgeLogEvents.Quarantined,
                    "Transfer {TransferId} quarantined ({Reason}, HTTP {Status}); its lane continues. " +
                    "Use 'queuey-edge retry/discard' after remediation.",
                    claim.Envelope.TransferId, outcome.Reason, outcome.Evidence?.StatusCode);
                break;

            default:
                var delay = _backoff.NextDelay(outcome, claim.LastDelay);
                await _spool.RescheduleAsync(claim.SpoolId, outcome, _clock.UtcNow + delay, delay, ct)
                    .ConfigureAwait(false);
                _state.RecordFailure(outcome);

                if (outcome.Class == TransferClass.RequiresAction)
                {
                    _logger.LogCritical(EdgeLogEvents.RequiresAction,
                        "Transfer blocked: {Reason} (HTTP {Status}). Events are retained; probing every {Delay}. " +
                        "This needs an operator — fix credentials/route/billing and Edge resumes automatically.",
                        outcome.Reason, outcome.Evidence?.StatusCode, delay);
                }
                else
                {
                    _logger.LogDebug(EdgeLogEvents.Rescheduled,
                        "Transfer {TransferId} rescheduled in {Delay} ({Class}/{Reason}).",
                        claim.Envelope.TransferId, delay, outcome.Class, outcome.Reason);
                }
                break;
        }
    }

    private async Task SweepIfDueAsync(CancellationToken ct)
    {
        var nowMono = _clock.MonotonicMilliseconds;
        if (_lastSweepMono != long.MinValue && nowMono - _lastSweepMono < SweepInterval.TotalMilliseconds)
            return;

        _lastSweepMono = nowMono;
        await _spool.SweepAsync(ct).ConfigureAwait(false);
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Stable log event ids — the greppable contract for operators' log pipelines.</summary>
internal static class EdgeLogEvents
{
    public static readonly EventId LoopStarted = new(7301, "queuey.edge.loop_started");
    public static readonly EventId LoopStopped = new(7302, "queuey.edge.loop_stopped");
    public static readonly EventId LoopError = new(7303, "queuey.edge.loop_error");
    public static readonly EventId Rescheduled = new(7304, "queuey.edge.rescheduled");
    public static readonly EventId SettledAsReplay = new(7305, "queuey.edge.settled_replay");
    public static readonly EventId Quarantined = new(7306, "queuey.edge.quarantined");
    public static readonly EventId RequiresAction = new(7307, "queuey.edge.requires_action");
    public static readonly EventId StorageFaulted = new(7308, "queuey.edge.storage_faulted");
    public static readonly EventId StorageFull = new(7309, "queuey.edge.storage_full");
    public static readonly EventId EphemeralPath = new(7310, "queuey.edge.ephemeral_path_warning");
    public static readonly EventId LocalEndpointStarted = new(7311, "queuey.edge.local_endpoint_started");
    public static readonly EventId LocalEndpointError = new(7312, "queuey.edge.local_endpoint_error");
    public static readonly EventId HealthReportStarted = new(7313, "queuey.edge.health_report_started");
    public static readonly EventId HealthReportFailed = new(7314, "queuey.edge.health_report_failed");
}
