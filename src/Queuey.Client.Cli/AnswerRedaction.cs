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

    private static void CheckLinks(JsonNode? node, ResolvedConfig config)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string name, JsonNode? child) in obj.ToList())
                {
                    if (ConsoleLinks.Contains(name) && child is JsonValue value && value.TryGetValue(out string? link))
                    {
                        string? shown = Operator.Link(config, link, out bool withheld);
                        obj[name] = shown;
                        // En lenke som ble holdt tilbake, sies fra om ved siden av, så svaret ikke later som det ikke fantes en.
                        if (withheld)
                            obj["linkWithheld"] = Operator.Withheld;
                    }
                    else
                        CheckLinks(child, config);
                }
                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                    CheckLinks(item, config);
                break;
        }
    }
}
