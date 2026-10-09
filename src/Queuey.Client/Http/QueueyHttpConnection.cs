using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>
/// Thin transport over <see cref="HttpClient"/>: builds a request with a raw body, applies the given
/// authenticator, sends, maps non-success responses to typed exceptions, and deserializes JSON.
/// </summary>
internal sealed class QueueyHttpConnection
{
    private readonly HttpClient _client;
    private readonly Action<HttpResponseMessage>? _observe;

    public QueueyHttpConnection(HttpClient client)
        : this(client, observe: null)
    {
    }

    /// <summary>
    /// A connection that shows every response to <paramref name="observe"/> before it is read or mapped to an error: the
    /// control plane reads Queuey's warning headers there (<c>X-Queuey-Warning</c>), and the step a dry run added to a plan.
    /// </summary>
    public QueueyHttpConnection(HttpClient client, Action<HttpResponseMessage>? observe)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _observe = observe;
    }

    /// <summary>Sends a request and deserializes the (2xx) JSON response into <typeparamref name="T"/>.</summary>
    public async Task<T> SendForJsonAsync<T>(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        string? contentType,
        IQueueyAuthenticator authenticator,
        Action<HttpRequestHeaders>? configureHeaders,
        CancellationToken cancellationToken)
    {
        byte[] payload = body ?? Array.Empty<byte>();

        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new ByteArrayContent(payload),
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            request.Content.Headers.ContentType = ContentType(contentType!);

        configureHeaders?.Invoke(request.Headers);

        // Authentication runs last: HMAC hashes the body and signs the method/path/query/timestamp/nonce.
        await authenticator.AuthenticateAsync(request, payload, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        _observe?.Invoke(response);

        if (!response.IsSuccessStatusCode)
            throw await QueueyErrorMapper.CreateAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request and maps a non-2xx response to a typed exception, ignoring any success body (e.g. 204).</summary>
    public async Task SendAsync(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        string? contentType,
        IQueueyAuthenticator authenticator,
        Action<HttpRequestHeaders>? configureHeaders,
        CancellationToken cancellationToken)
    {
        byte[] payload = body ?? Array.Empty<byte>();

        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new ByteArrayContent(payload),
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            request.Content.Headers.ContentType = ContentType(contentType!);

        configureHeaders?.Invoke(request.Headers);
        await authenticator.AuthenticateAsync(request, payload, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        _observe?.Invoke(response);

        if (!response.IsSuccessStatusCode)
            throw await QueueyErrorMapper.CreateAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// As <see cref="SendForJsonAsync{T}"/>, for an answer that may come without a body: a 204, or a 2xx with an empty
    /// body, is null. A 2xx whose body is not JSON throws, since it is not Queuey's answer, and whether the request was
    /// taken is unknown.
    /// </summary>
    // F2.7-review (2026-10-06): en ingress som svarer 204, sender ingen kvittering, og SendForJsonAsync kastet JsonException.
    // Den som publiserer, må skille det fra et 2xx med HTML fra en proxy, som ikke er et svar fra Queuey.
    public async Task<T?> SendForOptionalJsonAsync<T>(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        string? contentType,
        IQueueyAuthenticator authenticator,
        Action<HttpRequestHeaders>? configureHeaders,
        CancellationToken cancellationToken)
        where T : class
    {
        byte[] payload = body ?? Array.Empty<byte>();

        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new ByteArrayContent(payload),
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            request.Content.Headers.ContentType = ContentType(contentType!);

        configureHeaders?.Invoke(request.Headers);
        await authenticator.AuthenticateAsync(request, payload, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        _observe?.Invoke(response);

        if (!response.IsSuccessStatusCode)
            throw await QueueyErrorMapper.CreateAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content is null)
            return null;

#if NET8_0_OR_GREATER
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
        byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
        if (IsBlank(bytes))
            return null;

        int status = (int)response.StatusCode;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, QueueyJson.Options);
        }
        catch (JsonException)
        {
            // Kroppen vises ikke: den kan være hva som helst en proxy svarte med.
            throw new QueueyException(
                FormattableString.Invariant($"Queuey's address answered {status} with a body that is not JSON, so it is not Queuey's answer, and whether the request was taken is unknown."),
                status, "unreadable_response")
            {
                SuggestedAction = "Check that the base address points at Queuey, not at a proxy or a login page, and look the request up in Queuey before sending it again.",
            };
        }
    }

    // Verdien vises ikke i feilen: MediaTypeHeaderValue.Parse gjentok den, og den kommer fra den som kaller.
    private static MediaTypeHeaderValue ContentType(string contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed) && parsed is not null
            ? parsed
            : throw new ArgumentException("The content type is not a media type, such as application/json. Its value is not shown.", nameof(contentType));

    private static bool IsBlank(byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        }

        return true;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        T? result = await JsonSerializer
            .DeserializeAsync<T>(stream, QueueyJson.Options, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            throw new QueueyException("The Queuey API returned an empty or null response body.", (int)response.StatusCode);

        return result;
    }
}
