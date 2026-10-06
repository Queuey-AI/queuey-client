using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Queuey.Client.Cli;

/// <summary>
/// An address as <c>queuey listen</c> prints it: the scheme, the host and the path, never the query, the fragment or the
/// user info, and every part of the path that may carry a secret shown as <c>…</c>. The same rules as Queuey's
/// <c>TargetUrlRedaction</c> (F1.5): what an agent reads lands in its transcript. The request to the local app keeps the
/// whole URL.
/// </summary>
// Portert fra Queuey (SharedKernel/Abstractions/Delivery/TargetUrlRedaction.cs, 2026-10-05) for review av queuey-client
// #50 (K7). Endres reglene der, endres de her.
internal static class UrlRedaction
{
    /// <summary>What stands in for a part of a path that may carry a secret.</summary>
    public const string Hidden = "…";

    /// <summary><paramref name="url"/> as one string an agent may read, or <see cref="Hidden"/> when it is not an absolute URL.</summary>
    public static string Redact(string url)
        => TryParseAbsolute(url, out Uri uri)
            ? $"{uri.Scheme}://{uri.Authority}{RedactedPath(uri.Host.ToLowerInvariant(), uri.AbsolutePath)}"
            : Hidden;

    /// <summary>
    /// The path of the queue's endpoint as an agent may read it: from the endpoint's URL when the envelope has it, so a
    /// known webhook host's route rules apply, else from the path and query alone, without the query.
    /// </summary>
    public static string EndpointPath(string? originalUrl, string pathAndQuery)
    {
        if (TryParseAbsolute(originalUrl, out Uri uri))
            return RedactedPath(uri.Host.ToLowerInvariant(), uri.AbsolutePath);

        string path = pathAndQuery.Split('?', '#')[0];
        return path.StartsWith("/", StringComparison.Ordinal) ? RedactedPath(host: string.Empty, path) : Hidden;
    }

    // En verdi er en URL når den har et skjema og «://». På Linux og macOS leser Uri.TryCreate «/sti» som en absolutt
    // fil-URI, så en relativ sti ville ellers blitt «file:///sti».
    private static bool TryParseAbsolute(string? value, out Uri uri)
    {
        uri = null!;
        return value is not null
               && value.Contains("://", StringComparison.Ordinal)
               && Uri.TryCreate(value, UriKind.Absolute, out uri!);
    }

    private static string RedactedPath(string host, string absolutePath)
    {
        // Stien starter med «/», så første del er tom.
        string[] segments = absolutePath.Split('/').Skip(1).ToArray();

        if (RouteOnHookHost(host, segments) is { } route)
        {
            bool hidesSomething = segments.Skip(route).Any(segment => segment.Length > 0);
            var shown = segments.Take(route).ToList();
            if (hidesSomething)
                shown.Add(Hidden);
            else
                shown.AddRange(segments.Skip(route));
            return "/" + string.Join("/", shown);
        }

        var parts = new List<string>(segments.Length);
        foreach (string segment in segments)
        {
            string part = segment.Length == 0 || IsOrdinaryPathWord(segment) ? segment : Hidden;
            if (part == Hidden && parts.Count > 0 && parts[^1] == Hidden)
                continue;
            parts.Add(part);
        }
        return "/" + string.Join("/", parts);
    }

    /// <summary>
    /// On a known webhook host, how many leading path segments are the route (shown); the rest carries the secret.
    /// Null for any other host. A path that does not have the expected shape shows no segments at all.
    /// </summary>
    private static int? RouteOnHookHost(string host, string[] segments)
    {
        string At(int i) => i < segments.Length ? segments[i] : string.Empty;

        if (host == "hooks.slack.com")
            return At(0) == "services" ? 3 : At(0) is "workflows" or "triggers" ? 1 : 0;

        if (host == "hooks.zapier.com")
            return At(0) == "hooks" ? 3 : 0;

        if (host is "discord.com" or "discordapp.com" || host.EndsWith(".discord.com", StringComparison.Ordinal)
            || host.EndsWith(".discordapp.com", StringComparison.Ordinal))
        {
            int webhooks = Array.IndexOf(segments, "webhooks");
            return webhooks >= 0 ? webhooks + 2 : 0;
        }

        if (MakeHost.IsMatch(host)
            || host.EndsWith(".webhook.office.com", StringComparison.Ordinal)
            || host is "outlook.office.com" or "outlook.office365.com" or "maker.ifttt.com")
            return 0;

        return null;
    }

    private static readonly Regex MakeHost = new(@"^hook\.(?:[a-z0-9-]+\.)?(?:make|integromat)\.com$", RegexOptions.Compiled);

    // Et vanlig stiord: høyst 15 bokstaver (eventuelt ord bundet med - eller .), en versjon (v1, v10), eller et tall på
    // høyst fire sifre. Alt annet kan være en hemmelighet.
    private static readonly Regex OrdinaryWord = new(@"^[A-Za-z]+(?:[-.][A-Za-z]+)*$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^[vV][0-9]{1,3}$", RegexOptions.Compiled);
    private static readonly Regex ShortNumber = new(@"^[0-9]{1,4}$", RegexOptions.Compiled);

    private static bool IsOrdinaryPathWord(string segment)
        => Version.IsMatch(segment)
           || ShortNumber.IsMatch(segment)
           || (segment.Length <= 15 && OrdinaryWord.IsMatch(segment));
}
