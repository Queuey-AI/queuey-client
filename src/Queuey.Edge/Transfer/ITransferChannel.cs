using System;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Edge;

/// <summary>
/// The protocol seam: one attempt to hand one event to Queuey Cloud. v1 is
/// HTTP POST against the existing ingress; batch or a different transport
/// lands behind this interface without touching the spool or the loop.
/// Implementations must be safe to call repeatedly with the same envelope —
/// the transfer identity makes retries idempotent at Cloud.
/// </summary>
public interface ITransferChannel
{
    Task<TransferAttempt> SendAsync(EventEnvelope envelope, int attemptNumber, CancellationToken cancellationToken);
}

/// <summary>
/// The result of one send. <see cref="Ack"/> is non-null exactly when
/// <see cref="Outcome"/> is <see cref="TransferClass.Accepted"/>.
/// </summary>
public sealed record TransferAttempt(TransferOutcome Outcome, CloudAck? Ack);

/// <summary>
/// Proof of Cloud custody. <see cref="CloudEventId"/> may be null: a queue
/// configured with <c>SuccessStatusCode = 204</c> returns no body, and the
/// STATUS CLASS — never the body — is the ACK. <see cref="Replayed"/> comes
/// from the <c>X-Queuey-Idempotent-Replay</c> header and is a SUCCESS: the
/// original accept already holds custody.
/// </summary>
public sealed record CloudAck(string? CloudEventId, bool Replayed, DateTimeOffset AtUtc);
