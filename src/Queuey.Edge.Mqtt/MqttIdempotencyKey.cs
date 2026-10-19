using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Queuey.Edge.Mqtt;

/// <summary>
/// The idempotency key for an MQTT message whose JSON payload carries the
/// producer's own event id (<see cref="MqttRoute.IdempotencyKeyField"/>):
/// <c>{id}:{the first 16 hex characters of SHA-256(payload)}</c>.
///
/// <para>Why the payload hash, and not the id alone: Queuey Cloud deduplicates a
/// key permanently, and the spool compares keys, never content. A device that
/// reuses an id (a counter reset by a restart, say) would otherwise have every
/// new message under that id swallowed as a repeat, forever. A real repeat (the
/// device re-sending at QoS 1, or the broker re-sending after Edge crashed
/// between its commit and the acknowledgement) carries the same bytes and gets
/// the same key; a reused id with new content becomes a new event.</para>
///
/// <para>The key travels as the <c>Idempotency-Key</c> header, so the id is
/// used as it is only when it is short visible ASCII (0x21–0x7E, at most 128
/// characters). Anything else is replaced by <c>h</c> and the first 32 hex
/// characters of SHA-256 of its UTF-8 bytes.</para>
/// </summary>
internal static class MqttIdempotencyKey
{
    /// <summary>The longest id used as it is in the key; a longer one is hashed.</summary>
    public const int MaxPlainIdLength = 128;

    private const int ContentHashHexChars = 16;
    private const int IdHashHexChars = 32;

    /// <summary>A path is one or more dot-separated field names, none of them empty or blank.</summary>
    public static bool IsValidPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var segment in path.Split('.'))
        {
            if (string.IsNullOrWhiteSpace(segment)) return false;
        }
        return true;
    }

    /// <summary>
    /// The key for <paramref name="payload"/>, or null when it has no usable id at
    /// <paramref name="path"/>: the field is missing or null, is an object, an
    /// array, a boolean or an empty string, or the payload is not JSON. A JSON
    /// string gives its value, a JSON number its text as sent.
    /// </summary>
    public static string? From(string path, byte[] payload)
    {
        var id = ReadId(path, payload);
        return id is null ? null : SafeId(id) + ":" + Hex(SHA256.HashData(payload), ContentHashHexChars);
    }

    private static string? ReadId(string path, byte[] payload)
    {
        if (payload.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var current = document.RootElement;
            foreach (var segment in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    return null;
            }

            var id = current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.GetRawText(),
                _ => null
            };
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not JSON, or not text the reader can decode: no id, never a blocked message.
            return null;
        }
    }

    private static string SafeId(string id)
    {
        if (id.Length <= MaxPlainIdLength && IsVisibleAscii(id)) return id;
        return "h" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(id)), IdHashHexChars);
    }

    private static bool IsVisibleAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is < '\x21' or > '\x7e') return false;
        }
        return true;
    }

    private static string Hex(byte[] hash, int chars)
        => Convert.ToHexString(hash, 0, chars / 2).ToLowerInvariant();
}
