using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Queuey.Client;

/// <summary>Hashing primitives for Queuey signed requests. All hex output is lowercase.</summary>
internal static class QueueyHash
{
    /// <summary>Lowercase-hex SHA-256 of the given bytes (empty input → the well-known empty-body hash).</summary>
    public static string Sha256HexLower(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return ToLowerHex(sha256.ComputeHash(data ?? System.Array.Empty<byte>()));
    }

    /// <summary>Lowercase-hex HMAC-SHA256 of <paramref name="message"/> (UTF-8) under <paramref name="keyBytes"/>.</summary>
    public static string HmacSha256HexLower(byte[] keyBytes, string message)
    {
        using var hmac = new HMACSHA256(keyBytes);
        return ToLowerHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
