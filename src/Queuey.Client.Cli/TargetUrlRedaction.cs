using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Queuey.Client.Cli;

// Security-review av #71 (B2): en mottakers URL kan bære hemmeligheten (i spørringen, brukerinfoen eller stien), og det CLI-en
// skriver, leser en agent. Reglene er kopiert ordrett fra Queuey sin TargetUrlRedaction (F1.5, SharedKernel, integration/agents
// c37cac52), så klienten skjuler det samme som MCP. Backend retter dette varig for subjekter som ikke er personer, i eget spor;
// til da redigerer klienten selv. Endres reglene der, endres de her.

/// <summary>
/// A receiver's address as an agent may read it: the scheme, the host and the path, never the query, the fragment or the user
/// info, and every part of the path that may carry a secret shown as <c>…</c>. The rules are Queuey's own (TargetUrlRedaction).
/// </summary>
internal static class TargetUrlRedaction
{
    /// <summary>What stands in for a part of a path that may carry a secret.</summary>
    public const string Hidden = "…";

    /// <summary>
    /// The most text <see cref="RedactUrlsIn"/> reads. Longer text is cut at its last whitespace within the limit, and
    /// the rest reads as <see cref="Hidden"/>.
    /// </summary>
    public const int MaxTextLength = 4096;

    /// <summary>
    /// The host (with a port that is not the default) and the path of <paramref name="url"/>: never the query, the
    /// fragment or the user info, and every part of the path that may carry a secret is <see cref="Hidden"/>.
    /// Both are null for a value that is not an absolute URL.
    /// </summary>
    public static (string? Host, string? Path) HostAndPath(string? url)
    {
        if (!TryParseAbsolute(url, out var uri))
            return (null, null);

        return (uri.Authority, RedactedPath(uri.Host.ToLowerInvariant(), uri.AbsolutePath));
    }

    /// <summary>
    /// <paramref name="url"/> as one string an agent may read: <c>https://api.example.com/hooks/…</c>, with
    /// <see cref="HostAndPath"/>'s rules. A blank value stays as it is, and a value that is not an absolute URL reads as
    /// <see cref="Hidden"/>, since nothing says what it carries.
    /// </summary>
    public static string? Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;
        if (!TryParseAbsolute(url.Trim(), out var uri))
            return Hidden;

