using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Tests;

/// <summary>
/// Verifies <see cref="HmacRequestSigner"/> reproduces, byte-for-byte, the authoritative signatures
/// produced by the Queuey backend's reference signing client (frozen in hmac-vectors.json).
/// </summary>
public class GoldenVectorTests
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
    public async Task Signer_reproduces_backend_content_hash_and_signature(string name)
    {
        Vector v = Doc.Vectors.Single(x => x.Name == name);

        var signer = new HmacRequestSigner(
            v.KeyId,
            v.Secret,
            clock: () => DateTimeOffset.FromUnixTimeSeconds(v.Timestamp),
            nonceFactory: () => v.Nonce);

        byte[] body = Encoding.UTF8.GetBytes(v.BodyUtf8);
        using var request = new HttpRequestMessage(new HttpMethod(v.Method), v.Url)
        {
            Content = new ByteArrayContent(body),
        };
        if (!string.IsNullOrEmpty(v.ContentType))
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(v.ContentType);

        await signer.AuthenticateAsync(request, body);

        Assert.Equal(v.ExpectedContentSha256, Single(request, QueueyHeaders.ContentSha256));
        Assert.Equal(v.ExpectedSignature, Single(request, QueueyHeaders.Signature));
        Assert.Equal(v.KeyId, Single(request, QueueyHeaders.KeyId));
        Assert.Equal(v.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), Single(request, QueueyHeaders.Timestamp));
        Assert.Equal(v.Nonce, Single(request, QueueyHeaders.Nonce));
    }

    [Fact]
    public void Vectors_are_present()
    {
        Assert.NotEmpty(Doc.Vectors);
        // Empty-body vector must equal the well-known SHA-256 of empty input.
        Vector empty = Doc.Vectors.Single(v => v.Name == "post_empty_body");
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", empty.ExpectedContentSha256);
    }

    private static string Single(HttpRequestMessage request, string header)
        => request.Headers.GetValues(header).Single();

    private static VectorDoc Load()
    {
        Assembly asm = typeof(GoldenVectorTests).Assembly;
        string resource = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("hmac-vectors.json", StringComparison.Ordinal));

        using Stream stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();

        return JsonSerializer.Deserialize<VectorDoc>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
