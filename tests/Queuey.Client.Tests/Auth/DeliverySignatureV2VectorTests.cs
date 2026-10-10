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
/// Backendens gylne vektorer for signaturen på en levering, v1 og v2 (Queuey #521, laget av serverens SignedRequestSigner), kjørt mot
/// produksjonskoden: <see cref="QueueyDeliveryVerifier"/> skal godta hver av dem slik serveren sendte den. Fila er en ordrett kopi av
/// tests/Api/Queuey.Api.Tests/GoldenVectors/queuey-delivery-signature-v2.json; scripts/check-queuey-vectors.sh sammenligner dem før en tag.
/// </summary>
public class DeliverySignatureV2VectorTests
{
    private sealed record Doc(Vector[] Vectors);

    private sealed record Vector(
        string Name, string Method, string Url, string BodyUtf8, string? EventId, string? IdempotencyKey, string KeyId, string Secret,
        long Timestamp, string Nonce, string ExpectedContentSha256, string ExpectedSignature, string ExpectedSignatures);

    private static readonly Doc Vectors = Load();

    public static IEnumerable<object[]> Names() => Vectors.Vectors.Select(v => new object[] { v.Name });

    [Fact]
    public void All_eight_vectors_are_there_including_the_percent_encoded_path()
    {
        Assert.Equal(8, Vectors.Vectors.Length);
        Assert.Contains(Vectors.Vectors, v => v.Name == "percent_encoded_path");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_verifier_takes_every_delivery_the_server_signed_by_v2(string name)
    {
        Vector v = Vectors.Vectors.Single(x => x.Name == name);

        QueueyVerificationResult result = Verifier(v).Verify(v.Method, new Uri(v.Url), Headers(v), Encoding.UTF8.GetBytes(v.BodyUtf8));

        Assert.True(result.IsValid, $"{name}: {result.Failure}");
        Assert.Equal(2, result.SignatureVersion);
        Assert.Equal(v.EventId, result.EventId);
        Assert.Equal(v.IdempotencyKey, result.IdempotencyKey);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_v1_signature_of_every_vector_holds_too_with_accept_v1(string name)
    {
        Vector v = Vectors.Vectors.Single(x => x.Name == name);
        Func<string, string?> headers = Headers(v);
        Func<string, string?> v1Only = n => string.Equals(n, QueueyHeaders.Signatures, StringComparison.OrdinalIgnoreCase) ? null : headers(n);

        QueueyVerificationResult result = Verifier(v, acceptV1: true).Verify(v.Method, new Uri(v.Url), v1Only, Encoding.UTF8.GetBytes(v.BodyUtf8));

        Assert.True(result.IsValid, $"{name}: {result.Failure}");
        Assert.Equal(1, result.SignatureVersion);
    }

    [Fact]
    public void A_percent_encoded_path_is_signed_decoded_except_an_encoded_slash()
    {
        Assert.Equal("/hooks/café/order%2F1/a b", QueueyCanonicalRequest.DecodePath("/hooks/caf%C3%A9/order%2F1/a%20b"));
        Assert.Equal("/a%2fb", QueueyCanonicalRequest.DecodePath("/a%2fb"));
        Assert.Equal("/a+b", QueueyCanonicalRequest.DecodePath("/a+b"));
        Assert.Equal("/bad%C3", QueueyCanonicalRequest.DecodePath("/bad%C3"));      // ikke gyldig UTF-8: som den sto
        Assert.Equal("/x%zz", QueueyCanonicalRequest.DecodePath("/x%zz"));
    }

    private static QueueyDeliveryVerifier Verifier(Vector v, bool acceptV1 = false)
        => new(v.Secret, new QueueyDeliveryVerifierOptions
        {
            Clock = () => DateTimeOffset.FromUnixTimeSeconds(v.Timestamp), AcceptV1 = acceptV1,
        });

    private static Func<string, string?> Headers(Vector v)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [QueueyHeaders.KeyId] = v.KeyId,
            [QueueyHeaders.Timestamp] = v.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [QueueyHeaders.Nonce] = v.Nonce,
            [QueueyHeaders.ContentSha256] = v.ExpectedContentSha256,
            [QueueyHeaders.Signature] = v.ExpectedSignature,
            [QueueyHeaders.Signatures] = v.ExpectedSignatures,
            [QueueyHeaders.EventId] = v.EventId,
            [QueueyHeaders.IdempotencyKey] = v.IdempotencyKey,
        };
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static Doc Load()
    {
        Assembly assembly = typeof(DeliverySignatureV2VectorTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("queuey-delivery-signature-v2.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Doc>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
