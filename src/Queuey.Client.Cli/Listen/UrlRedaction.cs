namespace Queuey.Client.Cli;

/// <summary>
/// An address as <c>queuey listen</c> prints it: the scheme, the host and the path, never the query, the fragment or the
/// user info, and every part of the path that may carry a secret shown as <c>…</c>. What an agent reads lands in its
/// transcript. The request to the local app keeps the whole URL.
/// </summary>
// Security-review av #72 (K3): ingen egne regler her lenger, bare Queuey sine (TargetUrlRedaction, kopiert ordrett fra #514).
// Før var dette en egen port fra 2026-10-05, som ikke fikk med seg endringene i #514.
internal static class UrlRedaction
{
    /// <summary>What stands in for a part of a path that may carry a secret.</summary>
    public const string Hidden = TargetUrlRedaction.Hidden;

    /// <summary><paramref name="url"/> as one string an agent may read, or <see cref="Hidden"/> when it is not a URL.</summary>
    public static string Redact(string url) => TargetUrlRedaction.Redact(url) ?? Hidden;

    /// <summary>
    /// The path of the queue's endpoint as an agent may read it: from the endpoint's URL when the envelope has it, so a
    /// known webhook host's route rules apply, else from the path and query alone, without the query.
    /// </summary>
    public static string EndpointPath(string? originalUrl, string pathAndQuery)
    {
        if (TargetUrlRedaction.HostAndPath(originalUrl) is { Path: { } path })
            return path;
        return pathAndQuery.StartsWith("/", System.StringComparison.Ordinal)
            ? TargetUrlRedaction.Redact(pathAndQuery) ?? Hidden
            : Hidden;
    }
}
