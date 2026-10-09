using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Queuey.Client.Cli;

// Kopiert fra Queuey: src/BuildingBlocks/Queuey.SharedKernel/Abstractions/Delivery/RestTargetUrlRedaction.cs på
// integration/agents (#517, ad6596f5). Utelatt: AppliesTo, som avgjør hvem serveren redigerer for (IRequestContext); klienten
// redigerer alt den viser, uansett hvem som er logget inn.

// Security-reviewen av queuey-client #71 (2026-10-09): CLI-en driver feilsøking over REST, og REST serverte mottakerens URL hel
// der /mcp redigerer den. Egen fil, så TargetUrlRedaction står alene og klienten kan kopiere den ordrett.

/// <summary>Which REST callers read a receiver's URL redacted (<see cref="TargetUrlRedaction"/>, <see cref="ReceiverUrlAttribute"/>).</summary>
public static class RestTargetUrlRedaction
{
    /// <summary>JSON property names that hold an address, and the last segment of a diff path that does.</summary>
    public static readonly HashSet<string> UrlNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "baseUrl", "targetUrl", "targetEndpoint", "effectiveUrl", "endpoint", "endpointUrl", "receiverUrl",
    };

    /// <summary>JSON property names that hold text that can quote an address.</summary>
    public static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "errorMessage", "error", "lastError", "message", "detail", "location",
        "responsePreview", "responseSample", "responseBody", "summary",
    };

    /// <summary>True when a diff path (<c>delivery.baseUrl</c>, <c>targets[0].url</c>) names an address.</summary>
    public static bool NamesAnAddress(string? path)
        => path is not null && UrlNames.Contains(path[(path.LastIndexOf('.') + 1)..].Split('[')[0]);

    /// <summary>
    /// Redacts <paramref name="node"/> in place: properties named in <see cref="UrlNames"/> as addresses, those in
    /// <see cref="TextNames"/> as text, and both sides of a diff entry (<c>{ path, from, to }</c>) whose path names an address.
    /// </summary>
    public static JsonNode? RedactJson(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var diffOfAddress = obj["path"] is JsonValue p && p.TryGetValue<string>(out var path) && NamesAnAddress(path);
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (UrlNames.Contains(name) || (diffOfAddress && name is "from" or "to"))
                            obj[name] = TargetUrlRedaction.Redact(text);
                        else if (TextNames.Contains(name))
                            obj[name] = TargetUrlRedaction.RedactUrlsIn(text);
                    }
                    else
                        RedactJson(child);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    RedactJson(item);
                break;
        }
        return node;
    }

    /// <summary>
    /// A receiver's response headers stored as JSON (<c>{"Location":"…"}</c>), with each value read as
    /// <see cref="TargetUrlRedaction.RedactHeaderValue"/> and serialized back, so the result stays valid JSON. Text that is not
    /// such an object has the URLs in it redacted instead.
    /// </summary>
    public static string? RedactHeadersJson(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
            return headersJson;
        Dictionary<string, string>? headers;
        try
        {
            headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return TargetUrlRedaction.RedactUrlsIn(headersJson);
        }
        return headers is null ? headersJson : System.Text.Json.JsonSerializer.Serialize(TargetUrlRedaction.RedactHeaders(headers), Unescaped);
    }

    // Markøren skrives som «…», ikke «\u2026», så den som leser teksten uten å tolke JSON-en, også ser den.
    private static readonly System.Text.Json.JsonSerializerOptions Unescaped =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
