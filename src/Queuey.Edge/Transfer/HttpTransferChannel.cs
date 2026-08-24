using System;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Edge;

/// <summary>
/// v1 transport: one HTTP POST against the existing ingress surface. Three
/// wire rules matter more than the plumbing:
/// <list type="bullet">
/// <item>The STATUS CLASS is the ACK — a 204-configured queue returns no
///   body, so custody must never depend on parsing one.</item>
/// <item>The replay signal is the <c>X-Queuey-Idempotent-Replay</c> HEADER,
///   and a replay is a SUCCESS.</item>
/// <item><c>X-Queuey-Edge-Version</c> marks the accept for PERMANENT dedup
///   at Cloud — the reason a lost-ACK resend weeks later can never become a
///   second logical event.</item>
/// </list>
/// </summary>
internal sealed class HttpTransferChannel : ITransferChannel
{
    private static readonly string EdgeVersion =
        typeof(HttpTransferChannel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HttpTransferChannel).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly HttpClient _http;
    private readonly QueueyEdgeOptions _options;
    private readonly ITransferOutcomeClassifier _classifier;
    private readonly IEdgeClock _clock;
    private readonly Uri _ingressBase;

    public HttpTransferChannel(
        HttpClient http, QueueyEdgeOptions options, ITransferOutcomeClassifier classifier, IEdgeClock clock)
    {
        _http = http;
        _options = options;
        _classifier = classifier;
        _clock = clock;
        _ingressBase = options.ResolveIngressBaseAddress();
    }

    public async Task<TransferAttempt> SendAsync(EventEnvelope envelope, int attemptNumber, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(envelope, attemptNumber);

        // Per-attempt timeout, separate from the loop's token: an expiry is
        // INDETERMINATE (Cloud may hold the event) and classifies as
        // Transient/Timeout — the idempotent resend resolves the ambiguity.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Transfer.TransferTimeout);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var now = _clock.UtcNow;
            var status = (int)response.StatusCode;

            if (status is >= 200 and < 300)
            {
                var replayed = response.Headers.TryGetValues("X-Queuey-Idempotent-Replay", out var replay)
                               && string.Equals(string.Join("", replay), "true", StringComparison.OrdinalIgnoreCase);

                // Body is OPTIONAL (204 queues): the id is correlation sugar,
                // never part of the custody decision.
                string? cloudEventId = null;
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("eventId", out var idProp))
                            cloudEventId = idProp.GetString();
                    }
                    catch (JsonException)
                    {
                        // A 2xx with an unreadable body is still an ACK.
                    }
                }

                var outcome = new TransferOutcome(
                    TransferClass.Accepted,
                    replayed ? TransferReason.Replayed : TransferReason.Accepted,
                    TransferEvidence.Create(status, null, now, attemptNumber));
                return new TransferAttempt(outcome, new CloudAck(cloudEventId, replayed, now));
            }

            var snippet = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var failure = _classifier.ClassifyResponse(status, snippet, response.Headers.RetryAfter, now, attemptNumber);
            return new TransferAttempt(failure, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown, not an attempt outcome
        }
        catch (Exception ex)
        {
            return new TransferAttempt(_classifier.ClassifyException(ex, _clock.UtcNow, attemptNumber), null);
        }
    }

    private HttpRequestMessage BuildRequest(EventEnvelope envelope, int attemptNumber)
    {
        var uri = new Uri(_ingressBase,
            $"events/{Uri.EscapeDataString(envelope.TenantPublicId)}/{Uri.EscapeDataString(envelope.Queue)}");

        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(envelope.Payload)
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", envelope.ContentType);

        request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", envelope.TransferId);
        request.Headers.TryAddWithoutValidation("X-Queuey-Edge-Version", EdgeVersion);
        request.Headers.TryAddWithoutValidation("X-Queuey-Occurred-At",
            envelope.OccurredAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Queuey-Transfer-Attempt",
            attemptNumber.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(envelope.EventType))
            request.Headers.TryAddWithoutValidation("X-Queuey-Event-Type", envelope.EventType);
        if (!string.IsNullOrEmpty(envelope.GroupKey))
            request.Headers.TryAddWithoutValidation("X-Queuey-Group-Key", envelope.GroupKey);
        if (!string.IsNullOrEmpty(envelope.Source))
            request.Headers.TryAddWithoutValidation("X-Queuey-Source", envelope.Source);

        return request;
    }
}
