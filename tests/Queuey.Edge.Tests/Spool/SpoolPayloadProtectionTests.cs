using System.Security.Cryptography;
using Queuey.Edge;

namespace Queuey.Edge.Tests.Spool;

/// <summary>
/// The at-rest scheme is only worth having if a wrong key, a tampered blob or
/// an unknown scheme byte FAILS instead of yielding bytes — a spool that hands
/// the transfer loop garbage would ship garbage to Cloud.
/// </summary>
public class SpoolPayloadProtectionTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(SpoolPayloadProtection.KeyBytes);

    [Fact]
    public void Round_trips_and_never_stores_the_plaintext_bytes()
    {
        var key = Key();
        var plain = "{\"card\":\"4111 1111 1111 1111\"}"u8.ToArray();

        var blob = SpoolPayloadProtection.Protect(key, plain);

        Assert.Equal(SpoolPayloadProtection.SchemeAesGcmV1, blob[0]);
        Assert.DoesNotContain("4111", System.Text.Encoding.Latin1.GetString(blob));
        Assert.Equal(plain, SpoolPayloadProtection.Unprotect(key, blob));
    }

    [Fact]
    public void Two_protections_of_the_same_payload_differ_because_the_nonce_is_fresh()
    {
        var key = Key();
        var plain = new byte[64];

        Assert.NotEqual(SpoolPayloadProtection.Protect(key, plain), SpoolPayloadProtection.Protect(key, plain));
    }

    [Fact]
    public void A_wrong_key_fails_loudly()
    {
        var blob = SpoolPayloadProtection.Protect(Key(), new byte[8]);

        Assert.ThrowsAny<CryptographicException>(() => SpoolPayloadProtection.Unprotect(Key(), blob));
    }

    [Fact]
    public void A_tampered_blob_fails_loudly()
    {
        var key = Key();
        var blob = SpoolPayloadProtection.Protect(key, "hello"u8.ToArray());
        blob[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => SpoolPayloadProtection.Unprotect(key, blob));
    }

    [Fact]
    public void A_plain_row_is_not_mistaken_for_a_protected_one()
    {
        Assert.ThrowsAny<CryptographicException>(() => SpoolPayloadProtection.Unprotect(Key(), "{\"a\":1}"u8.ToArray()));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void The_key_must_be_exactly_32_bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => SpoolPayloadProtection.Protect(new byte[length], new byte[1]));
    }
}
