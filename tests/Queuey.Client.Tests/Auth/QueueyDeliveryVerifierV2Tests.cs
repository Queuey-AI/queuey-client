using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Queuey.Client;

namespace Queuey.Client.Tests;

/// <summary>
/// Signatur v2 på leveranser (Queuey #521, 2026-10-10): de seks linjene i v1, så event-id-en og idempotensnøkkelen, i
/// <c>X-Queuey-Signatures</c> som <c>v2=&lt;hex&gt;</c>. Verifikatoren krever v2 som standard og faller aldri tilbake til v1 når v2
/// mangler eller ikke holder. Signaturene her regnes ut etter kontrakten, ikke med verifikatorens egen kode for v2.
/// </summary>
public class QueueyDeliveryVerifierV2Tests
{
    private const string Secret = "whsec-test-secret";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Url = new("https://shop.test/warehouse/order-created?b=2&a=1");
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":\"10042\"}");

    private static QueueyDeliveryVerifier Verifier(bool acceptV1 = false)
        => new(Secret, new QueueyDeliveryVerifierOptions { Clock = () => Now, AcceptV1 = acceptV1 });

    /// <summary>A delivery as Queuey #521 signs it: v1 and v2 over the same timestamp, nonce and body hash.</summary>
    private static Dictionary<string, string?> Delivery(string? eventId = "evt_01J9", string? idempotencyKey = "order-10042")
    {
        string timestamp = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = "7f3c9a1e2b4d4c6f8a0b1c2d3e4f5a6b";
        string contentSha = Convert.ToHexString(SHA256.HashData(Body)).ToLowerInvariant();
        string v1 = QueueyCanonicalRequest.Build("POST", Url, timestamp, nonce, contentSha);
        // Kontrakten for v2, skrevet ut her: sjuende og åttende linje, trimmet, tomme når de mangler.
        string v2 = v1 + "\n" + (eventId ?? "").Trim() + "\n" + (idempotencyKey ?? "").Trim();

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [QueueyHeaders.KeyId] = "warehouse-delivery",
            [QueueyHeaders.Timestamp] = timestamp,
            [QueueyHeaders.Nonce] = nonce,
            [QueueyHeaders.ContentSha256] = contentSha,
            [QueueyHeaders.Signature] = Hmac(v1),
            [QueueyHeaders.Signatures] = "v2=" + Hmac(v2),
        };
        if (eventId is not null) headers[QueueyHeaders.EventId] = eventId;
        if (idempotencyKey is not null) headers[QueueyHeaders.IdempotencyKey] = idempotencyKey;
        return headers;
    }

    private static string Hmac(string canonical)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static QueueyVerificationResult Verify(Dictionary<string, string?> headers, bool acceptV1 = false)
        => Verifier(acceptV1).Verify("POST", Url, n => headers.TryGetValue(n, out var v) ? v : null, Body);

    [Fact]
    public void A_v2_delivery_verifies_and_gives_the_event_id_and_the_idempotency_key()
    {
        QueueyVerificationResult result = Verify(Delivery());

        Assert.True(result.IsValid, result.Failure.ToString());
        Assert.Equal(2, result.SignatureVersion);
        Assert.Equal("evt_01J9", result.EventId);
        Assert.Equal("order-10042", result.IdempotencyKey);
    }

    [Theory]
    [InlineData("v2=SIG")]
    [InlineData("v1=abc, v2=SIG")]            // flere versjoner, mellomrom
    [InlineData("V2=SIG,v3=ffff")]             // navnet uten hensyn til store og små bokstaver
    public void The_v2_value_is_found_in_the_list(string shape)
    {
        var headers = Delivery();
        string sig = headers[QueueyHeaders.Signatures]!.Substring(3);
        headers[QueueyHeaders.Signatures] = shape.Replace("SIG", sig);

        Assert.True(Verify(headers).IsValid);
    }

    [Fact]
    public void A_changed_event_id_is_refused()
    {
        var headers = Delivery();
        headers[QueueyHeaders.EventId] = "evt_OTHER";

        QueueyVerificationResult result = Verify(headers, acceptV1: true);

        Assert.False(result.IsValid);
        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, result.Failure);
    }

    [Fact]
    public void A_changed_idempotency_key_is_refused()
    {
        var headers = Delivery();
        headers[QueueyHeaders.IdempotencyKey] = "order-99999";

        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, Verify(headers, acceptV1: true).Failure);
    }

    [Fact]
    public void Without_v2_a_delivery_is_refused_by_default_and_accepted_with_accept_v1_without_its_ids()
    {
        var headers = Delivery();
        headers.Remove(QueueyHeaders.Signatures);

        QueueyVerificationResult strict = Verify(headers);
        QueueyVerificationResult lenient = Verify(headers, acceptV1: true);

        Assert.Equal(QueueyVerificationFailure.MissingV2Signature, strict.Failure);
        Assert.True(lenient.IsValid);
        Assert.Equal(1, lenient.SignatureVersion);
        Assert.Null(lenient.EventId);
        Assert.Null(lenient.IdempotencyKey);
    }

    [Fact]
    public void A_downgrade_with_v2_stripped_and_the_id_swapped_is_refused_by_default()
    {
        // Angriperen tar v2 bort og bytter event-id-en; v1 er fortsatt gyldig, for v1 dekker ikke id-en.
        var headers = Delivery();
        headers.Remove(QueueyHeaders.Signatures);
        headers[QueueyHeaders.EventId] = "evt_OTHER";

        Assert.Equal(QueueyVerificationFailure.MissingV2Signature, Verify(headers).Failure);
        // Med AcceptV1 godtas den, men id-en gis ikke tilbake: ingen dedupe kan lures av den.
        QueueyVerificationResult lenient = Verify(headers, acceptV1: true);
        Assert.Null(lenient.EventId);
    }

    [Fact]
    public void A_v2_that_does_not_hold_never_falls_back_to_v1_even_with_accept_v1()
    {
        var headers = Delivery();
        headers[QueueyHeaders.Signatures] = "v2=" + new string('0', 64);

        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, Verify(headers, acceptV1: true).Failure);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("evt_01J9", null)]
    [InlineData(null, "order-10042")]
    public void Missing_fields_are_signed_as_empty_lines(string? eventId, string? idempotencyKey)
    {
        QueueyVerificationResult result = Verify(Delivery(eventId, idempotencyKey));

        Assert.True(result.IsValid, result.Failure.ToString());
        Assert.Equal(eventId, result.EventId);
        Assert.Equal(idempotencyKey, result.IdempotencyKey);
    }

    [Fact]
    public void An_empty_field_cannot_be_filled_in_afterwards()
    {
        // Signert uten nøkkel: en nøkkel lagt til underveis gir en annen åttende linje.
        var headers = Delivery(idempotencyKey: null);
        headers[QueueyHeaders.IdempotencyKey] = "order-10042";

        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, Verify(headers).Failure);
    }

    [Fact]
    public void Surrounding_whitespace_on_the_ids_is_trimmed_as_queuey_signs_them()
    {
        var headers = Delivery();
        headers[QueueyHeaders.EventId] = "  evt_01J9 ";
        headers[QueueyHeaders.IdempotencyKey] = " order-10042  ";

        QueueyVerificationResult result = Verify(headers);

        Assert.True(result.IsValid, result.Failure.ToString());
        Assert.Equal("evt_01J9", result.EventId);
    }
}
