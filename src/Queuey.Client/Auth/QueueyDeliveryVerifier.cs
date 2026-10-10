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
/// string, the body (through the content hash header), the signing headers
/// Queuey generates, and — in v2, which this verifier requires — the event id
/// (<c>X-Queuey-Event-Id</c>) and the idempotency key (<c>Idempotency-Key</c>).
/// Those two are what a receiver deduplicates on, so a verified
/// <see cref="QueueyVerificationResult.EventId"/> and
/// <see cref="QueueyVerificationResult.IdempotencyKey"/> are safe to be
/// idempotent on. It does NOT cover other request headers, so never trust an
/// unsigned header as if verification vouched for it.</para>
///
/// <para><b>v2 is required.</b> A delivery without a v2 signature
/// (<c>X-Queuey-Signatures: v2=…</c>) is refused, also when it carries a valid
/// v1, so stripping v2 cannot downgrade it. For a Queuey that does not sign v2
/// yet, set <see cref="QueueyDeliveryVerifierOptions.AcceptV1"/>.</para>
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
    /// A verifier for the secret <see cref="QueueyOptions.UseEnvironmentVariables(Func{string, string?}?)"/> finds:
    /// <c>QUEUEY_DELIVERY_SECRET</c> from the environment, or in Development from <c>.env</c>, as
    /// <c>queuey credentials generate --write</c> writes it. For .NET configuration (user secrets), build it from
    /// <c>new QueueyOptions().UseSettings(key =&gt; configuration[key]).DeliverySecret</c>. It requires v2 as any verifier does;
    /// <see cref="QueueyDeliveryVerifierOptions.AcceptV1"/> in <paramref name="options"/> accepts v1 too.
    /// </summary>
    /// <exception cref="QueueyConfigurationException">No delivery secret is set.</exception>
    public static QueueyDeliveryVerifier FromEnvironment(QueueyDeliveryVerifierOptions? options = null)
        => new(new QueueyOptions().UseEnvironmentVariables().DeliverySecret is { Length: > 0 } secret
            ? secret
            : throw new QueueyConfigurationException(
                $"No delivery secret is set: {QueueyEnvironmentVariables.DeliverySecret} is not in the environment, nor in .env in Development.")
            {
                SuggestedAction = "Make one with `queuey credentials generate <name> --write .env` (or --write user-secrets), which also stores it in Queuey.",
            }, options);

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
        var signatureV1 = Trimmed(header(QueueyHeaders.Signature));
        var signaturesV2 = V2Signatures(header(QueueyHeaders.Signatures));
        // Leses for loggen ved en feil; i et gyldig resultat bare når v2 dekket dem (2026-10-10, Queuey #521).
        var eventId = Trimmed(header(QueueyHeaders.EventId));
        var idempotencyKey = Trimmed(header(QueueyHeaders.IdempotencyKey));

        if (keyId is null || timestamp is null || nonce is null || contentHash is null || (signaturesV2.Count == 0 && signatureV1 is null))
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.MissingHeaders, keyId, eventId);

        // Ingen nedgradering: uten v2 godtas en levering bare når mottakeren uttrykkelig tar v1 (AcceptV1). Er v2 der, avgjør den
        // alene, også om v1 er gyldig, så en fjernet eller endret v2 aldri faller tilbake til v1.
        if (signaturesV2.Count == 0 && !_options.AcceptV1)
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.MissingV2Signature, keyId, eventId);

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

        bool v2 = signaturesV2.Count > 0;
        if (v2)
        {
            var canonical = QueueyCanonicalRequest.BuildV2(
                method, requestUri, timestamp, nonce, contentHash.ToLowerInvariant(), eventId, idempotencyKey);
            var expected = QueueyHash.HmacSha256HexLower(_secretBytes, canonical);
            var matched = false;
            foreach (var candidate in signaturesV2)
                matched |= FixedTimeEqualsHex(candidate, expected);
            if (!matched)
                return QueueyVerificationResult.Fail(QueueyVerificationFailure.SignatureMismatch, keyId, eventId, signedAt);
        }
        else
        {
            var canonical = QueueyCanonicalRequest.Build(method, requestUri, timestamp, nonce, contentHash.ToLowerInvariant());
            var expected = QueueyHash.HmacSha256HexLower(_secretBytes, canonical);
            if (!FixedTimeEqualsHex(signatureV1!, expected))
                return QueueyVerificationResult.Fail(QueueyVerificationFailure.SignatureMismatch, keyId, eventId, signedAt);
        }

        // Replay last: it is the only check that can touch storage, and an
        // invalid delivery should never get to write a nonce.
        if (_options.NonceAlreadySeen is { } seen && seen(nonce))
            return QueueyVerificationResult.Fail(QueueyVerificationFailure.ReplayedNonce, keyId, eventId, signedAt);

        // Event-id-en og nøkkelen er verdt å stole på bare når signaturen dekket dem: v2. Med v1 er de ikke med.
        return v2
            ? QueueyVerificationResult.Ok(keyId, signedAt, nonce, 2, eventId, idempotencyKey, eventId)
            : QueueyVerificationResult.Ok(keyId, signedAt, nonce, 1, eventId: null, idempotencyKey: null, claimedEventId: eventId);
    }

    /// <summary>
    /// The <c>v2</c> values of <c>X-Queuey-Signatures</c>: split on <c>,</c>, each element on its first <c>=</c>, names compared
    /// without regard to case. Empty when the header is absent or has none.
    /// </summary>
    private static List<string> V2Signatures(string? header)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(header))
            return found;
        foreach (var element in header!.Split(','))
        {
            var at = element.IndexOf('=');
            if (at <= 0)
                continue;
            if (string.Equals(element.Substring(0, at).Trim(), "v2", StringComparison.OrdinalIgnoreCase)
                && element.Substring(at + 1).Trim() is { Length: > 0 } value)
                found.Add(value);
        }
        return found;
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

    /// <summary>
    /// Accept a delivery signed only with v1 (<c>X-Queuey-Signature</c>), for a Queuey that does not sign v2 yet. Off by
    /// default. With it on, anyone in the path can strip v2 and send v1 alone, so the protection is v1's whatever Queuey
    /// sends: v1 does NOT cover the event id or the idempotency key, and a delivery captured and replayed inside the timestamp
    /// window with another id or key still verifies. A verified v1 delivery therefore leaves
    /// <see cref="QueueyVerificationResult.EventId"/> and <see cref="QueueyVerificationResult.IdempotencyKey"/> null. A
    /// delivery that carries v2 is verified by v2 alone, also with this set: a v2 that does not hold never falls back to v1.
    /// Turn it off once your deliveries carry v2.
    /// </summary>
    public bool AcceptV1 { get; set; }

    /// <summary>The clock, injectable so timestamp handling is testable. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
}

