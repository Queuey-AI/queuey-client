using System;
using System.Linq;

namespace Queuey.Client;

/// <summary>Builds absolute request URIs from a host base address plus path segments and a query.</summary>
internal static class QueueyUri
{
    /// <summary>
    /// Combines <paramref name="baseAddress"/> with the given path segments (each URL-encoded) and an
    /// optional pre-built query string (without a leading <c>?</c>).
    /// </summary>
    public static Uri Build(Uri baseAddress, string? query, params string[] segments)
    {
        if (baseAddress is null) throw new ArgumentNullException(nameof(baseAddress));

        // Preserve any base path on an override (e.g. a local instance behind "http://localhost/queuey/"),
        // while staying correct for authority-only hosts like "https://ingress.queuey.ai".
        string authority = baseAddress.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        string basePath = baseAddress.AbsolutePath.Trim('/');
        string path = string.Join("/", segments.Select(Uri.EscapeDataString));
        string combined = basePath.Length == 0 ? path : basePath + "/" + path;

        string url = authority + "/" + combined;
        if (!string.IsNullOrEmpty(query))
            url += "?" + query;

        return new Uri(url, UriKind.Absolute);
    }
}
