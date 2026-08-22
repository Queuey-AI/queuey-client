using System;
using System.Security.Cryptography;

namespace Queuey.Edge;

/// <summary>
/// UUIDv7 (RFC 9562): 48-bit Unix-millisecond timestamp + random tail —
/// time-ordered without coordination, which keeps the spool's dedup index
/// append-friendly and makes transfer ids sort by publish time. Implemented
/// locally because net8.0 has no <c>Guid.CreateVersion7</c>.
/// </summary>
internal static class Uuid7
{
    public static string NewString(DateTimeOffset atUtc)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var ms = (ulong)atUtc.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC variant

        // Standard textual layout (8-4-4-4-12) built from the big-endian
        // bytes directly — Guid's little-endian field encoding would scramble
        // the timestamp ordering, so we format ourselves.
        return string.Create(36, bytes.ToArray(), static (span, b) =>
        {
            const string hex = "0123456789abcdef";
            var i = 0;
            for (var j = 0; j < 16; j++)
            {
                if (j is 4 or 6 or 8 or 10) span[i++] = '-';
                span[i++] = hex[b[j] >> 4];
                span[i++] = hex[b[j] & 0xF];
            }
        });
    }
}
