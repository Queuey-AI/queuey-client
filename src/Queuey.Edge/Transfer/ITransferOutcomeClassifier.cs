using System;
using System.Net.Http.Headers;

namespace Queuey.Edge;

/// <summary>
/// THE single place a response or exception becomes a
/// <see cref="TransferOutcome"/>. Classify once, persist the class, never
/// re-dispatch on status codes downstream — the same discipline the Cloud
/// delivery engine applies to its own (separate, unshared) FailureClass.
/// </summary>
public interface ITransferOutcomeClassifier
{
    /// <summary>Classifies an HTTP response that arrived.</summary>
    TransferOutcome ClassifyResponse(int statusCode, string? bodySnippet, RetryConditionHeaderValue? retryAfter, DateTimeOffset atUtc, int attempt);

    /// <summary>Classifies a transport failure (no response arrived).</summary>
    TransferOutcome ClassifyException(Exception exception, DateTimeOffset atUtc, int attempt);
}
