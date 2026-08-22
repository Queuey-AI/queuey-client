using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Queuey.Client;

namespace Queuey.Edge;

/// <summary>
/// The polyglot one-liner: a LOOPBACK HTTP endpoint on the Edge daemon that
/// is wire-compatible with Queuey Cloud ingress —
/// <c>POST /events/{tenantPublicId}/{queueName}</c> with the same headers
/// (Idempotency-Key, X-Queuey-Event-Type / Group-Key / Source /
/// Occurred-At). Any language's existing publish snippet becomes DURABLE by
/// swapping the base URL to <c>http://localhost:&lt;port&gt;</c>: the 202
/// answers only after the fsync'd local commit (the same accept boundary as
/// the in-process <c>PublishAsync</c>), and the daemon owns transfer from
/// there. Thin per-language SDKs become trivial wrappers over this.
///
/// <para>Trust model: binds 127.0.0.1 ONLY. The machine is the customer's
/// trust domain (same boundary as the spool file itself); anything that can
/// run code on the box can publish — and could equally exec the CLI. Never
/// bind this to a public interface; LAN gateways belong behind the
/// customer's own network controls.</para>
///
/// <para>Response shape: a 202 with an Edge RECEIPT
/// (<c>{ transferId, queue, acceptedAtUtc, occurredAtUtc, custody: "edge" }</c>)
/// — deliberately NOT a fake Cloud receipt with an <c>eventId</c> that does
/// not exist yet. The STATUS CLASS is the ACK (the same rule Edge itself
/// lives by against Cloud), so clients that only check for 2xx work
/// unchanged against both hosts.</para>
/// </summary>
internal sealed class EdgeLocalEndpoint : BackgroundService
{
    private const long MaxBodyBytes = 8 * 1024 * 1024; // generous local cap; the queue's real cap applies at transfer

    private readonly QueueyEdgeOptions _options;
    private readonly IQueueyPublisher _publisher;
    private readonly IQueueyEdgeHealth _health;
    private readonly ILogger<EdgeLocalEndpoint> _logger;

    public EdgeLocalEndpoint(
        QueueyEdgeOptions options,
        IQueueyPublisher publisher,
        IQueueyEdgeHealth health,
        ILogger<EdgeLocalEndpoint> logger)
    {
        _options = options;
        _publisher = publisher;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.LocalEndpoint.Port is not { } port)
            return; // opt-in only

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _logger.LogInformation(EdgeLogEvents.LocalEndpointStarted,
            "Edge local endpoint listening on http://localhost:{Port} — POST /events/{{tenant}}/{{queue}} " +
            "publishes DURABLY (loopback only; same wire shape as cloud ingress).", port);

        await using var _ = stoppingToken.Register(listener.Stop);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogWarning(EdgeLogEvents.LocalEndpointError, ex, "Local endpoint accept failed; continuing.");
                continue;
            }

            // Sequential handling is deliberate: the accept path is a local
            // SQLite commit (sub-ms) and Edge's whole workload is ~events per
            // minute, not per millisecond. No thread-pool fan-out to tune.
            await HandleAsync(context, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var segments = (request.Url?.AbsolutePath ?? "").Trim('/').Split('/');

            if (request.HttpMethod == "GET" && segments is ["health"])
            {
                await WriteJsonAsync(response, 200, _health.Snapshot(), ct).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod != "POST" || segments is not ["events", var tenant, var queue]
                || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(queue))
            {
                await WriteErrorAsync(response, 404, "not_found",
                    "Expected POST /events/{tenantPublicId}/{queueName} or GET /health.", ct).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(tenant, _options.TenantPublicId, StringComparison.Ordinal))
            {
                await WriteErrorAsync(response, 404, "tenant_mismatch",
                    $"This Edge daemon publishes for '{_options.TenantPublicId}'.", ct).ConfigureAwait(false);
                return;
            }

            if (request.ContentLength64 > MaxBodyBytes)
            {
                await WriteErrorAsync(response, 413, "payload_too_large",
                    $"Body exceeds the local endpoint cap ({MaxBodyBytes} bytes).", ct).ConfigureAwait(false);
                return;
            }

            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                await request.InputStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                payload = buffer.ToArray();
            }

            var options = new PublishOptions
            {
                ContentType = request.ContentType,
                IdempotencyKey = request.Headers["Idempotency-Key"],
                EventType = request.Headers["X-Queuey-Event-Type"],
                GroupKey = request.Headers["X-Queuey-Group-Key"],
                Source = request.Headers["X-Queuey-Source"],
                OccurredAtUtc = ParseOccurredAt(request.Headers["X-Queuey-Occurred-At"])
            };

            var receipt = await _publisher.PublishAsync(
                queue, payload, request.ContentType ?? "application/json", options, ct).ConfigureAwait(false);

            await WriteJsonAsync(response, 202, new
            {
                transferId = receipt.TransferId,
                queue = receipt.Queue,
                acceptedAtUtc = receipt.AcceptedAtUtc,
                occurredAtUtc = receipt.OccurredAtUtc,
                custody = "edge"
            }, ct).ConfigureAwait(false);
        }
        catch (QueueyPayloadRejectedException ex)
        {
            await WriteErrorAsync(response, 400, "payload_rejected", ex.Message, ct).ConfigureAwait(false);
        }
        catch (QueueySpoolFullException ex)
        {
            // 507 Insufficient Storage: unmistakably local, never confused
            // with a Cloud-side rejection.
            await WriteErrorAsync(response, 507, "spool_full", ex.Message, ct).ConfigureAwait(false);
        }
        catch (QueueyStorageFaultedException ex)
        {
            await WriteErrorAsync(response, 503, "storage_faulted", ex.Message, ct).ConfigureAwait(false);
        }
        catch (QueueyConfigurationException ex)
        {
            await WriteErrorAsync(response, 400, "configuration", ex.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(EdgeLogEvents.LocalEndpointError, ex, "Local endpoint request failed.");
            await WriteErrorAsync(response, 500, "internal", "Unexpected error; see the daemon log.", ct).ConfigureAwait(false);
        }
        finally
        {
            try { response.Close(); } catch { /* client went away */ }
        }
    }

    private static DateTimeOffset? ParseOccurredAt(string? raw)
        => !string.IsNullOrWhiteSpace(raw)
           && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object body, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, QueueyJson.Options);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int status, string code, string message, CancellationToken ct)
        => WriteJsonAsync(response, status, new { error = new { code, message } }, ct);
}
