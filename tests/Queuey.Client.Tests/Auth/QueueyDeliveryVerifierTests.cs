using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Queuey.Client;

namespace Queuey.Client.Tests;

/// <summary>
/// The receiving half of Queuey's HMAC contract. The load-bearing test is the
/// first one: the verifier accepts the SAME golden vectors the backend's
/// reference signer produced, so compatibility is proven against the server
/// rather than against our own signer.
///
/// The rest pin the checks a hand-written verifier silently gets wrong — the
/// body hash as a check separate from the signature, the timestamp window in
/// both directions, and constant-time comparison that still accepts uppercase
/// hex.
/// </summary>
public class QueueyDeliveryVerifierTests
{
    private sealed record VectorDoc(Vector[] Vectors);

    private sealed record Vector(
        string Name,
        string Method,
        string Url,
        string BodyUtf8,
        string? ContentType,
        string KeyId,
        string Secret,
        long Timestamp,
        string Nonce,
        string ExpectedContentSha256,
        string ExpectedSignature);

    private static readonly VectorDoc Doc = Load();

    public static IEnumerable<object[]> VectorNames() => Doc.Vectors.Select(v => new object[] { v.Name });

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Verifier_accepts_every_signature_the_backend_reference_signer_produced(string name)
    {
        var v = Doc.Vectors.Single(x => x.Name == name);

        var result = VerifierFor(v).Verify(
            v.Method,
            new Uri(v.Url),
            HeadersFor(v),
            Encoding.UTF8.GetBytes(v.BodyUtf8));

        Assert.True(result.IsValid, $"vector '{name}' failed with {result.Failure}");
        Assert.Equal(QueueyVerificationFailure.None, result.Failure);
        Assert.Equal(v.KeyId, result.KeyId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(v.Timestamp), result.SignedAtUtc);
        Assert.Equal(v.Nonce, result.Nonce);
    }

    [Fact]
    public void A_body_altered_in_transit_fails_on_the_hash_before_the_signature()
    {
        var v = First();

        var result = VerifierFor(v).Verify(
            v.Method,
            new Uri(v.Url),
            HeadersFor(v),
            Encoding.UTF8.GetBytes(v.BodyUtf8 + " "));

        Assert.False(result.IsValid);
        Assert.Equal(QueueyVerificationFailure.BodyHashMismatch, result.Failure);
    }

