using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Queuey.Client;

/// <summary>
/// Verifies that an inbound webhook really came from Queuey.
///
/// <para>This is the receiving half of <see cref="HmacRequestSigner"/>: the
/// same canonical string, built by the same <see cref="QueueyCanonicalRequest"/>,
/// so the two sides cannot drift. Hand it the request exactly as it arrived —
/// method, URI, a way to read headers, and the <b>raw body bytes</b> — and it
/// answers whether the signature, the body hash and the timestamp all hold.</para>
///
/// <code>
/// var verifier = new QueueyDeliveryVerifier(signingSecret);
/// var result = verifier.Verify(Request.Method, uri, n => Request.Headers[n], rawBody);
/// if (!result.IsValid) return Results.Unauthorized();
/// </code>
///
/// <para><b>The raw body is not negotiable.</b> The signature covers a hash of
/// the exact bytes Queuey sent. A body that has been deserialized and
/// re-serialized is a different byte sequence even when it is the same JSON, so
/// verification will fail. In ASP.NET Core that means reading the body before
/// model binding (<c>Request.EnableBuffering()</c> then reading the stream), or
/// taking the body as <c>byte[]</c>/<c>string</c> rather than a typed model.</para>
///
/// <para><b>What the signature covers:</b> the HTTP method, the path, the query
/// string, the body (through the content hash header) and the signing headers
/// Queuey generates. It deliberately does NOT cover other request headers, so
/// never trust an unsigned header as if verification vouched for it.</para>
/// </summary>
public sealed class QueueyDeliveryVerifier
{
    private readonly byte[] _secretBytes;
    private readonly QueueyDeliveryVerifierOptions _options;

    /// <summary>Creates a verifier for one signing secret.</summary>
    /// <param name="secret">The signing secret, as issued with the key id. Used as raw UTF-8 bytes.</param>
    /// <param name="options">Clock skew, replay hook and clock. Defaults are the ones Queuey signs with.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is null or whitespace.</exception>
    public QueueyDeliveryVerifier(string secret, QueueyDeliveryVerifierOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("A signing secret is required.", nameof(secret));

        _secretBytes = Encoding.UTF8.GetBytes(secret);
        _options = options ?? new QueueyDeliveryVerifierOptions();
    }

    /// <summary>
    /// Reads the key id off a request without verifying anything. Use it to look
    /// up which secret to construct the verifier with when a receiver accepts
    /// deliveries signed by more than one key (during a key rotation, say).
    /// A key id is an untrusted lookup hint until <see cref="Verify"/> passes.
    /// </summary>
    public static string? ReadKeyId(Func<string, string?> header)
    {
        if (header is null) throw new ArgumentNullException(nameof(header));
        var keyId = header(QueueyHeaders.KeyId);
        return string.IsNullOrWhiteSpace(keyId) ? null : keyId!.Trim();
    }

    /// <summary>
    /// Verifies one delivery. Never throws on a bad request: an unverifiable
    /// delivery comes back as a result with <see cref="QueueyVerificationResult.IsValid"/>
    /// false and a reason meant for your logs.
    /// </summary>
    /// <param name="method">The HTTP method exactly as received.</param>
    /// <param name="requestUri">The request URI. Only its path and query take part in the signature.</param>
    /// <param name="header">Reads one request header by name; return null when absent.</param>
    /// <param name="body">The RAW body bytes as received. An empty body is valid; null is treated as empty.</param>
    public QueueyVerificationResult Verify(
        string method,
        Uri requestUri,
        Func<string, string?> header,
        byte[]? body)
    {
        if (requestUri is null) throw new ArgumentNullException(nameof(requestUri));
        if (header is null) throw new ArgumentNullException(nameof(header));

        var keyId = Trimmed(header(QueueyHeaders.KeyId));
        var timestamp = Trimmed(header(QueueyHeaders.Timestamp));
        var nonce = Trimmed(header(QueueyHeaders.Nonce));
        var contentHash = Trimmed(header(QueueyHeaders.ContentSha256));
        var signature = Trimmed(header(QueueyHeaders.Signature));
        var eventId = Trimmed(header(QueueyHeaders.EventId));

        if (keyId is null || timestamp is null || nonce is null || contentHash is null || signature is null)
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.MissingHeaders, keyId, eventId);

