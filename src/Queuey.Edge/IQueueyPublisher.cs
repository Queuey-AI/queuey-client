using System;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Edge;

/// <summary>
/// The application-facing surface of Queuey Edge. Register with
/// <c>AddQueueyEdge(...)</c>; after that, delivery is one call.
/// </summary>
public interface IQueueyPublisher
{
    /// <summary>
    /// Hands an event to Queuey. When this call returns successfully, Queuey has durably accepted
    /// the event on this machine and owns its delivery mechanics from here: transfer to Queuey
    /// Cloud, retries, backoff, reconnect, lost-acknowledgement resolution, idempotent resend,
    /// recovery across process and machine restarts, and backlog draining. No further application
    /// code is involved in delivery.
    /// </summary>
    /// <remarks>
    /// <para><b>What success means.</b> The event is committed to Queuey's local durable store
    /// (flushed to disk before this method returns) under a stable transfer identity that Queuey
    /// Cloud deduplicates on permanently — a retry, however late, can never become a second
    /// logical event. Queuey retains the event and keeps transferring until Queuey Cloud accepts
    /// it or an operator explicitly discards it; age alone never deletes an accepted event.</para>
    /// <para><b>What success does not mean.</b> The event has usually not reached Queuey Cloud
    /// yet, and it does not survive destruction of this machine's storage. Delivery from Queuey
    /// Cloud to your destination is operated and observable in the Queuey console.</para>
    /// <para><b>Ordering.</b> Events reach Queuey Cloud in strict accept order within a lane
    /// (the queue, or the queue plus <see cref="PublishOptions.GroupKey"/>), with at most one
    /// transfer in flight per lane. There is no ordering across lanes. Whether that order is
    /// kept through to your destination is the queue's ordering policy, not this method's
    /// promise.</para>
    /// <para><b>Failures.</b> This method throws only for conditions that exist before Queuey
    /// takes responsibility: missing configuration
    /// (<see cref="QueueyConfigurationException"/>), a payload Queuey cannot accept
    /// (<see cref="QueueyPayloadRejectedException"/>), a full local store
    /// (<see cref="QueueySpoolFullException"/>), or a faulted local store awaiting operator
    /// recovery (<see cref="QueueyStorageFaultedException"/>). Network state, Queuey Cloud
    /// availability and HTTP errors never surface here — handling them is Queuey's job, not
    /// yours.</para>
    /// </remarks>
    Task<PublishReceipt> PublishAsync<T>(
        string queue,
        T payload,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raw-bytes overload of <see cref="PublishAsync{T}"/> with an explicit content type.
    /// Same semantic contract.
    /// </summary>
    Task<PublishReceipt> PublishAsync(
        string queue,
        byte[] payload,
        string contentType,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The receipt of a durable LOCAL acceptance. Deliberately not a
/// <see cref="PublishResult"/>: at accept time no Cloud-side ids
/// (<c>evt_…</c>, <c>que_…</c>) exist yet, and returning a shape whose fields
/// would only be meaningfully populated later is exactly the half-truth Edge
/// exists to remove. <see cref="TransferId"/> is the permanent correlation
/// handle — it is the <c>Idempotency-Key</c> on the wire and appears in the
/// Queuey console once the event lands.
/// </summary>
public sealed class PublishReceipt
{
    /// <summary>The event's permanent transfer identity (the Cloud dedup key).</summary>
    public string TransferId { get; init; } = default!;

    /// <summary>Queue display name the event was accepted for.</summary>
    public string Queue { get; init; } = default!;

    /// <summary>When Edge durably accepted the event (this machine's clock).</summary>
    public DateTimeOffset AcceptedAtUtc { get; init; }

    /// <summary>
    /// The occurrence time that will be asserted to Cloud via
    /// <c>X-Queuey-Occurred-At</c> — equals <see cref="AcceptedAtUtc"/>
    /// unless the caller supplied one.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; init; }
}
