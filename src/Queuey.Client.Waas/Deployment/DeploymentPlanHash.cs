using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Queuey.Client.Waas;

// Queuey F2.3 (2026-10-06): planen får en id og en hash over den normaliserte diffen og tilstanden på serveren, og F3.11 gjør
// den til et godkjenningsobjekt. Reglene for kanonisk JSON er de samme som Queuey sin CanonicalJson (versjon 1), og begge
// sider låser dem med de samme testvektorene. Tilstanden kommer fra serveren (stateHash i hver dry run), diffen er det
// serveren svarte, og for en kø som ikke finnes ennå, det apply ville sendt.

/// <summary>
/// The canonical text of a JSON value, version 1, and its SHA-256. The same value gives the same text whatever order its
/// members or elements came in: objects drop members whose value is null and sort the rest by name (ordinal), every array
/// sorts its elements by their canonical text, strings escape only <c>"</c>, <c>\</c> and control characters, and numbers
/// are written without exponent, sign of zero or trailing zeros. Queuey computes state hashes with the same rules.
/// </summary>
public static class CanonicalJson
{
    /// <summary>The version of these rules.</summary>
    public const int Version = 1;

    /// <summary>The canonical text of <paramref name="value"/>.</summary>
    public static string Write(JsonElement value)
    {
        var text = new StringBuilder();
        WriteValue(value, text);
        return text.ToString();
    }

    /// <summary><c>sha256:</c> and the lower-case hex SHA-256 of the canonical text's UTF-8 bytes.</summary>
    public static string Hash(JsonElement value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Write(value)));
        var hex = new StringBuilder("sha256:", 7 + digest.Length * 2);
        foreach (byte b in digest)
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }

    private static void WriteValue(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                List<JsonProperty> members = value.EnumerateObject()
                    .Where(m => m.Value.ValueKind != JsonValueKind.Null && m.Value.ValueKind != JsonValueKind.Undefined)
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ToList();
                text.Append('{');
                for (int i = 0; i < members.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    WriteString(members[i].Name, text);
                    text.Append(':');
                    WriteValue(members[i].Value, text);
                }
                text.Append('}');
                break;

            case JsonValueKind.Array:
                List<string> elements = value.EnumerateArray().Select(Write).OrderBy(e => e, StringComparer.Ordinal).ToList();
                text.Append('[').Append(string.Join(",", elements)).Append(']');
                break;

            case JsonValueKind.String:
                WriteString(value.GetString()!, text);
                break;

            case JsonValueKind.Number:
                text.Append(Number(value.GetRawText()));
                break;

            case JsonValueKind.True:
                text.Append("true");
                break;

            case JsonValueKind.False:
                text.Append("false");
                break;

            default:
                text.Append("null");
                break;
        }
    }

    private static void WriteString(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }

    private static string Number(string raw)
    {
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
        {
            string text = d.ToString(CultureInfo.InvariantCulture);
            if (text.IndexOf('.') >= 0)
                text = text.TrimEnd('0').TrimEnd('.');
            return text == "-0" ? "0" : text;
        }

        return double.Parse(raw, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The id and hash of a <see cref="DeploymentPlan"/>, version 1. The hash covers everything apply would change and the
/// server state it rests on, and nothing that does not change what apply does: not the order of the queues in the file,
/// of the steps or of the changes, not the file's formatting, and not the plan's notes or messages.
/// </summary>
/// <remarks>
/// <para>The hashed document is <c>{ "v": 1, "tenant": …, "queues": [ … ], "steps": [ … ] }</c>, in canonical form
/// (<see cref="CanonicalJson"/>). Each queue is <c>{ name, id }</c>, the id null for a queue apply would create, so a queue
/// made again under the same name is another plan. Each step is <c>{ target, aspect, creates, state, changes, desired,
/// refusal }</c>:</para>
/// <list type="bullet">
/// <item><c>state</c> is what the step rests on: the server's <c>stateHash</c> of the config the dry run started from, or
/// the mode the queue has when a declared mode needs no write;</item>
/// <item><c>changes</c> are the server's, each <c>{ path, from, to }</c>, leaving out any whose two sides are the same in
/// canonical form (a filter whose conditions only changed order);</item>
/// <item><c>desired</c> is what apply would send to a queue it creates, which no dry run can answer for;</item>
/// <item><c>refusal</c> is the code of a refusal, without its message.</item>
/// </list>
/// <para>The id is <c>plan_</c> and the first 24 hex characters of the hash, so the same file against the same state gives
/// the same id. Plans are not stored yet; once they are (Queuey F3.11), a stored plan carries an id of its own.</para>
/// </remarks>
public static class DeploymentPlanHash
{
    /// <summary>The version of the hashed document.</summary>
    public const int Version = 1;

    /// <summary>The plan's hash: <c>sha256:</c> and 64 hex characters.</summary>
    public static string Of(string tenant, IEnumerable<DeploymentPlanQueue> queues, IEnumerable<DeploymentPlanStep> steps)
    {
        var document = new JsonObject
        {
            ["v"] = Version,
            ["tenant"] = tenant,
            ["queues"] = new JsonArray(queues.Select(q => (JsonNode?)new JsonObject { ["name"] = q.Name, ["id"] = q.PublicId }).ToArray()),
            ["steps"] = new JsonArray(steps.Select(StepOf).ToArray<JsonNode?>()),
        };

        using JsonDocument parsed = JsonDocument.Parse(document.ToJsonString());
        return CanonicalJson.Hash(parsed.RootElement);
    }

    /// <summary>The plan's id for <paramref name="hash"/>.</summary>
    public static string IdOf(string hash) => "plan_" + hash.Substring("sha256:".Length, 24);

    private static JsonObject StepOf(DeploymentPlanStep step) => new()
    {
        ["target"] = step.Target,
        ["aspect"] = step.Aspect,
        ["creates"] = step.Creates,
        ["state"] = step.State,
        ["changes"] = new JsonArray(step.Changes
            .Where(c => !SameCanonically(c.From, c.To))
            .Select(c => (JsonNode?)new JsonObject
            {
                ["path"] = c.Path,
                ["from"] = NodeOf(c.From),
                ["to"] = NodeOf(c.To),
            })
            .ToArray()),
        ["desired"] = NodeOf(step.Desired),
        ["refusal"] = step.Error is { } error ? error.ErrorCode ?? FormattableString.Invariant($"status_{error.StatusCode ?? 0}") : null,
    };

    private static bool SameCanonically(JsonElement? a, JsonElement? b)
        => (a is null || a.Value.ValueKind == JsonValueKind.Null) && (b is null || b.Value.ValueKind == JsonValueKind.Null)
           || (a is { } x && b is { } y && string.Equals(CanonicalJson.Write(x), CanonicalJson.Write(y), StringComparison.Ordinal));

    private static JsonNode? NodeOf(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } v ? JsonNode.Parse(v.GetRawText()) : null;
}