        return $"{uri.Scheme}://{uri.Authority}{RedactedPath(uri.Host.ToLowerInvariant(), uri.AbsolutePath)}";
    }

    /// <summary>
    /// <paramref name="text"/> with every URL in it redacted: each absolute URL by <see cref="Redact"/>, and the location
    /// a refused redirect names (<c>redirect_not_allowed: HTTP 302 → /login?session=…</c>, written by the delivery
    /// strategy), which can be a relative path with a query. A query left anywhere else in the text reads as
    /// <c>?…</c>. Text longer than <see cref="MaxTextLength"/> is cut at a whitespace first. The rest of the text is left
    /// as it is: it is Queuey's own.
    /// </summary>
    public static string? RedactUrlsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Først stedet en avvist redirect peker på, som kan være relativt; så hver absolutte URL som er igjen. Et punktum
        // eller komma rett etter en URL hører til setningen, ikke til URL-en. Til sist en spørring som står igjen utenfor
        // en URL, som i «https://h/a ?token=x», der mellomrommet skiller den fra URL-en.
        var redirectsRedacted = RedirectLocation.Replace(WithinLimit(text), m => m.Groups["arrow"].Value + RedactLocation(m.Groups["location"].Value));
        var urlsRedacted = AbsoluteUrl.Replace(redirectsRedacted, m =>
        {
            var url = m.Value.TrimEnd(SentencePunctuation);
            return (Redact(url) ?? Hidden) + m.Value[url.Length..];
        });
        return LeftoverQuery.Replace(urlsRedacted, "?" + Hidden);
    }

    // Høyst MaxTextLength tegn leses (2026-10-05). En lagret feiltekst er høyst 2 000 tegn, men en probe svarer med
    // Location-headeren slik mottakeren sendte den, opptil 64 KB, og teksten trenger ikke mer. Teksten kuttes ved siste
    // mellomrom innenfor grensen, så ingen URL eller sti deles: en bit av et token kunne vist det hele tokenet skjuler,
    // som et langt bokstav-token kortet ned til et vanlig stiord. Det som kuttes bort, blir «…».
    private static string WithinLimit(string text)
    {
        if (text.Length <= MaxTextLength)
            return text;

        var cut = MaxTextLength;
        while (cut > 0 && !char.IsWhiteSpace(text[cut]))
            cut--;
        return cut > 0 ? $"{text[..cut]} {Hidden}" : Hidden;
    }

    private static readonly char[] SentencePunctuation = { '.', ',', ';', ':', '!', '?' };

    // En verdi er en URL når den har et skjema og «://». På Linux og macOS leser Uri.TryCreate «/sti» som en absolutt
    // fil-URI, så en relativ sti ville ellers blitt «file:///sti».
    private static bool TryParseAbsolute(string? value, out Uri uri)
    {
        uri = null!;
        return value is not null
               && value.Contains("://", StringComparison.Ordinal)
               && Uri.TryCreate(value, UriKind.Absolute, out uri!);
    }

    // HttpDeliveryStrategy skriver «redirect_not_allowed: HTTP {code} → {Location} (Queuey does not follow redirects)».
    // AbsoluteUrl kjøres uten tilbakesporing (2026-10-05): med tilbakesporing var skjemadelen kvadratisk på tekst som
    // «a.a.a.…», målt til 1,4 s for 64 KB og 35 s for 256 KB, mot 1–3 ms nå. De to andre starter på et bestemt tegn og
    // slutter med en klasse som løper til et mellomrom, så de sporer aldri tilbake. Uten tilbakesporing var de tregere,
    // fordi de fanger grupper eller gir lange treff (målt 2026-10-05).
    private static readonly Regex RedirectLocation = new(@"(?<arrow>→\s*)(?<location>\S+)", RegexOptions.Compiled);
    private static readonly Regex AbsoluteUrl = new(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""'<>()]+", RegexOptions.NonBacktracking);
    private static readonly Regex LeftoverQuery = new(@"\?\S+", RegexOptions.Compiled);

    private static string RedactLocation(string location)
    {
        if (TryParseAbsolute(location, out _))
            return Redact(location) ?? Hidden;

        // En relativ plassering: stien med reglene for en vert vi ikke kjenner, uten spørring og fragment.
        var path = location.Split('?', '#')[0];
        return path.StartsWith('/') ? RedactedPath(host: string.Empty, path) : Hidden;
    }

    private static string RedactedPath(string host, string absolutePath)
    {
        // Stien starter med «/», så første del er tom.
        var segments = absolutePath.Split('/').Skip(1).ToArray();

        if (RouteOnHookHost(host, segments) is { } route)
        {
            var hidesSomething = segments.Skip(route).Any(segment => segment.Length > 0);
            var shown = segments.Take(route).ToList();
            if (hidesSomething)
                shown.Add(Hidden);
            else
                shown.AddRange(segments.Skip(route));
            return "/" + string.Join('/', shown);
        }

        var parts = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var part = segment.Length == 0 || IsOrdinaryPathWord(segment) ? segment : Hidden;
            if (part == Hidden && parts.Count > 0 && parts[^1] == Hidden)
                continue;
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>
    /// On a known webhook host, how many leading path segments are the route (shown); the rest carries the secret.
    /// Null for any other host. A path that does not have the expected shape shows no segments at all.
    /// </summary>
    private static int? RouteOnHookHost(string host, string[] segments)
    {
        string At(int i) => i < segments.Length ? segments[i] : string.Empty;

        // hooks.slack.com/services/T…/B…/<hemmelighet>; workflows/ og triggers/ viser bare ruteordet.
        if (host == "hooks.slack.com")
            return At(0) == "services" ? 3 : At(0) is "workflows" or "triggers" ? 1 : 0;

        // hooks.zapier.com/hooks/catch/<konto>/<hemmelighet>/ (og hooks/standard/…).
        if (host == "hooks.zapier.com")
            return At(0) == "hooks" ? 3 : 0;

        // discord.com/api[/v10]/webhooks/<id>/<token>.
        if (host is "discord.com" or "discordapp.com" || host.EndsWith(".discord.com", StringComparison.Ordinal)
            || host.EndsWith(".discordapp.com", StringComparison.Ordinal))
        {
            var webhooks = Array.IndexOf(segments, "webhooks");
            return webhooks >= 0 ? webhooks + 2 : 0;
        }

        // Make (tidligere Integromat): hook.eu1.make.com/<token>. Microsoft Teams: <tenant>.webhook.office.com/…, og de eldre
        // koblingene på outlook.office.com. IFTTT: maker.ifttt.com/trigger/<hendelse>/with/key/<nøkkel>. Hele stien er
        // hemmelig eller bærer den.
        if (MakeHost.IsMatch(host)
            || host.EndsWith(".webhook.office.com", StringComparison.Ordinal)
            || host is "outlook.office.com" or "outlook.office365.com" or "maker.ifttt.com")
            return 0;

        return null;
    }

    private static readonly Regex MakeHost = new(@"^hook\.(?:[a-z0-9-]+\.)?(?:make|integromat)\.com$", RegexOptions.Compiled);

    // Et vanlig stiord: høyst 15 bokstaver (eventuelt ord bundet med - eller .), en versjon (v1, v10), eller et tall på
    // høyst fire sifre. Alt annet kan være en hemmelighet: sifre blandet med bokstaver (s3cr3t-t0k3n), lange bokstav-
    // eller tallrekker, ord bundet med _, prosentkoding og andre tegn.
    private static readonly Regex OrdinaryWord = new(@"^[A-Za-z]+(?:[-.][A-Za-z]+)*$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^[vV][0-9]{1,3}$", RegexOptions.Compiled);
    private static readonly Regex ShortNumber = new(@"^[0-9]{1,4}$", RegexOptions.Compiled);

    private static bool IsOrdinaryPathWord(string segment)
        => Version.IsMatch(segment)
           || ShortNumber.IsMatch(segment)
           || (segment.Length <= 15 && OrdinaryWord.IsMatch(segment));

    /// <summary>The properties that hold a receiver's address, whole: they are redacted as one URL.</summary>
    private static readonly HashSet<string> UrlProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "effectiveUrl", "targetUrl", "targetEndpoint", "url", "endpoint", "location",
    };

    /// <summary>
    /// Queuey's own console links: kept when <see cref="Operator.Link"/> takes them, null otherwise (security-review av #71
    /// runde 2, K-d), at any depth in the answer.
    /// </summary>
    private static readonly HashSet<string> ConsoleLinks = new(StringComparer.OrdinalIgnoreCase) { "approvalUrl", "consoleUrl" };

    /// <summary>
    /// <paramref name="element"/> with every receiver address redacted: a property that holds one (<c>effectiveUrl</c>,
    /// <c>targetUrl</c>, <c>targetEndpoint</c>, …) by <see cref="Redact"/>, and every other string by <see cref="RedactUrlsIn"/>,
    /// since an error text or a response preview can quote the URL.
    /// </summary>
    public static JsonElement? RedactJson(JsonElement? element, ResolvedConfig config)
    {
        if (element is not { } e) return null;
        JsonNode? node = JsonNode.Parse(e.GetRawText());
        node = RedactNode(node, property: null, config);
        return node is null ? JsonDocument.Parse("null").RootElement.Clone() : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private static JsonNode? RedactNode(JsonNode? node, string? property, ResolvedConfig config)
    {
        switch (node)
        {
            case JsonObject obj:
                // Nye noder hele veien: en node som alt har en forelder, kan ikke settes inn igjen.
                var redactedObject = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> member in obj)
                {
                    JsonNode? redacted = RedactNode(member.Value, member.Key, config);
                    redactedObject[member.Key] = redacted;
                    // En lenke som ble holdt tilbake, sies fra om ved siden av, så svaret ikke later som det ikke fantes en.
                    if (ConsoleLinks.Contains(member.Key) && redacted is null && member.Value is JsonValue v
                        && v.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetValue<string>()))
                        redactedObject["linkWithheld"] = Operator.Withheld;
                }
                return redactedObject;
            case JsonArray array:
                var redactedArray = new JsonArray();
                foreach (JsonNode? item in array)
                    redactedArray.Add(RedactNode(item, property, config));
                return redactedArray;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                string text = value.GetValue<string>();
                if (property is not null && ConsoleLinks.Contains(property))
                    return JsonValue.Create(Operator.Link(config, text, out _));
                return JsonValue.Create(property is not null && UrlProperties.Contains(property) ? Redact(text) : RedactUrlsIn(text));
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }
}
