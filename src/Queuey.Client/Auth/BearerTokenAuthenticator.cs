using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>
/// Authenticates control-plane (API host) requests with an OAuth access token sent as <c>Authorization: Bearer</c>, such as
/// the one <c>queuey login</c> gets. The token is opaque to the SDK and asked for before each request, so a provider that
/// renews it keeps a long-running client working. The ingress does not take it: publishing needs an API key or signing.
/// </summary>
public sealed class BearerTokenAuthenticator : IQueueyAuthenticator
{
    private readonly Func<CancellationToken, Task<string>> _token;

    /// <summary>Creates an authenticator that sends the token <paramref name="token"/> returns for each request.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
    public BearerTokenAuthenticator(Func<CancellationToken, Task<string>> token)
        => _token = token ?? throw new ArgumentNullException(nameof(token));

    /// <inheritdoc />
    public async Task AuthenticateAsync(HttpRequestMessage request, byte[] body, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        string token = await _token(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new QueueyConfigurationException("The access token provider returned no token.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }
}
