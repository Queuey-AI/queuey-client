using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>
/// Authenticates ingress requests with an API key sent as the <c>X-Api-Key</c> header.
/// The key (<c>qak_&lt;keyId&gt;.&lt;secret&gt;</c>) is opaque to the SDK and forwarded verbatim.
/// </summary>
public sealed class ApiKeyAuthenticator : IQueueyAuthenticator
{
    private readonly string _apiKey;

    /// <summary>Creates an authenticator for the given API key.</summary>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is null or whitespace.</exception>
    public ApiKeyAuthenticator(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An API key is required.", nameof(apiKey));

        _apiKey = apiKey.Trim();
    }

    /// <inheritdoc />
    public Task AuthenticateAsync(HttpRequestMessage request, byte[] body, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        QueueyHttpHeaders.Set(request.Headers, QueueyHeaders.ApiKey, _apiKey);
        return Task.CompletedTask;
    }
}
