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
/// back. When a probe has found the uplink down and a later one gets
/// through, every lane that was waiting out a transient failure resumes at
/// once instead of on its own retry schedule.
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

    // 1 når en probe (en runde tatt mens løkka var degradert) feilet transient: utfallet er bekreftet, ikke bare
    // én lane som feiler. Uten dette kravet ville én lane med vedvarende 500 bli gjenopptatt av hver suksess i de
    // andre lanene, det vil si prøvd annenhver runde forbi backoff-en sin.
    private int _uplinkDownConfirmed;

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
                var probing = _state.Degraded;
                var lanes = probing ? 1 : _options.Transfer.MaxConcurrency;
                var claims = await _spool.ClaimReadyAsync(lanes, _options.Transfer.ClaimLease, stoppingToken)
                    .ConfigureAwait(false);

                if (claims.Count == 0)
                {
                    await _wake.WaitAsync(_options.Transfer.IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await Task.WhenAll(claims.Select(c => ProcessAsync(c, probing, stoppingToken))).ConfigureAwait(false);
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

    private async Task ProcessAsync(ClaimedEvent claim, bool probe, CancellationToken ct)
    {
        var attemptNumber = claim.Attempts + 1;
        var attempt = await _channel.SendAsync(claim.Envelope, attemptNumber, ct).ConfigureAwait(false);
        var outcome = attempt.Outcome;

        switch (outcome.Class)
        {
            case TransferClass.Accepted:
                await _spool.SettleAsync(claim.SpoolId, attempt.Ack!, ct).ConfigureAwait(false);
                var endedDegraded = _state.RecordAccepted(new AcceptedTransfer(claim.Envelope.Queue, claim.Envelope.TransferId,
                    attempt.Ack!.CloudEventId, attempt.Ack.Replayed, attempt.Ack.AtUtc));
                // Blindtest 2 (2026-10-09, funn 13): event-id-en Cloud ga, så hendelsen kan slås opp med events get.
                // Security-review av queuey-client #71: Idempotency-Key-en er produsentens tekst og kan bære personopplysninger
                // (en e-post, et ordrenummer), så den logges bare på Debug. Event-id-en er Queuey sin.
                if (attempt.Ack.CloudEventId is { } eventId)
                    _logger.LogInformation(EdgeLogEvents.Accepted,
                        "Event {EventId} accepted in {Queue}; look it up with queuey events get {EventId} --queue {Queue}.",
                        eventId, claim.Envelope.Queue, eventId, claim.Envelope.Queue);
                else
                    _logger.LogInformation(EdgeLogEvents.Accepted,
                        "An event was accepted in {Queue}; the queue answers without a body, so there is no event id.", claim.Envelope.Queue);
                _logger.LogDebug(EdgeLogEvents.Accepted, "Transfer {TransferId} (Idempotency-Key) was accepted as event {EventId}.",
                    claim.Envelope.TransferId, attempt.Ack.CloudEventId ?? "(none)");
                if (attempt.Ack.Replayed)
                {
                    // Security-review av queuey-client #71 runde 2: Idempotency-Key-en bare på Debug, overalt.
                    _logger.LogInformation(EdgeLogEvents.SettledAsReplay,
                        "Event {EventId} in {Queue} settled as a replay — Cloud already held custody (a lost ACK, resolved).",
                        attempt.Ack.CloudEventId ?? "(no id)", claim.Envelope.Queue);
                }

                // Bare suksessen som slo av degradert, og bare når en probe bekreftet utfallet: én gang per gjenoppretting.
                if (endedDegraded && Interlocked.Exchange(ref _uplinkDownConfirmed, 0) == 1)
                    await ResumeAfterOutageAsync(ct).ConfigureAwait(false);
                break;

            case TransferClass.EventRejected:
                // This event's bytes cannot succeed; the LANE continues
                // (step-aside). Exits are explicit operator retry/discard.
                await _spool.QuarantineAsync(claim.SpoolId, outcome, ct).ConfigureAwait(false);
                _state.RecordFailure(outcome);
                // Cloud tok ikke imot eventet, så det har ingen event-id; spool-id-en er det edge retry og discard tar.
                _logger.LogWarning(EdgeLogEvents.Quarantined,
                    "Spooled event #{SpoolId} in {Queue} quarantined ({Reason}, HTTP {Status}); its lane continues. " +
                    "After remediation: queuey edge retry --spool <path> --id {SpoolId}, or queuey edge discard --spool <path> --id {SpoolId}.",
                    claim.SpoolId, claim.Envelope.Queue, outcome.Reason, outcome.Evidence?.StatusCode, claim.SpoolId, claim.SpoolId);
                _logger.LogDebug(EdgeLogEvents.Quarantined, "Spooled event #{SpoolId} is transfer {TransferId} (Idempotency-Key).",
                    claim.SpoolId, claim.Envelope.TransferId);
                break;

            default:
                var delay = _backoff.NextDelay(outcome, claim.LastDelay);
                await _spool.RescheduleAsync(claim.SpoolId, outcome, _clock.UtcNow + delay, delay, ct)
                    .ConfigureAwait(false);
                _state.RecordFailure(outcome);
                if (probe && outcome.Class is TransferClass.Transient or TransferClass.Unknown)
                    Volatile.Write(ref _uplinkDownConfirmed, 1);

                if (outcome.Reason == TransferReason.ClockSkew)
                {
                    _logger.LogCritical(EdgeLogEvents.RequiresAction,
                        "Transfer blocked: Queuey refused the signature's timestamp (HTTP {Status}): this machine's clock is more " +
                        "than 5 minutes off. Events are retained; probing every {Delay}. Sync the clock (NTP; under systemd, " +
                        "time-sync.target) and Edge resumes automatically. The key is checked once the clock is right.",
                        outcome.Evidence?.StatusCode, delay);
                }
                else if (outcome.Class == TransferClass.RequiresAction)
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

    /// <summary>
    /// The uplink is back: every lane head that was waiting out a transient failure is due now, and the loop wakes,
    /// so the next round claims them all, up to <see cref="EdgeTransferOptions.MaxConcurrency"/>. Each row keeps its
    /// backoff ladder. What Cloud or an operator must clear (Retry-After, auth, pause, billing, clock) keeps its wait.
    /// </summary>
    private async Task ResumeAfterOutageAsync(CancellationToken ct)
    {
        var resumed = await _spool.ResumeTransientAsync(ct).ConfigureAwait(false);
        _wake.Poke();
        if (resumed > 0)
            _logger.LogInformation(EdgeLogEvents.UplinkRecovered,
                "Uplink recovered: {Resumed} pending event(s) resumed at once.", resumed);
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
    public static readonly EventId Accepted = new(7315, "queuey.edge.accepted");
    public static readonly EventId UplinkRecovered = new(7316, "queuey.edge.uplink_recovered");
}
