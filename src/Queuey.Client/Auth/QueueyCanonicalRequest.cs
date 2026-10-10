using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Queuey.Client;

/// <summary>
/// Builds the Queuey HMAC canonical request string. This must match the server byte-for-byte:
/// <c>METHOD\nPATH\nQUERY\nTIMESTAMP\nNONCE\nCONTENT_SHA256</c> (six lines, single <c>\n</c>, no
/// trailing newline), where METHOD is upper-cased, PATH is trimmed (empty → <c>/</c>), QUERY is
/// flatten-sort-encoded (see <see cref="NormalizeQuery"/>), and CONTENT_SHA256 is lowercase hex.
/// </summary>
public static class QueueyCanonicalRequest
{
    /// <summary>
    /// The canonical string of a delivery's v2 signature (<c>X-Queuey-Signatures: v2=…</c>): the six lines of
    /// <see cref="Build"/>, then the event id (<c>X-Queuey-Event-Id</c>) and the idempotency key (<c>Idempotency-Key</c>),
    /// each trimmed and empty when the delivery has none. The line breaks are there either way.
    /// </summary>
    public static string BuildV2(
        string method, Uri requestUri, string timestamp, string nonce, string contentSha256, string? eventId, string? idempotencyKey)
        => Build(method, requestUri, timestamp, nonce, contentSha256)
           + "\n" + (eventId ?? string.Empty).Trim()
           + "\n" + (idempotencyKey ?? string.Empty).Trim();

    /// <summary>Builds the canonical string from a request's method, URI, and the signed header values.</summary>
    public static string Build(string method, Uri requestUri, string timestamp, string nonce, string contentSha256)
    {
        if (requestUri is null) throw new ArgumentNullException(nameof(requestUri));

        string normalizedMethod = (method ?? string.Empty).Trim().ToUpperInvariant();
        string normalizedPath = NormalizePath(requestUri);
        string normalizedQuery = NormalizeQuery(requestUri.Query);

        var sb = new StringBuilder(256);
        sb.Append(normalizedMethod).Append('\n');
        sb.Append(normalizedPath).Append('\n');
        sb.Append(normalizedQuery).Append('\n');
        sb.Append(timestamp).Append('\n');
        sb.Append(nonce).Append('\n');
        sb.Append(contentSha256);
        return sb.ToString();
    }

    /// <summary>
    /// The path as the server reads it (ASP.NET Core's <c>Request.Path</c>): percent-decoded as UTF-8, except <c>%2F</c>, which
    /// stays as written so it cannot become a separator. A sequence that is not valid UTF-8 stays as written. <c>+</c> is a
    /// plus. Empty → <c>/</c>.
    /// </summary>
    // Security-review av queuey-client #74 (BØR-1): stien ble signert escapet (AbsolutePath), mens Queuey signerer og verifiserer
    // den dekodet. Et endepunkt med %C3%A9 eller %20 i stien ble alltid avvist. Gjelder v1 og v2, inn og ut.
    private static string NormalizePath(Uri uri)
    {
        string path = DecodePath(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
    }

    /// <summary><paramref name="escaped"/> decoded as <see cref="NormalizePath"/> describes.</summary>
    internal static string DecodePath(string escaped)
    {
        if (escaped.IndexOf('%') < 0)
            return escaped;

        var result = new StringBuilder(escaped.Length);
        var bytes = new List<byte>();
        int runStart = -1;
        for (int i = 0; i < escaped.Length;)
        {
            if (escaped[i] == '%' && i + 2 < escaped.Length
                && HexValue(escaped[i + 1]) is var high and >= 0 && HexValue(escaped[i + 2]) is var low and >= 0
                && !((high << 4 | low) == 0x2F))
            {
                if (runStart < 0) runStart = i;
                bytes.Add((byte)(high << 4 | low));
                i += 3;
                continue;
            }

            Flush(escaped, runStart, i, bytes, result);
            runStart = -1;
            result.Append(escaped[i]);
            i++;
        }
        Flush(escaped, runStart, escaped.Length, bytes, result);
        return result.ToString();
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // En rekke prosentkodede byte: dekodet når den er gyldig UTF-8, ellers som den sto.
    private static void Flush(string escaped, int runStart, int runEnd, List<byte> bytes, StringBuilder into)
    {
        if (bytes.Count == 0)
            return;
        try
        {
            into.Append(StrictUtf8.GetString(bytes.ToArray()));
        }
        catch (DecoderFallbackException)
        {
            into.Append(escaped, runStart, runEnd - runStart);
        }
        bytes.Clear();
    }

    private static int HexValue(char c)
        => c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : -1;

    /// <summary>
    /// Flatten-sort-encode the query: URL-decode each key/value (<c>+</c> → space), sort pairs by
    /// key then value (ordinal), re-encode each with RFC-3986 escaping, join with <c>&amp;</c>, no
    /// leading <c>?</c>. Empty/absent query → empty string.
    /// </summary>
    public static string NormalizeQuery(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return string.Empty;

        string raw = rawQuery;
        if (raw[0] == '?')
            raw = raw.Substring(1);

        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string segment in raw.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = segment.IndexOf('=');
            string rawKey, rawValue;
            if (idx < 0)
            {
                rawKey = segment;
                rawValue = string.Empty;
            }
            else
            {
                rawKey = segment.Substring(0, idx);
                rawValue = segment.Substring(idx + 1);
            }

            pairs.Add(new KeyValuePair<string, string>(DecodeComponent(rawKey), DecodeComponent(rawValue)));
        }

        IEnumerable<KeyValuePair<string, string>> ordered = pairs
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Value, StringComparer.Ordinal);

        return string.Join("&", ordered.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
    }

    private static string DecodeComponent(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // '+' means space in query components; decode after that substitution.
        return Uri.UnescapeDataString(value.Replace("+", " "));
    }
}
