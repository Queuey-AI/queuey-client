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

    private static string NormalizePath(Uri uri)
    {
        string path = uri.AbsolutePath;
        return string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
    }

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
