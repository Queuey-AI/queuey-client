using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>
/// Authenticates ingress requests with an HMAC-SHA256 signature, writing the five Queuey signing
/// headers (<c>X-Queuey-Key-Id</c>, <c>-Timestamp</c>, <c>-Nonce</c>, <c>-Content-SHA256</c>,
/// <c>-Signature</c>). This is the alternative to <see cref="ApiKeyAuthenticator"/> for ingress.
/// </summary>
/// <remarks>
/// The signature is <c>lowercase-hex HMAC-SHA256(canonical, secret)</c> where the secret is used as
/// raw UTF-8 bytes and the canonical string is produced by <see cref="QueueyCanonicalRequest"/>.
/// The clock and nonce factory are injectable for deterministic testing (golden vectors).
/// </remarks>
public sealed class HmacRequestSigner : IQueueyAuthenticator
{
    private readonly string _keyId;
    private readonly byte[] _secretBytes;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _nonceFactory;

    /// <summary>Creates an HMAC signer.</summary>
    /// <param name="keyId">The signing key id sent as <c>X-Queuey-Key-Id</c>.</param>
    /// <param name="secret">The signing secret (used as raw UTF-8 bytes).</param>
    /// <param name="clock">Optional UTC clock; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="nonceFactory">Optional nonce generator; defaults to a fresh 32-char hex GUID.</param>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> or <paramref name="secret"/> is null/whitespace.</exception>
    public HmacRequestSigner(
        string keyId,
        string secret,
        Func<DateTimeOffset>? clock = null,
        Func<string>? nonceFactory = null)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("A signing key id is required.", nameof(keyId));
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("A signing secret is required.", nameof(secret));

        _keyId = keyId.Trim();
        _secretBytes = Encoding.UTF8.GetBytes(secret);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nonceFactory = nonceFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    public Task AuthenticateAsync(HttpRequestMessage request, byte[] body, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.RequestUri is null)
            throw new InvalidOperationException("The request URI must be set before signing.");

        body ??= Array.Empty<byte>();

        string contentSha256 = QueueyHash.Sha256HexLower(body);
        string timestamp = _clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = _nonceFactory();
        if (string.IsNullOrWhiteSpace(nonce))
            throw new InvalidOperationException("The nonce factory produced an empty nonce.");

        string canonical = QueueyCanonicalRequest.Build(
            request.Method.Method, request.RequestUri, timestamp, nonce, contentSha256);
        string signature = QueueyHash.HmacSha256HexLower(_secretBytes, canonical);

        var headers = request.Headers;
        QueueyHttpHeaders.Set(headers, QueueyHeaders.KeyId, _keyId);
        QueueyHttpHeaders.Set(headers, QueueyHeaders.Timestamp, timestamp);
        QueueyHttpHeaders.Set(headers, QueueyHeaders.Nonce, nonce);
        QueueyHttpHeaders.Set(headers, QueueyHeaders.ContentSha256, contentSha256);
        QueueyHttpHeaders.Set(headers, QueueyHeaders.Signature, signature);

        return Task.CompletedTask;
    }
}
