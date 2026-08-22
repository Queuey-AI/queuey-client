using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Edge;

/// <summary>
/// The accept pipeline. Four steps, none of which touch the network:
/// validate locally, mint the transfer identity, commit durably, wake the
/// transfer loop. That absence of network is what makes the contract
/// truthful — there is no failure the accept path can inherit from
/// connectivity, so there is none it can hand back to the caller.
/// </summary>
internal sealed class QueueyEdgePublisher : IQueueyPublisher
{
    private const string JsonContentType = "application/json";

    private readonly QueueyEdgeOptions _options;
    private readonly IEventSpool _spool;
    private readonly IEdgeClock _clock;
    private readonly EdgeWake _wake;

    public QueueyEdgePublisher(QueueyEdgeOptions options, IEventSpool spool, IEdgeClock clock, EdgeWake wake)
    {
        _options = options;
        _spool = spool;
        _clock = clock;
        _wake = wake;
    }

    public Task<PublishReceipt> PublishAsync<T>(
        string queue, T payload, PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (payload is null)
            throw new QueueyPayloadRejectedException("The payload cannot be null.");

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(payload, QueueyJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            // Event construction is the caller's side of the boundary —
            // surfaced at the call, not quarantined days later.
            throw new QueueyPayloadRejectedException(
                $"The payload of type {typeof(T).Name} could not be serialized to JSON.", ex);
        }

        return AcceptAsync(queue, bytes, options?.ContentType ?? JsonContentType, options, cancellationToken);
    }

    public Task<PublishReceipt> PublishAsync(
        string queue, byte[] payload, string contentType, PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (payload is null)
            throw new QueueyPayloadRejectedException("The payload cannot be null.");
        if (string.IsNullOrWhiteSpace(contentType))
            throw new QueueyConfigurationException("A content type is required for raw payloads.");

        return AcceptAsync(queue, payload, contentType.Trim(), options, cancellationToken);
    }

    private async Task<PublishReceipt> AcceptAsync(
        string queue, byte[] payload, string contentType, PublishOptions? options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queue))
            throw new QueueyConfigurationException("A queue name is required.");

        if (_options.MaxPayloadBytes is { } cap && payload.LongLength > cap)
        {
            throw new QueueyPayloadRejectedException(
                $"The payload is {payload.LongLength} bytes; the locally configured MaxPayloadBytes is {cap}. " +
                "Fix the producer, or raise the local cap to match the queue's server-side limit.");
        }

        var now = _clock.UtcNow;
        var occurredAt = options?.OccurredAtUtc ?? now;

        // One key, one dedup domain: a caller-supplied idempotency key IS the
        // transfer identity — a second, separate transfer key would create
        // two domains that can disagree. Absent one, Edge mints a UUIDv7 so
        // ids sort by publish time.
        var transferId = string.IsNullOrWhiteSpace(options?.IdempotencyKey)
            ? Uuid7.NewString(now)
            : options!.IdempotencyKey!.Trim();

        var envelope = new EventEnvelope(
            Version: EventEnvelope.CurrentVersion,
            TransferId: transferId,
            Queue: queue.Trim(),
            TenantPublicId: _options.TenantPublicId!,
            ContentType: contentType,
            Payload: payload,
            OccurredAtUtc: occurredAt,
            EventType: options?.EventType,
            GroupKey: options?.GroupKey,
            Source: options?.Source ?? _options.Source);

        // The durable boundary. Everything before this line is the caller's
        // failure; everything after is Queuey's.
        var accept = await _spool.EnqueueAsync(envelope, ct).ConfigureAwait(false);

        _wake.Poke();

        return new PublishReceipt
        {
            TransferId = accept.TransferId,
            Queue = envelope.Queue,
            AcceptedAtUtc = accept.AcceptedAtUtc,
            OccurredAtUtc = occurredAt
        };
    }
}

/// <summary>
/// The publish→transfer wake signal: a publish pokes the loop so drain
/// latency is milliseconds, not a poll interval — while a missed poke costs
/// nothing (the idle poll is the fallback, and spool state is the truth).
/// </summary>
internal sealed class EdgeWake
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Poke()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already poked — coalescing is the point.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _signal.WaitAsync(timeout, cancellationToken);
}
