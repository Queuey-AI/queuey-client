using System;
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

    public QueueyHttpConnection(HttpClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

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
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        configureHeaders?.Invoke(request.Headers);

        // Authentication runs last: HMAC hashes the body and signs the method/path/query/timestamp/nonce.
        await authenticator.AuthenticateAsync(request, payload, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

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
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        configureHeaders?.Invoke(request.Headers);
        await authenticator.AuthenticateAsync(request, payload, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await QueueyErrorMapper.CreateAsync(response, cancellationToken).ConfigureAwait(false);
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
