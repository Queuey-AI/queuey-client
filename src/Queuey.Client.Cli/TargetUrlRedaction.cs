using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Queuey.Client.Cli;

// Kopiert ordrett fra Queuey: src/BuildingBlocks/Queuey.SharedKernel/Abstractions/Delivery/TargetUrlRedaction.cs på
// integration/agents (#514, b88ce69a, 2026-10-09). Bare using-linjene og navnerommet er klientens. Endres reglene der, kopieres
// fila på nytt; klienten har ingen egne regler for hva som skjules.


// En mottakers adresse slik en agent, en modell eller et sammendrag får lese den (F1.5, 2026-10-05; delt fra
// get_target_health 2026-10-05). En leverings-URL kan bære hemmeligheten: i spørringen (token=, sig=), i brukerinfoen
// (bruker:passord@), og hos mange webhook-mottakere i selve stien (Slack, Zapier, Discord, Make, Microsoft Teams). Det som
// når en agent, havner i transkriptet, og gjennom årsaksanalysen hos AI-leverandøren. Hele URL-en vises derfor aldri:
//   - spørringen, fragmentet og brukerinfoen tas aldri med;
//   - på en kjent webhook-vert vises ruten, og resten blir «…»;
//   - ellers vises et stisegment bare når det leses som et vanlig stiord: høyst 15 bokstaver, eventuelt ord bundet med -
//     eller ., der hvert ord ser ut som et ord (små bokstaver eller CamelCase, en vokal, ikke fem konsonanter på rad), en
//     versjon som v1, eller et tall på høyst fire sifre. Alt annet blir «…», og flere på rad blir ett.
//   - i tekst redigeres også en URL uten skjema (bruker:passord@vert/…, vert.no/sti), en prosentkodet URL (https%3A%2F%2F…)
//     og en JSON-escapet URL (https:\/\/…), og en URL løper til mellomrom, ", < eller >: tegn en URL aldri har (2026-10-09).
// Hvem som leser hva (2026-10-09): verktøyene på /mcp og modellene leser alltid redigert. Over REST leser en API-nøkkel og en
// innlogging (OAuth-tilkobling) hvert felt merket [ReceiverUrl] redigert (TargetUrlRedactionFilter i API-et); bare en person i
// konsollet leser hele URL-en. TargetUrlRedactionRatchetTests krever at hvert REST-felt som heter noe med URL, endpoint eller
// error, er merket eller står som Queueys eget.
//
// Fila står alene, uten typer fra resten av serveren: queuey-client kopierer den ordrett.

