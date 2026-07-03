using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>
/// Applies authentication to an outgoing request just before it is sent. Implementations
/// add the credential headers required by Queuey — the API key (<c>X-Api-Key</c>) or the
/// HMAC signature set — given the request and its already-materialized body bytes.
/// </summary>
public interface IQueueyAuthenticator
{
    /// <summary>
    /// Mutates <paramref name="request"/> in place to carry the required auth headers.
    /// </summary>
    /// <param name="request">The request to authenticate; its method, path, and query are read for signing.</param>
    /// <param name="body">The raw request body bytes (empty for bodyless requests), used for content hashing.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task AuthenticateAsync(HttpRequestMessage request, byte[] body, CancellationToken cancellationToken = default);
}
