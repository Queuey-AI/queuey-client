using System.Net.Http;
using System.Net.Http.Headers;

namespace Queuey.Client;

/// <summary>Byte-exact Queuey HTTP header names used by the SDK.</summary>
public static class QueueyHeaders
{
    /// <summary>API-key authentication header.</summary>
    public const string ApiKey = "X-Api-Key";

    /// <summary>HMAC signing key id.</summary>
    public const string KeyId = "X-Queuey-Key-Id";

    /// <summary>HMAC unix-seconds timestamp.</summary>
    public const string Timestamp = "X-Queuey-Timestamp";

    /// <summary>HMAC single-use nonce.</summary>
    public const string Nonce = "X-Queuey-Nonce";

    /// <summary>Lowercase-hex SHA-256 of the raw request body.</summary>
    public const string ContentSha256 = "X-Queuey-Content-SHA256";

    /// <summary>Lowercase-hex HMAC-SHA256 request signature.</summary>
    public const string Signature = "X-Queuey-Signature";

    /// <summary>License scope header for control-plane calls.</summary>
    public const string LicensePublicId = "X-License-PublicId";

    /// <summary>Producer trace/source header.</summary>
    public const string Source = "X-Queuey-Source";

    /// <summary>Group-key context header (extracted only by context-configured queues).</summary>
    public const string GroupKey = "X-Queuey-Group-Key";

    /// <summary>Event-type context header (extracted only by context-configured queues).</summary>
    public const string EventType = "X-Queuey-Event-Type";

    /// <summary>Producer idempotency key (24h dedup by default).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Response header set to <c>true</c> when a publish was deduplicated (replayed).</summary>
    public const string IdempotentReplay = "X-Queuey-Idempotent-Replay";
}

/// <summary>Internal helpers for writing raw header values onto a request.</summary>
internal static class QueueyHttpHeaders
{
    /// <summary>Sets (replacing any existing) a raw request header value without validation.</summary>
    public static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }
}