/// <summary>Why a delivery did not verify. For your logs — never return it to the caller, which would tell an attacker which check they failed.</summary>
public enum QueueyVerificationFailure
{
    /// <summary>Verification succeeded.</summary>
    None = 0,

    /// <summary>
    /// A signing header was absent: <c>X-Queuey-Key-Id</c>, <c>X-Queuey-Timestamp</c>, <c>X-Queuey-Nonce</c>,
    /// <c>X-Queuey-Content-SHA256</c>, or every signature (neither <c>X-Queuey-Signatures</c> nor <c>X-Queuey-Signature</c>).
    /// Usually means the request did not come from Queuey at all. A delivery with v1 alone is <see cref="MissingV2Signature"/>.
    /// </summary>
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

    /// <summary>
    /// The delivery had no v2 signature (<c>X-Queuey-Signatures: v2=…</c>), and v1 is not accepted
    /// (<see cref="QueueyDeliveryVerifierOptions.AcceptV1"/>). A Queuey that does not sign v2 yet, or v2 stripped in transit.
    /// </summary>
    MissingV2Signature,
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
        string? nonce,
        int signatureVersion = 0,
        string? idempotencyKey = null,
        string? claimedEventId = null)
    {
        IsValid = isValid;
        ClaimedEventId = claimedEventId;
        Failure = failure;
        KeyId = keyId;
        EventId = eventId;
        SignedAtUtc = signedAt;
        Nonce = nonce;
        SignatureVersion = signatureVersion;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>True when the signature, the body hash and the timestamp all held.</summary>
    public bool IsValid { get; }

    /// <summary>Why it failed, or <see cref="QueueyVerificationFailure.None"/>. Log it; do not return it.</summary>
    public QueueyVerificationFailure Failure { get; }

    /// <summary>The key id the delivery claimed, when it carried one. Trustworthy only once <see cref="IsValid"/> is true.</summary>
    public string? KeyId { get; }

    /// <summary>
    /// The Queuey event id (<c>X-Queuey-Event-Id</c>), on a valid result only when the v2 signature covered it. This is the
    /// value to be idempotent on: the same event redelivered after a timeout carries the same id, and processing it twice is
    /// the failure mode retries create. Null on a delivery verified by v1 (<see cref="QueueyDeliveryVerifierOptions.AcceptV1"/>),
    /// and on a failed result: <see cref="ClaimedEventId"/> has the header as it came, for your logs.
    /// </summary>
    public string? EventId { get; }

    /// <summary>
    /// The <c>X-Queuey-Event-Id</c> header as the request carried it, verified or not. For your logs only; never deduplicate on
    /// it. <see cref="EventId"/> is the one to trust.
    /// </summary>
    public string? ClaimedEventId { get; }

    /// <summary>
    /// The delivery's idempotency key (<c>Idempotency-Key</c>), only when the v2 signature covered it; null otherwise. A
    /// patched resend keeps the event id and gets a key of its own, so dedupe on the one your handler means.
    /// </summary>
    public string? IdempotencyKey { get; }

    /// <summary>The signature version that verified: 2, or 1 with <see cref="QueueyDeliveryVerifierOptions.AcceptV1"/>; 0 on a failure.</summary>
    public int SignatureVersion { get; }

    /// <summary>When Queuey signed the delivery.</summary>
    public DateTimeOffset? SignedAtUtc { get; }

    /// <summary>The delivery's nonce. Remember it if you are guarding against replays.</summary>
    public string? Nonce { get; }

    internal static QueueyVerificationResult Ok(
        string keyId, DateTimeOffset signedAt, string nonce, int signatureVersion, string? eventId, string? idempotencyKey,
        string? claimedEventId)
        => new(true, QueueyVerificationFailure.None, keyId, eventId, signedAt, nonce, signatureVersion, idempotencyKey, claimedEventId);

    // Security-review av #74 (KAN-2): ved en feil er EventId null; den rå verdien står bare i ClaimedEventId.
    internal static QueueyVerificationResult Fail(
        QueueyVerificationFailure failure,
        string? keyId = null,
        string? eventId = null,
        DateTimeOffset? signedAt = null)
        => new(false, failure, keyId, eventId: null, signedAt, null, claimedEventId: eventId);
}
