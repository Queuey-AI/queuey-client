using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Queuey.Client.Cli;

// Det CLI-en skriver fra et svar, leser en agent. Mottakerens adresser redigeres etter Queuey sine regler (RestTargetUrlRedaction,
// kopiert fra #514), og Queueys egne lenker (approvalUrl, consoleUrl) vises bare på verter brukeren valgte (Operator.Link,
// security-review av #71 runde 2, K-d). Serveren redigerer selv for nøkler og innlogginger etter #514; klienten gjør det også, så
// en eldre server eller en person i konsollet aldri gir hele URL-en videre gjennom CLI-en.

/// <summary>An answer from Queuey as the CLI shows it: receiver addresses redacted, console links checked.</summary>
internal static class AnswerRedaction
{
    /// <summary>Queuey's own console links: kept when <see cref="Operator.Link"/> takes them, null otherwise, at any depth.</summary>
    private static readonly HashSet<string> ConsoleLinks = new(StringComparer.OrdinalIgnoreCase) { "approvalUrl", "consoleUrl" };

    /// <summary><paramref name="element"/> redacted for showing; null for null.</summary>
    public static JsonElement? RedactJson(JsonElement? element, ResolvedConfig config)
    {
        if (element is not { } e) return null;
        JsonNode? node = RestTargetUrlRedaction.RedactJson(JsonNode.Parse(e.GetRawText()));
        CheckLinks(node, config);
        return node is null ? JsonDocument.Parse("null").RootElement.Clone() : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    // Security-review av #72 (N1): etter redigeringen etter feltnavn går RedactUrlsIn over hver streng som er igjen, også
    // strenger rett i en array, som RestTargetUrlRedaction ikke rører. Det fanger responsePreview, payloadHeadersJson og
    // annen tekst som kan sitere en adresse. Queueys egne lenker går gjennom Operator.Link i stedet.
    private static void CheckLinks(JsonNode? node, ResolvedConfig config)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string name, JsonNode? child) in obj.ToList())
                {
                    // Queuey #517: svarheadere leses som headere, så en relativ Location (/webhooks/<token>/) også redigeres.
                    if (name.EndsWith("HeadersJson", StringComparison.OrdinalIgnoreCase) && child is JsonValue headersJson
                        && headersJson.TryGetValue(out string? headersText))
                        obj[name] = RestTargetUrlRedaction.RedactHeadersJson(headersText);
                    else if (name.EndsWith("Headers", StringComparison.OrdinalIgnoreCase) && child is JsonObject headers)
                    {
                        foreach ((string header, JsonNode? headerValue) in headers.ToList())
                        {
                            if (headerValue is JsonValue hv && hv.TryGetValue(out string? hvText))
                                headers[header] = TargetUrlRedaction.RedactHeaderValue(header, hvText);
                            else
                                CheckLinks(headerValue, config);
                        }
                    }
                    else if (child is JsonValue value && value.TryGetValue(out string? text))
                    {
                        if (ConsoleLinks.Contains(name))
                        {
                            string? shown = Operator.Link(config, text, out bool withheld);
                            obj[name] = shown;
                            // En lenke som ble holdt tilbake, sies fra om ved siden av, så svaret ikke later som det ikke fantes en.
                            if (withheld)
                                obj["linkWithheld"] = Operator.Withheld;
                        }
                        else
                            obj[name] = TargetUrlRedaction.RedactUrlsIn(text);
                    }
                    else
                        CheckLinks(child, config);
                }
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue item && item.TryGetValue(out string? text))
                        array[i] = TargetUrlRedaction.RedactUrlsIn(text);
                    else
                        CheckLinks(array[i], config);
                }
                break;
        }
    }
}