        // Timestamp first: it is the cheapest check, and it turns a captured
        // delivery replayed tomorrow into a rejection before we spend a hash on it.
        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.MalformedTimestamp, keyId, eventId);

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var age = _options.Clock() - signedAt;
        if (age > _options.ClockSkew || age < -_options.ClockSkew)
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.TimestampOutsideWindow, keyId, eventId, signedAt);

        // The body hash must be checked SEPARATELY from the signature. The
        // signature covers the hash HEADER, not the bytes — so a body swapped
        // without touching the header would still carry a valid signature.
        // This is the check that makes the signature mean what it looks like.
        var actualHash = QueueyHash.Sha256HexLower(body ?? Array.Empty<byte>());
        if (!FixedTimeEqualsHex(contentHash, actualHash))
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.BodyHashMismatch, keyId, eventId, signedAt);

        var canonical = QueueyCanonicalRequest.Build(
            method,
            requestUri,
            timestamp,
            nonce,
            contentHash.ToLowerInvariant());

        var expected = QueueyHash.HmacSha256HexLower(_secretBytes, canonical);
        if (!FixedTimeEqualsHex(signature, expected))
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.SignatureMismatch, keyId, eventId, signedAt);

        // Replay last: it is the only check that can touch storage, and an
        // invalid delivery should never get to write a nonce.
        if (_options.NonceAlreadySeen is { } seen && seen(nonce))
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.ReplayedNonce, keyId, eventId, signedAt);

        return QueueyVerificationResult.Ok(keyId, eventId, signedAt, nonce);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    /// <summary>
    /// Constant-time comparison of two lowercase-hex strings. Parsing to bytes
    /// first makes the comparison case-insensitive and keeps it off the
    /// character-by-character early exit that string equality would take.
    /// </summary>
    private static bool FixedTimeEqualsHex(string provided, string expected)
    {
        if (!TryParseHex(provided, out var providedBytes) || !TryParseHex(expected, out var expectedBytes))
            return false;

        if (providedBytes.Length != expectedBytes.Length)
            return false;

#if NET8_0_OR_GREATER
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
#else
        var diff = 0;
        for (var i = 0; i < providedBytes.Length; i++)
            diff |= providedBytes[i] ^ expectedBytes[i];
        return diff == 0;
#endif
    }

    private static bool TryParseHex(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value) || value.Length % 2 != 0)
            return false;

        var parsed = new byte[value.Length / 2];
        for (var i = 0; i < parsed.Length; i++)
        {
            var high = HexValue(value[i * 2]);
            var low = HexValue(value[(i * 2) + 1]);
            if (high < 0 || low < 0)
                return false;

            parsed[i] = (byte)((high << 4) | low);
        }

        bytes = parsed;
        return true;
    }

    private static int HexValue(char c)
        => c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : -1;
}

/// <summary>Knobs for <see cref="QueueyDeliveryVerifier"/>. The defaults match what Queuey signs with.</summary>
public sealed class QueueyDeliveryVerifierOptions
{
    /// <summary>
    /// How far the delivery's timestamp may sit from your clock, in either
    /// direction. Default five minutes, the same window Queuey's own ingress
    /// allows. Widen it only if your clocks genuinely drift; every extra minute
    /// is a minute longer a captured delivery can be replayed.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Optional replay guard: given a nonce, return true when you have seen it
    /// before. Queuey's nonces are unique per delivery attempt, so remembering
    /// them for a little longer than <see cref="ClockSkew"/> closes the window
    /// the timestamp check leaves open. Leave it null and the timestamp window
    /// is your only replay protection, which is weaker but not nothing.
    /// </summary>
    public Func<string, bool>? NonceAlreadySeen { get; set; }

    /// <summary>The clock, injectable so timestamp handling is testable. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
}

/// <summary>Why a delivery did not verify. For your logs — never return it to the caller, which would tell an attacker which check they failed.</summary>
public enum QueueyVerificationFailure
{
    /// <summary>Verification succeeded.</summary>
    None = 0,

    /// <summary>One or more of the five signing headers was absent. Usually means the request did not come from Queuey at all.</summary>
    MissingHeaders,

    /// <summary>The timestamp header was not an integer number of Unix seconds.</summary>
    MalformedTimestamp,

    /// <summary>The delivery was signed too long ago, or too far in the future. A replayed capture, or a clock that has drifted.</summary>
    TimestampOutsideWindow,

    /// <summary>The body does not hash to the value in the content hash header. The body was altered in transit, or it was re-serialized before verification.</summary>
    BodyHashMismatch,

    /// <summary>The signature did not match. Wrong secret, wrong key id, or a forged request.</summary>
    SignatureMismatch,

    /// <summary>Your replay guard had seen this nonce before.</summary>
    ReplayedNonce,
}

/// <summary>The outcome of verifying one delivery.</summary>
public readonly struct QueueyVerificationResult
{
    private QueueyVerificationResult(
        bool isValid,
        QueueyVerificationFailure failure,
        string? keyId,
        string? eventId,
        DateTimeOffset? signedAt,
        string? nonce)
    {
        IsValid = isValid;
        Failure = failure;
        KeyId = keyId;
        EventId = eventId;
        SignedAtUtc = signedAt;
        Nonce = nonce;
    }

    /// <summary>True when the signature, the body hash and the timestamp all held.</summary>
    public bool IsValid { get; }

    /// <summary>Why it failed, or <see cref="QueueyVerificationFailure.None"/>. Log it; do not return it.</summary>
    public QueueyVerificationFailure Failure { get; }

    /// <summary>The key id the delivery claimed, when it carried one. Trustworthy only once <see cref="IsValid"/> is true.</summary>
    public string? KeyId { get; }

    /// <summary>
    /// The Queuey event id, when the delivery carried one. This is the value to
    /// be idempotent on: the same event redelivered after a timeout carries the
    /// same id, and processing it twice is the failure mode retries create.
    /// </summary>
    public string? EventId { get; }

    /// <summary>When Queuey signed the delivery.</summary>
    public DateTimeOffset? SignedAtUtc { get; }

    /// <summary>The delivery's nonce. Remember it if you are guarding against replays.</summary>
    public string? Nonce { get; }

    internal static QueueyVerificationResult Ok(string keyId, string? eventId, DateTimeOffset signedAt, string nonce)
        => new(true, QueueyVerificationFailure.None, keyId, eventId, signedAt, nonce);

    internal static QueueyVerificationResult Fail(
        QueueyVerificationFailure failure,
        string? keyId = null,
        string? eventId = null,
        DateTimeOffset? signedAt = null)
        => new(false, failure, keyId, eventId, signedAt, null);
}