/// <summary>
/// A receiver's address as an agent or a model may read it: the scheme, the host and the path, never the query, the
/// fragment or the user info, and every part of the path that may carry a secret shown as <c>…</c>.
/// </summary>
public static class TargetUrlRedaction
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
    /// <see cref="HostAndPath"/>'s rules. A blank value stays as it is, a path alone (<c>/orders</c>) reads with the same
    /// rules for its path, and any other value that is not an absolute URL reads as <see cref="Hidden"/>, since nothing says
    /// what it carries.
    /// </summary>
    public static string? Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;
        // En sti alene (en køs egen sti som legges til workspacets base-URL, «/orders»): stien med reglene for en vert vi ikke
        // kjenner, uten spørring og fragment (2026-10-09).
        if (url.TrimStart().StartsWith('/'))
            return RedactLocation(url.Trim());
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
            var url = TrimToUrl(m.Value);
            return (Redact(url) ?? Hidden) + m.Value[url.Length..];
        });

        // Security-reviewen av queuey-client #71 (2026-10-09): det samme for en URL som er kodet, og en uten skjema.
        var encodedRedacted = EncodedUrl.Replace(urlsRedacted, m =>
        {
            var url = TrimToUrl(m.Value);
            return (Redact(Decoded(url)) ?? Hidden) + m.Value[url.Length..];
        });
        var userInfoRedacted = SchemelessUserInfo.Replace(encodedRedacted, m => Schemeless(m, encodedRedacted));
        var hostsRedacted = SchemelessHostPath.Replace(userInfoRedacted, m => Schemeless(m, userInfoRedacted));
        return LeftoverQuery.Replace(hostsRedacted, "?" + Hidden);
    }

    // Et tegn som hører til setningen rundt URL-en, ikke til den: tegnsetting, et apostrof, og en ) uten sin (.
    private static string TrimToUrl(string match)
    {
        var url = match;
        while (url.Length > 0)
        {
            var last = url[^1];
            if (SentencePunctuation.Contains(last) || last == '\'')
                url = url[..^1];
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    // https%3A%2F%2F… og https:\/\/… leses som URL-en de koder.
    private static string Decoded(string url)
        => url.Contains('%') ? Uri.UnescapeDataString(url) : url.Replace("\\/", "/", StringComparison.Ordinal);

    // En URL uten skjema redigeres som om den hadde https://, og vises uten. Et treff midt i et ord, en sti eller en adresse
    // (tegnet foran er en bokstav, et siffer eller ett av / @ . : - _ \ %) er ingen egen URL og står som det er: uttrykkene
    // sporer ikke tilbake, så de kan ikke se bakover selv.
    private static string Schemeless(Match m, string input)
    {
        if (m.Index > 0 && (char.IsLetterOrDigit(input[m.Index - 1]) || "/@.:-_\\%".Contains(input[m.Index - 1])))
            return m.Value;
        var url = TrimToUrl(m.Value);
        var redacted = Redact("https://" + url);
        return (redacted is null || redacted == Hidden ? Hidden : redacted["https://".Length..]) + m.Value[url.Length..];
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

    private static readonly char[] SentencePunctuation = ['.', ',', ';', ':', '!', '?'];

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
    // En URL løper til et mellomrom, ", < eller >, tegn en URL aldri har ukodet. ( ) og ' kan stå i en URL (et token, et
    // passord), og stoppet uttrykket der, ble resten stående: «https://h/x?token=(abc)» ble «https://h/x(abc)» (2026-10-09).
    // TrimToUrl gir setningen tilbake det som hører til den.
    private static readonly Regex AbsoluteUrl = new(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""<>]+", RegexOptions.NonBacktracking);
    private static readonly Regex EncodedUrl = new(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*(?::\\/\\/|%3[aA]%2[fF]%2[fF])[^\s""<>]+", RegexOptions.NonBacktracking);
    // bruker:passord@vert[:port][/sti]: brukerinfo uten skjema.
    private static readonly Regex SchemelessUserInfo = new(@"[^\s/@:""'<>()]+:[^\s/@""<>]*@[A-Za-z0-9][A-Za-z0-9.\-]*(?::[0-9]+)?(?:/[^\s""<>]*)?", RegexOptions.NonBacktracking);
    // vert.tld[:port]/sti: en vert med en sti, uten skjema (hooks.slack.com/services/…). En vert uten sti bærer ingen hemmelighet.
    private static readonly Regex SchemelessHostPath = new(@"(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,}(?::[0-9]+)?/[^\s""<>]*", RegexOptions.NonBacktracking);
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
    //
    // Bokstavene alene var ikke nok (security-reviewen av queuey-client #71, 2026-10-09): et token på høyst 15 bokstaver,
    // som «AbCdEfGhIjKlMn», ble vist. Hver del må nå se ut som et ord: små bokstaver (høyst tre, eller med en vokal og ikke
    // fem konsonanter på rad), CamelCase av slike ord på minst tre bokstaver, eller høyst fire store bokstaver (API, HTTP).
    private static readonly Regex OrdinaryWord = new(@"^[A-Za-z]+(?:[-.][A-Za-z]+)*$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^[vV][0-9]{1,3}$", RegexOptions.Compiled);
    private static readonly Regex ShortNumber = new(@"^[0-9]{1,4}$", RegexOptions.Compiled);
    private static readonly Regex CamelWords = new(@"^[A-Za-z][a-z]*(?:[A-Z][a-z]*)*$", RegexOptions.Compiled);

    private static bool IsOrdinaryPathWord(string segment)
        => Version.IsMatch(segment)
           || ShortNumber.IsMatch(segment)
           || (segment.Length <= 15 && OrdinaryWord.IsMatch(segment) && segment.Split('-', '.').All(ReadsAsWords));

    private static bool ReadsAsWords(string part)
    {
        if (part.All(char.IsUpper))
            return part.Length <= 4;
        if (part.All(char.IsLower))
            return part.Length <= 3 || ReadsAsWord(part);
        if (!CamelWords.IsMatch(part))
            return false;

        // CamelCase: hvert ord starter ved en stor bokstav.
        var words = new List<string>();
        var start = 0;
        for (var i = 1; i <= part.Length; i++)
        {
            if (i == part.Length || char.IsUpper(part[i]))
            {
                words.Add(part[start..i]);
                start = i;
            }
        }
        return words.All(w => w.Length >= 3 && ReadsAsWord(w.ToLowerInvariant()));
    }

    private static bool ReadsAsWord(string lower)
    {
        var hasVowel = false;
        var consonants = 0;
        foreach (var c in lower)
        {
            if ("aeiouy".Contains(c))
            {
                hasVowel = true;
                consonants = 0;
            }
            else if (++consonants >= 5)
                return false;
        }
        return hasVowel;
    }
}
