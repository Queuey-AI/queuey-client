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
            return await _connection.SendForJsonAsync<PublishResult>(
                HttpMethod.Post, uri, body, contentType, _authenticator, Configure, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyNotFoundException ex) when (!QueueyName.IsValid(queueName))
        {
            // Publishing deliberately does NOT pre-validate the name: the server keeps routing queues
            // that predate its own name validator, and the SDK must not refuse a queue Queuey accepts.
            // But when the route 404s AND the name could never have been created, say so — that is the
            // typo case, and the bare "not found" sends people hunting in the console.
            throw new QueueyNotFoundException(
                $"{ex.Message} The queue name '{queueName}' also breaks Queuey's naming rules, so it is " +
                $"unlikely to exist: {QueueyName.Hint(queueName)}",
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
