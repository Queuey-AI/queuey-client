using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>Default <see cref="IQueueyIngress"/> implementation over <see cref="QueueyHttpConnection"/>.</summary>
internal sealed class QueueyIngressClient : IQueueyIngress
{
    private const string JsonContentType = "application/json";
    private const string OctetStreamContentType = "application/octet-stream";

    private readonly QueueyHttpConnection _connection;
    private readonly IQueueyAuthenticator _authenticator;
    private readonly Uri _ingressBase;
    private readonly string? _tenantPublicId;
    private readonly string? _defaultSource;

    public QueueyIngressClient(
        QueueyHttpConnection connection,
        IQueueyAuthenticator authenticator,
        Uri ingressBase,
        string? tenantPublicId,
        string? defaultSource)
    {
        _connection = connection;
        _authenticator = authenticator;
        _ingressBase = ingressBase;
        _tenantPublicId = tenantPublicId;
        _defaultSource = defaultSource;
    }

    public Task<PublishResult> PublishAsync(string queueName, byte[] payload, PublishOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync(
            queueName,
            payload ?? Array.Empty<byte>(),
            options?.ContentType ?? OctetStreamContentType,
            sandbox: false,
            query: null,
            options?.EventType, options?.GroupKey, options?.IdempotencyKey, options?.Source,
            cancellationToken);

    public Task<PublishResult> PublishAsync<T>(string queueName, T payload, PublishOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync(
            queueName,
            Serialize(payload),
            options?.ContentType ?? JsonContentType,
            sandbox: false,
            query: null,
            options?.EventType, options?.GroupKey, options?.IdempotencyKey, options?.Source,
            cancellationToken);

    /// <summary>
    /// <see cref="PublishAsync(string, byte[], PublishOptions?, CancellationToken)"/> for a caller that must tell an event the
    /// ingress took without a receipt (a queue whose ingress answers 204) from one with: null for none. A 2xx whose body is
    /// not Queuey's receipt throws, rather than passing for a publish that worked.
    /// </summary>
    internal async Task<PublishResult?> PublishForReceiptAsync(string queueName, byte[] payload, PublishOptions? options, CancellationToken cancellationToken)
    {
        PublishResult? receipt = await SendCoreAsync(
            queueName,
            payload ?? Array.Empty<byte>(),
            options?.ContentType ?? OctetStreamContentType,
            sandbox: false,
            query: null,
            options?.EventType, options?.GroupKey, options?.IdempotencyKey, options?.Source,
            cancellationToken).ConfigureAwait(false);

        return Checked(receipt);
    }

    public Task<PublishResult> PublishSandboxAsync(string queueName, byte[] payload, SandboxPublishOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync(
            queueName,
            payload ?? Array.Empty<byte>(),
            options?.ContentType ?? OctetStreamContentType,
            sandbox: true,
            query: NullIfEmpty(options?.BuildQuery()),
            options?.EventType, options?.GroupKey, options?.IdempotencyKey, options?.Source,
            cancellationToken);

    public Task<PublishResult> PublishSandboxAsync<T>(string queueName, T payload, SandboxPublishOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync(
            queueName,
            Serialize(payload),
            options?.ContentType ?? JsonContentType,
            sandbox: true,
            query: NullIfEmpty(options?.BuildQuery()),
            options?.EventType, options?.GroupKey, options?.IdempotencyKey, options?.Source,
            cancellationToken);

    // Den offentlige veien (F2.7-re-review, 2026-10-06): et 204-svar, eller et tomt 2xx, er et event ingressen tok imot uten
    // kvittering. Før kastet den en rå JsonException etter at eventet var tatt imot, og en som prøvde igjen, laget et duplikat.
    // Nå gir den et resultat med tom EventId og QueuePublicId, som PublishResult dokumenterer. Et 2xx som ikke er Queuey sin
    // kvittering, kaster.
    private async Task<PublishResult> SendAsync(
        string queueName,
        byte[] body,
        string contentType,
        bool sandbox,
        string? query,
        string? eventType,
        string? groupKey,
        string? idempotencyKey,
        string? source,
        CancellationToken cancellationToken)
    {
        PublishResult? receipt = await SendCoreAsync(queueName, body, contentType, sandbox, query, eventType, groupKey, idempotencyKey,
            source, cancellationToken).ConfigureAwait(false);

        // Uten kvittering er tiden ukjent, som id-ene (herding før tag, review av #53): UtcNow var klientens klokke vist som
        // serverens mottakstid. Typen er ikke nullbar (en offentlig kontrakt), så den er default, som PublishResult dokumenterer.
        return Checked(receipt) ?? new PublishResult
        {
            QueuePublicId = string.Empty,
            EventId = string.Empty,
            ReceivedAtUtc = default,
            Mode = string.Empty,
        };
    }

    // JSON uten event-id er ikke Queuey sin kvittering, og hva som skjedde med eventet, er ukjent.
    private static PublishResult? Checked(PublishResult? receipt)
        => receipt is not null && string.IsNullOrWhiteSpace(receipt.EventId)
            ? throw new QueueyException(
                "The ingress answered with JSON that is not Queuey's receipt (it has no event id), so whether the event was taken is unknown.",
                errorCode: "unreadable_response")
            {
                SuggestedAction = "Check that the ingress base address points at Queuey's ingress, not at a proxy, and look the event up in Queuey before sending it again.",
            }
            : receipt;

    private async Task<PublishResult?> SendCoreAsync(
        string queueName,
        byte[] body,
        string contentType,
        bool sandbox,
        string? query,
        string? eventType,
        string? groupKey,
        string? idempotencyKey,
        string? source,
        CancellationToken cancellationToken)
    {
        // Et svar uten kropp er null her; de to veiene over skiller seg i hva de gir tilbake for det.
        if (string.IsNullOrWhiteSpace(queueName))
            throw new ArgumentException("A queue name is required.", nameof(queueName));
        if (string.IsNullOrWhiteSpace(_tenantPublicId))
            throw new QueueyConfigurationException("TenantPublicId is required to publish. Set QueueyOptions.TenantPublicId.");

        Uri uri = sandbox
            ? QueueyUri.Build(_ingressBase, query, "events", "sandbox", _tenantPublicId!, queueName)
            : QueueyUri.Build(_ingressBase, query, "events", _tenantPublicId!, queueName);

        string? resolvedSource = source ?? _defaultSource;

        void Configure(HttpRequestHeaders headers)
        {
            if (!string.IsNullOrEmpty(eventType)) QueueyHttpHeaders.Set(headers, QueueyHeaders.EventType, eventType!);
            if (!string.IsNullOrEmpty(groupKey)) QueueyHttpHeaders.Set(headers, QueueyHeaders.GroupKey, groupKey!);
            if (!string.IsNullOrEmpty(idempotencyKey)) QueueyHttpHeaders.Set(headers, QueueyHeaders.IdempotencyKey, idempotencyKey!);
            if (!string.IsNullOrEmpty(resolvedSource)) QueueyHttpHeaders.Set(headers, QueueyHeaders.Source, resolvedSource!);
        }

        try
        {
            return await _connection.SendForOptionalJsonAsync<PublishResult>(
                HttpMethod.Post, uri, body, contentType, _authenticator, Configure, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyNotFoundException ex) when (!QueueyName.IsValid(queueName))
        {
            // Publishing deliberately does NOT pre-validate the name: the server keeps routing queues
            // that predate its own name validator, and the SDK must not refuse a queue Queuey accepts.
            // But when the route 404s AND the name could never have been created, say so — that is the
            // typo case, and the bare "not found" sends people hunting in the console.
            // F2.7-review (2026-10-06): høyst tre tegn av navnet, og ikke forslaget, som er navnet med små bokstaver. Verdien
            // kan være en hemmelighet limt inn der kønavnet skulle stå (`queuey publish "$QUEUEY_API_KEY"`).
            throw new QueueyNotFoundException(
                $"{ex.Message} The queue name ('{QueueyName.Shown(queueName)}') also breaks Queuey's naming rules, so it is " +
                $"unlikely to exist: {QueueyName.Validate(queueName)}",
                ex.ErrorCode);
        }
    }

    private static byte[] Serialize<T>(T payload)
    {
        if (payload is null)
            throw new ArgumentNullException(nameof(payload), "The payload cannot be null.");
        return JsonSerializer.SerializeToUtf8Bytes(payload, QueueyJson.Options);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
