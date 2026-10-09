using System;

namespace Queuey.Client.Waas;

// Queuey #514 og security-reviewen av queuey-client #72 (2026-10-09): en mottakers URL kan bære en hemmelighet, og Queuey viser
// den redigert til en nøkkel og en innlogging. pull, apply --check og plan må vite når en URL er det, eller ville vært det.
// Reglene (Queuey sin TargetUrlRedaction) krever net7+, og denne pakken bygger også for netstandard2.0, så de gis inn her av
// queuey-CLI-en, som har en ordrett kopi.

/// <summary>A receiver's URL in a deployment file, the pulled workspace or a plan: whether it reads redacted, and how it is shown.</summary>
internal static class DeploymentUrls
{
    /// <summary>The redaction marker Queuey writes where a part of a URL may carry a secret.</summary>
    internal const string Marker = "…";

    private const string EncodedMarker = "%E2%80%A6";

    /// <summary>
    /// How a receiver's URL reads redacted (Queuey's TargetUrlRedaction.Redact): set by the <c>queuey</c> CLI. Without it the
    /// SDK knows a redacted URL by its marker only, and shows a URL as its scheme and host.
    /// </summary>
    internal static Func<string, string?>? Redact { get; set; }

    /// <summary>Whether <paramref name="url"/> carries the marker, as such or percent-encoded.</summary>
    internal static bool CarriesMarker(string? url)
        => url is not null && (url.IndexOf(Marker, StringComparison.Ordinal) >= 0
                               || url.IndexOf(EncodedMarker, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// Whether <paramref name="url"/> is not safe to write or show as it is: it is a redacted reading (the marker), or it reads
    /// differently redacted (a query, user info or a path part that may be a secret).
    /// </summary>
    internal static bool HidesSomething(string? url)
        => url is not null && !IsReference(url)
           && (CarriesMarker(url) || (Redact is { } redact ? !string.Equals(redact(url), url, StringComparison.Ordinal) : HasQueryOrUserInfo(url)));

    /// <summary><paramref name="url"/> as it may be shown: redacted by the rules when the CLI gave them, else its scheme and host.</summary>
    internal static string? Shown(string? url)
    {
        if (url is null || IsReference(url)) return url;
        if (Redact is { } redact) return redact(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || url.IndexOf("://", StringComparison.Ordinal) < 0)
            return Marker;
        return uri.AbsolutePath.Length > 1 || uri.Query.Length > 0 ? $"{uri.Scheme}://{uri.Authority}/{Marker}" : $"{uri.Scheme}://{uri.Authority}";
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same scheme, host and port.</summary>
    internal static bool SameOrigin(string? a, string? b)
        => Uri.TryCreate(a, UriKind.Absolute, out Uri? x) && Uri.TryCreate(b?.Replace(Marker, "x"), UriKind.Absolute, out Uri? y)
           && string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(x.Authority, y.Authority, StringComparison.OrdinalIgnoreCase);

    // ${VAR} er filens referanse, ikke en adresse.
    private static bool IsReference(string url) => url.StartsWith("${", StringComparison.Ordinal);

    private static bool HasQueryOrUserInfo(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0);
}
