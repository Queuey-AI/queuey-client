using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Queuey.Edge;

/// <summary>
/// The one place status codes and exceptions become behaviour. Everything
/// downstream dispatches on <see cref="TransferClass"/> and reads
/// <see cref="TransferReason"/> for diagnosis — never re-inspecting codes.
///
/// Deliberately NOT the backend's FailureClass: that enum is a frozen
/// persisted contract about the CUSTOMER'S destination with operator
/// wording built on top. This hop's destination is Queuey itself; sharing
/// values would make "unavailable" ambiguous in exactly the surfaces that
/// exist to tell operators what to do.
/// </summary>
internal sealed class TransferOutcomeClassifier : ITransferOutcomeClassifier
{
    public TransferOutcome ClassifyResponse(
        int statusCode, string? bodySnippet, RetryConditionHeaderValue? retryAfter, DateTimeOffset atUtc, int attempt)
    {
        var evidence = TransferEvidence.Create(statusCode, bodySnippet, atUtc, attempt);
        var retryDelay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - atUtc : null);

        return statusCode switch
        {
            >= 200 and < 300 => new TransferOutcome(TransferClass.Accepted, TransferReason.Accepted, evidence),

            429 => new TransferOutcome(TransferClass.Throttled, TransferReason.RateLimited, evidence) { RetryAfter = retryDelay },
            503 when retryDelay is not null
                => new TransferOutcome(TransferClass.Throttled, TransferReason.RateLimited, evidence) { RetryAfter = retryDelay },

            500 or 502 or 503 or 504 or 408
                => new TransferOutcome(TransferClass.Transient, TransferReason.CloudServerError, evidence),

            401 => new TransferOutcome(TransferClass.RequiresAction, TransferReason.AuthenticationRejected, evidence),
            403 => new TransferOutcome(TransferClass.RequiresAction, TransferReason.Forbidden, evidence),
            404 => new TransferOutcome(TransferClass.RequiresAction, TransferReason.RouteUnknown, evidence),
            402 => new TransferOutcome(TransferClass.RequiresAction, TransferReason.BillingBlocked, evidence),
            // 409 on ingress = queue_paused: an INTENTIONAL operator state,
            // not an error — retain and probe until the operator resumes.
            409 => new TransferOutcome(TransferClass.RequiresAction, TransferReason.QueuePaused, evidence),

            400 => new TransferOutcome(TransferClass.EventRejected, TransferReason.MalformedRequest, evidence),
            413 => new TransferOutcome(TransferClass.EventRejected, TransferReason.PayloadTooLarge, evidence),
            415 => new TransferOutcome(TransferClass.EventRejected, TransferReason.UnsupportedContentType, evidence),
            // 422 (loop detection) is also permanent for this event.
            422 => new TransferOutcome(TransferClass.EventRejected, TransferReason.MalformedRequest, evidence),

            _ => new TransferOutcome(TransferClass.Unknown, TransferReason.Unclassified, evidence)
        };
    }

    public TransferOutcome ClassifyException(Exception exception, DateTimeOffset atUtc, int attempt)
    {
        var evidence = TransferEvidence.Create(null, exception.Message, atUtc, attempt);

        // A timeout is INDETERMINATE, never a failure: Cloud may hold the
        // event. The retry re-sends the same transfer identity and resolves
        // through Cloud's dedup — that is what makes it safe.
        if (exception is TaskCanceledException or TimeoutException)
            return new TransferOutcome(TransferClass.Transient, TransferReason.Timeout, evidence);

        if (FindInner<AuthenticationException>(exception) is not null)
            return new TransferOutcome(TransferClass.RequiresAction, TransferReason.TlsFailure, evidence);

        if (FindInner<SocketException>(exception) is { } socket)
        {
            var reason = socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => TransferReason.DnsFailure,
                SocketError.ConnectionRefused => TransferReason.ConnectionRefused,
                SocketError.ConnectionReset or SocketError.ConnectionAborted => TransferReason.ConnectionReset,
                _ => TransferReason.Unclassified
            };
            return new TransferOutcome(TransferClass.Transient, reason, evidence);
        }

        if (exception is HttpRequestException)
            return new TransferOutcome(TransferClass.Transient, TransferReason.Unclassified, evidence);

        return new TransferOutcome(TransferClass.Unknown, TransferReason.Unclassified, evidence);
    }

    private static T? FindInner<T>(Exception exception) where T : Exception
    {
        for (var e = (Exception?)exception; e is not null; e = e.InnerException)
        {
            if (e is T match) return match;
        }
        return null;
    }
}