    [Fact]
    public void A_body_swapped_together_with_its_hash_header_still_fails_because_the_header_is_signed()
    {
        // The attack the body-hash check alone would miss: recompute the hash
        // header to match the forged body. The signature covers that header, so
        // it stops here — this is why both checks have to exist.
        var v = First();
        var forged = Encoding.UTF8.GetBytes("{\"orderId\":\"evil\"}");
        var forgedHash = Sha256HexLower(forged);

        var result = VerifierFor(v).Verify(
            v.Method,
            new Uri(v.Url),
            Override(HeadersFor(v), QueueyHeaders.ContentSha256, forgedHash),
            forged);

        Assert.False(result.IsValid);
        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, result.Failure);
    }

    [Fact]
    public void A_delivery_signed_too_long_ago_is_rejected_and_so_is_one_from_the_future()
    {
        var v = First();
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(v.Timestamp);

        var tooOld = VerifierFor(v, at: signedAt.AddMinutes(6)).Verify(
            v.Method, new Uri(v.Url), HeadersFor(v), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.TimestampOutsideWindow, tooOld.Failure);

        var fromTheFuture = VerifierFor(v, at: signedAt.AddMinutes(-6)).Verify(
            v.Method, new Uri(v.Url), HeadersFor(v), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.TimestampOutsideWindow, fromTheFuture.Failure);

        // The edge of the window is still inside it.
        var atTheEdge = VerifierFor(v, at: signedAt.AddMinutes(5)).Verify(
            v.Method, new Uri(v.Url), HeadersFor(v), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.True(atTheEdge.IsValid);
    }

    [Fact]
    public void A_wrong_secret_fails_on_the_signature_and_a_missing_header_fails_before_any_hashing()
    {
        var v = First();

        var wrongSecret = new QueueyDeliveryVerifier("not-the-secret", OptionsAt(v)).Verify(
            v.Method, new Uri(v.Url), HeadersFor(v), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, wrongSecret.Failure);

        var noSignature = VerifierFor(v).Verify(
            v.Method, new Uri(v.Url), Override(HeadersFor(v), QueueyHeaders.Signature, null), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.MissingHeaders, noSignature.Failure);

        var noTimestamp = VerifierFor(v).Verify(
            v.Method, new Uri(v.Url), Override(HeadersFor(v), QueueyHeaders.Timestamp, null), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.MissingHeaders, noTimestamp.Failure);

        var badTimestamp = VerifierFor(v).Verify(
            v.Method, new Uri(v.Url), Override(HeadersFor(v), QueueyHeaders.Timestamp, "not-a-number"), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.MalformedTimestamp, badTimestamp.Failure);
    }

    [Fact]
    public void The_replay_guard_runs_last_so_an_invalid_delivery_never_records_a_nonce()
    {
        var v = First();
        var asked = new List<string>();

        var options = OptionsAt(v);
        options.NonceAlreadySeen = nonce => { asked.Add(nonce); return true; };

        var replayed = new QueueyDeliveryVerifier(v.Secret, options).Verify(
            v.Method, new Uri(v.Url), HeadersFor(v), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.ReplayedNonce, replayed.Failure);
        Assert.Equal(new[] { v.Nonce }, asked);

        asked.Clear();
        var forged = new QueueyDeliveryVerifier("not-the-secret", options).Verify(
            v.Method, new Uri(v.Url), HeadersFor(v), Encoding.UTF8.GetBytes(v.BodyUtf8));
        Assert.Equal(QueueyVerificationFailure.SignatureMismatch, forged.Failure);
        Assert.Empty(asked);
    }

    [Fact]
    public void Uppercase_hex_verifies_and_surrounding_whitespace_on_a_header_is_tolerated()
    {
        var v = First();

        var headers = Override(HeadersFor(v), QueueyHeaders.Signature, v.ExpectedSignature.ToUpperInvariant());
        headers = Override(headers, QueueyHeaders.Nonce, $"  {v.Nonce}  ");

        var result = VerifierFor(v).Verify(v.Method, new Uri(v.Url), headers, Encoding.UTF8.GetBytes(v.BodyUtf8));

        Assert.True(result.IsValid, $"failed with {result.Failure}");
    }

    [Fact]
    public void An_empty_body_verifies_and_a_null_body_is_treated_as_empty()
    {
        var v = Doc.Vectors.Single(x => x.Name == "post_empty_body");

        Assert.True(VerifierFor(v).Verify(v.Method, new Uri(v.Url), HeadersFor(v), Array.Empty<byte>()).IsValid);
        Assert.True(VerifierFor(v).Verify(v.Method, new Uri(v.Url), HeadersFor(v), null).IsValid);
    }

    [Fact]
    public void The_event_id_comes_back_so_the_receiver_can_be_idempotent_on_it()
    {
        var v = First();
        var headers = Override(HeadersFor(v), QueueyHeaders.EventId, "evt_01H8XK");

        var result = VerifierFor(v).Verify(v.Method, new Uri(v.Url), headers, Encoding.UTF8.GetBytes(v.BodyUtf8));

        Assert.True(result.IsValid);
        Assert.Equal("evt_01H8XK", result.EventId);
    }

    [Fact]
    public void The_key_id_can_be_read_before_verifying_so_a_receiver_can_pick_the_secret()
    {
        var v = First();

        Assert.Equal(v.KeyId, QueueyDeliveryVerifier.ReadKeyId(HeadersFor(v)));
        Assert.Null(QueueyDeliveryVerifier.ReadKeyId(_ => null));
    }

    [Fact]
    public void A_secret_is_required()
    {
        Assert.Throws<ArgumentException>(() => new QueueyDeliveryVerifier(" "));
    }

    // ── support ───────────────────────────────────────────────────────

    private static Vector First() => Doc.Vectors.Single(x => x.Name == "post_json_no_query");

    private static QueueyDeliveryVerifierOptions OptionsAt(Vector v, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.FromUnixTimeSeconds(v.Timestamp);
        return new QueueyDeliveryVerifierOptions { Clock = () => now };
    }

    private static QueueyDeliveryVerifier VerifierFor(Vector v, DateTimeOffset? at = null)
        => new(v.Secret, OptionsAt(v, at));

    private static Func<string, string?> HeadersFor(Vector v)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [QueueyHeaders.KeyId] = v.KeyId,
            [QueueyHeaders.Timestamp] = v.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [QueueyHeaders.Nonce] = v.Nonce,
            [QueueyHeaders.ContentSha256] = v.ExpectedContentSha256,
            [QueueyHeaders.Signature] = v.ExpectedSignature,
        };
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static Func<string, string?> Override(Func<string, string?> headers, string name, string? value)
        => n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) ? value : headers(n);

    private static string Sha256HexLower(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var sb = new StringBuilder();
        foreach (var b in sha.ComputeHash(data))
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static VectorDoc Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var dir = Path.GetDirectoryName(asm.Location)!;
        var path = Path.Combine(dir, "GoldenVectors", "hmac-vectors.json");
        if (!File.Exists(path))
            path = Path.Combine(dir, "..", "..", "..", "GoldenVectors", "hmac-vectors.json");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<VectorDoc>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
