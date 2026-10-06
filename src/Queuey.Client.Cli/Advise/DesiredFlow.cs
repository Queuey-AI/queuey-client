using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Advise;

/// <summary>Where a value in a Desired Flow came from.</summary>
public enum Provenance
{
    /// <summary>The intent says so: the person, or the agent writing for them.</summary>
    Stated,

    /// <summary>The repository shows it, at the file and line its evidence names.</summary>
    Evidence,

    /// <summary>Neither: advise filled it in. Every assumed field is an assumption to put to the person.</summary>
    Assumed,
}

/// <summary>Where in the repository a value was read: the file, the line, and what is there, in words.</summary>
/// <param name="File">Repository-relative path.</param>
/// <param name="Line">1-based line, or null when the finding is the file itself.</param>
/// <param name="What">What was found there. Never a value copied from the file beyond a route, a name or a type.</param>
public sealed record FlowEvidence(string File, int? Line, string What)
{
    public override string ToString() => Line is { } line ? $"{File}:{line}" : File;
}

/// <summary>One field of a Desired Flow: its value, where the value came from, and the evidence for it.</summary>
public sealed record FlowValue(JsonNode Value, Provenance Provenance, IReadOnlyList<FlowEvidence> Evidence)
{
    public static FlowValue Of(string value, Provenance provenance, IEnumerable<FlowEvidence>? evidence = null)
        => new(JsonValue.Create(value)!, provenance, evidence?.ToArray() ?? Array.Empty<FlowEvidence>());

    public static FlowValue Of(bool value, Provenance provenance, IEnumerable<FlowEvidence>? evidence = null)
        => new(JsonValue.Create(value), provenance, evidence?.ToArray() ?? Array.Empty<FlowEvidence>());

    public static FlowValue Of(int value, Provenance provenance, IEnumerable<FlowEvidence>? evidence = null)
        => new(JsonValue.Create(value), provenance, evidence?.ToArray() ?? Array.Empty<FlowEvidence>());

    public static FlowValue Of(IEnumerable<string> values, Provenance provenance, IEnumerable<FlowEvidence>? evidence = null)
        => new(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), provenance,
            evidence?.ToArray() ?? Array.Empty<FlowEvidence>());

    public string? AsString => Value is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public bool? AsBool => Value is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public int? AsInt => Value is JsonValue v && v.TryGetValue(out int i) ? i : null;

    public IReadOnlyList<string> AsStrings =>
        Value is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToArray() : Array.Empty<string>();

    /// <summary>The same value with more evidence after what it has.</summary>
    public FlowValue WithEvidence(IEnumerable<FlowEvidence> more)
        => this with { Evidence = Evidence.Concat(more).Distinct().ToArray() };
}

/// <summary>
/// Something advise will not decide for the person. <see cref="Kind"/> says what: the repository contradicts the intent
/// (<c>contradiction</c>), points several ways the intent does not choose between (<c>ambiguous</c>), the intent asks
/// for what Queuey cannot do (<c>unsupported</c>), or the design needs something neither says (<c>missing</c>). While a
/// flow has one, advise proposes nothing.
/// </summary>
public sealed record FlowConflict(
    string Kind,
    string Field,
    JsonNode? Stated,
    JsonNode? Found,
    IReadOnlyList<FlowEvidence> Evidence,
    string Message,
    string Question);

/// <summary>The type of a Desired Flow field's value.</summary>
public enum FlowValueType
{
    String,
    Boolean,
    Integer,
    StringList,
}

/// <summary>One field a Desired Flow can have: its path, its type, the values it takes and what it means.</summary>
/// <param name="Path">Dotted path, such as <c>destination.route</c>.</param>
/// <param name="Type">The type of <c>value</c>.</param>
/// <param name="Values">The values it takes. With <paramref name="Closed"/> false, the ones advise knows; others are kept.</param>
/// <param name="Closed">Whether <paramref name="Values"/> is the whole list.</param>
/// <param name="Description">What the field means, for the schema.</param>
public sealed record FlowFieldSpec(
    string Path, FlowValueType Type, IReadOnlyList<string>? Values, bool Closed, string Description);

/// <summary>The fields a Desired Flow has, in the order it is written.</summary>
public static class FlowFields
{
    public static readonly IReadOnlyList<string> SourceKinds = new[] { "stripe", "supabase", "app" };

    public static readonly IReadOnlyList<string> Authentications = new[] { "stripe-signature", "api-key", "queuey-signature", "none" };

    public static readonly IReadOnlyList<string> Frameworks = new[]
    {
        "aspnet", "express", "fastify", "hono", "koa", "nestjs", "nextjs", "node", "supabase-edge", "fastapi", "flask", "go",
    };

    public static readonly IReadOnlyList<string> Verifications = new[] { "stripe-signature", "queuey-signature", "shared-secret", "none" };

    public static readonly IReadOnlyList<string> Orderings = new[] { "none", "per-key", "fifo" };

    public static readonly IReadOnlyList<FlowFieldSpec> All = new FlowFieldSpec[]
    {
        new("environment", FlowValueType.String, DeploymentWorkspace.EnvironmentValues, Closed: true,
            "The Queuey workspace environment the flow is set up in: dev, test, staging or prod. In dev the queue delivers to " +
            "a queuey listen session on the developer's machine; elsewhere it delivers over HTTP."),
        new("queue", FlowValueType.String, null, Closed: false,
            "The queue the flow goes through, by name: lowercase letters, digits, '.', '-' and '_'."),
        new("source.kind", FlowValueType.String, SourceKinds, Closed: false,
            "Who sends the events: stripe, supabase (a Database Webhook), or app (code in this repository publishes them). " +
            "Another value is kept, but advise designs only these."),
        new("source.authentication", FlowValueType.String, Authentications, Closed: true,
            "How the source proves an event is its own when it reaches Queuey's ingress: stripe-signature (Stripe signs every " +
            "webhook), api-key (a key in a header, as a Supabase Database Webhook or the app sends it), queuey-signature " +
            "(Queuey's own HMAC scheme), or none."),
        new("source.eventTypes", FlowValueType.StringList, null, Closed: false,
            "The event types the flow carries, such as checkout.session.completed, or INSERT and UPDATE for a Supabase table."),
        new("source.table", FlowValueType.String, null, Closed: false,
            "For a Supabase Database Webhook: the table whose row changes are the events, such as public.orders."),
        new("destination.route", FlowValueType.String, null, Closed: false,
            "The path Queuey delivers to, such as /api/stripe: the route of the handler that receives the events."),
        new("destination.framework", FlowValueType.String, Frameworks, Closed: false,
            "What serves the route: aspnet, express, fastify, hono, koa, nestjs, nextjs, node, supabase-edge, fastapi, flask " +
            "or go. Another value is kept."),
        new("destination.port", FlowValueType.Integer, null, Closed: false,
            "The port the receiver listens on while it is developed, where queuey listen --forward-to points."),
        new("destination.baseUrl", FlowValueType.String, null, Closed: false,
            "Where the receiver is reachable over HTTP, such as https://api.example.com: the base Queuey delivers to outside " +
            "dev. Never a local or private address, which Queuey's delivery does not reach."),
        new("destination.expectsRawBody", FlowValueType.Boolean, null, Closed: false,
            "Whether the receiver needs the exact bytes the source sent, as Stripe's constructEvent does."),
        new("requirements.verification", FlowValueType.String, Verifications, Closed: true,
            "What the receiver checks on each delivery: stripe-signature (Queuey signs each delivery again in Stripe's " +
            "format), queuey-signature, shared-secret (a secret in a header), or none."),
        new("requirements.ordering", FlowValueType.String, Orderings, Closed: true,
            "Whether the receiver needs events in order: none, per-key (in order per orderingKey), or fifo (the whole queue " +
            "in order, one at a time)."),
        new("requirements.orderingKey", FlowValueType.String, null, Closed: false,
            "With per-key ordering, where the key is: a top-level field of the JSON body (customer_id), or a header " +
            "(header:X-Customer-Id). Queuey's ingress reads only top-level body fields."),
        new("requirements.idempotent", FlowValueType.Boolean, null, Closed: false,
            "Whether the receiver handles the same event twice safely, such as by deduplicating on the event id. Queuey " +
            "delivers at least once."),
    };

    /// <summary>The groups fields sit under, in the order a flow is written.</summary>
    public static readonly IReadOnlyList<string> Groups = new[] { "source", "destination", "requirements" };

    internal static FlowFieldSpec? Find(string path) => All.FirstOrDefault(s => s.Path == path);
}

/// <summary>
/// The intent behind a Queuey flow, and why its deployment file looks as it does: the source, the destination and the
/// requirements, each field marked with where it came from. An agent writes the intent (fields marked
/// <see cref="Provenance.Stated"/>), and <c>queuey advise --intent</c> enriches it from the repository.
/// </summary>
/// <remarks>
/// It is an artifact, kept beside the code or in a pull request. It is not the configuration: <c>queuey.deploy.json</c> is,
/// and the only file <c>apply</c> reads. When the two drift apart, the deployment file holds, and the flow is updated or
/// archived.
/// </remarks>
public sealed class DesiredFlow
{
    private readonly Dictionary<string, FlowValue> _fields = new(StringComparer.Ordinal);

    /// <summary>What the flow takes for granted that is not a field, in words.</summary>
    public List<string> Assumptions { get; } = new();

    /// <summary>What advise will not decide. Advise fills this in; a flow it reads, it reads without.</summary>
    public List<FlowConflict> Conflicts { get; } = new();

    public FlowValue? this[string path] => _fields.TryGetValue(path, out FlowValue? value) ? value : null;

    public string? String(string path) => this[path]?.AsString;

    public bool? Bool(string path) => this[path]?.AsBool;

    public int? Int(string path) => this[path]?.AsInt;

    public IReadOnlyList<string> Strings(string path) => this[path]?.AsStrings ?? Array.Empty<string>();

    public bool IsStated(string path) => this[path]?.Provenance == Provenance.Stated;

    public void Set(string path, FlowValue value)
    {
        if (FlowFields.Find(path) is null)
            throw new ArgumentException($"A Desired Flow has no field {path}.", nameof(path));
        _fields[path] = value;
    }

    public void Remove(string path) => _fields.Remove(path);

    /// <summary>The fields it has, in the order the schema lists them.</summary>
    public IEnumerable<(FlowFieldSpec Spec, FlowValue Value)> Fields
        => FlowFields.All.Where(s => _fields.ContainsKey(s.Path)).Select(s => (s, _fields[s.Path]));

    /// <summary>
    /// The intent in this flow: the stated fields and the assumptions written in words. What advise filled in before is
    /// left out, so a flow advise returned can be passed back in: to keep a value it found, mark it stated.
    /// </summary>
    public DesiredFlow Intent()
    {
        var intent = new DesiredFlow();
        foreach ((FlowFieldSpec spec, FlowValue value) in Fields)
        {
            if (value.Provenance == Provenance.Stated)
                intent.Set(spec.Path, value);
        }
        intent.Assumptions.AddRange(Assumptions);
        return intent;
    }

    /// <summary>The flow as JSON, in the shape the schema describes.</summary>
    public JsonObject ToJson(string? schemaUrl)
    {
        var root = new JsonObject();
        if (schemaUrl is not null)
            root["$schema"] = schemaUrl;

        foreach ((FlowFieldSpec spec, FlowValue value) in Fields)
        {
            int dot = spec.Path.IndexOf('.');
            if (dot < 0)
            {
                root[spec.Path] = FieldJson(value);
                continue;
            }

            string group = spec.Path[..dot];
            if (root[group] is not JsonObject groupObject)
                root[group] = groupObject = new JsonObject();
            groupObject[spec.Path[(dot + 1)..]] = FieldJson(value);
        }

        // Gruppene i den rekkefølgen skjemaet har dem, uansett hvilket felt som kom først.
        foreach (string group in FlowFields.Groups)
        {
            if (root[group] is JsonObject g)
            {
                root.Remove(group);
                root[group] = g;
            }
        }

        root["assumptions"] = new JsonArray(Assumptions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        root["conflicts"] = new JsonArray(Conflicts.Select(ConflictJson).ToArray());
        return root;
    }

    internal static JsonObject FieldJson(FlowValue value)
    {
        var field = new JsonObject
        {
            ["value"] = value.Value.DeepClone(),
            ["provenance"] = ProvenanceText(value.Provenance),
        };
        if (value.Evidence.Count > 0)
            field["evidence"] = EvidenceJson(value.Evidence);
        return field;
    }

    public static JsonArray EvidenceJson(IEnumerable<FlowEvidence> evidence)
        => new(evidence.Select(e =>
        {
            var item = new JsonObject { ["file"] = e.File };
            if (e.Line is { } line)
                item["line"] = line;
            item["what"] = e.What;
            return (JsonNode?)item;
        }).ToArray());

    private static JsonNode ConflictJson(FlowConflict conflict)
    {
        var item = new JsonObject
        {
            ["kind"] = conflict.Kind,
            ["field"] = conflict.Field,
            ["stated"] = conflict.Stated?.DeepClone(),
            ["found"] = conflict.Found?.DeepClone(),
        };
        if (conflict.Evidence.Count > 0)
            item["evidence"] = EvidenceJson(conflict.Evidence);
        item["message"] = conflict.Message;
        item["question"] = conflict.Question;
        return item;
    }

    public static string ProvenanceText(Provenance provenance) => provenance switch
    {
        Provenance.Stated => "stated",
        Provenance.Evidence => "evidence",
        _ => "assumed",
    };

    /// <summary>
    /// Reads a Desired Flow. Throws <see cref="FlowFormatException"/> naming the place that is wrong: malformed JSON, a
    /// property the schema does not have, a field without its value or provenance, or a value of the wrong type or form.
    /// The message never repeats a value from the file. <c>conflicts</c> is read and dropped: advise works them out again.
    /// </summary>
    public static DesiredFlow Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            // Meldingen fra parseren kan sitere fila; linjen og posisjonen er nok til å finne feilen.
            throw new FlowFormatException(
                $"The intent is not valid JSON (line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1}).");
        }

        if (root is not JsonObject obj)
            throw new FlowFormatException("The intent must be a JSON object, as `queuey schema --flow` describes.");

        var flow = new DesiredFlow();
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            switch (property.Key)
            {
                case "$schema":
                    if (property.Value is not JsonValue v || !v.TryGetValue(out string? _))
                        throw new FlowFormatException("$schema must be a string.");
                    break;

                case "environment":
                case "queue":
                    flow.Set(property.Key, ReadField(FlowFields.Find(property.Key)!, property.Value, property.Key));
                    break;

                case "source":
                case "destination":
                case "requirements":
                    ReadGroup(flow, property.Key, property.Value);
                    break;

                case "assumptions":
                    flow.Assumptions.AddRange(ReadStrings(property.Value, "assumptions", allowEmptyItems: false));
                    break;

                case "conflicts":
                    if (property.Value is not JsonArray conflicts || conflicts.Any(c => c is not JsonObject))
                        throw new FlowFormatException("conflicts must be an array of objects.");
                    break;

                default:
                    throw new FlowFormatException($"The intent has a property the schema does not: {Shown(property.Key)}.");
            }
        }

        return flow;
    }

    private static void ReadGroup(DesiredFlow flow, string group, JsonNode? node)
    {
        if (node is not JsonObject obj)
            throw new FlowFormatException($"{group} must be an object.");

        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            string path = group + "." + property.Key;
            FlowFieldSpec spec = FlowFields.Find(path)
                ?? throw new FlowFormatException($"{group} has a property the schema does not: {Shown(property.Key)}.");
            flow.Set(path, ReadField(spec, property.Value, path));
        }
    }

    private static FlowValue ReadField(FlowFieldSpec spec, JsonNode? node, string path)
    {
        if (node is not JsonObject field)
            throw new FlowFormatException($"{path} must be an object with value and provenance, such as " +
                                          "{ \"value\": …, \"provenance\": \"stated\" }.");

        JsonNode? value = null;
        Provenance? provenance = null;
        IReadOnlyList<FlowEvidence> evidence = Array.Empty<FlowEvidence>();

        foreach (KeyValuePair<string, JsonNode?> property in field)
        {
            switch (property.Key)
            {
                case "value":
                    value = ReadValue(spec, property.Value, path + ".value");
                    break;
                case "provenance":
                    provenance = ReadProvenance(property.Value, path + ".provenance");
                    break;
                case "evidence":
                    evidence = ReadEvidence(property.Value, path + ".evidence");
                    break;
                default:
                    throw new FlowFormatException($"{path} has a property the schema does not: {Shown(property.Key)}. " +
                                                  "A field has value, provenance and evidence.");
            }
        }

        if (value is null)
            throw new FlowFormatException($"{path} needs a value.");
        if (provenance is null)
            throw new FlowFormatException($"{path} needs a provenance: stated, evidence or assumed.");

        return new FlowValue(value, provenance.Value, evidence);
    }

    private static JsonNode ReadValue(FlowFieldSpec spec, JsonNode? node, string path)
    {
        switch (spec.Type)
        {
            case FlowValueType.Boolean:
                if (node is JsonValue b && b.TryGetValue(out bool flag))
                    return JsonValue.Create(flag);
                throw new FlowFormatException($"{path} must be true or false.");

            case FlowValueType.Integer:
                if (node is JsonValue n && n.TryGetValue(out int number) && number is >= 1 and <= 65535)
                    return JsonValue.Create(number);
                throw new FlowFormatException($"{path} must be a whole number from 1 to 65535.");

            case FlowValueType.StringList:
                return new JsonArray(ReadStrings(node, path, allowEmptyItems: false)
                    .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

            default:
                if (node is not JsonValue s || !s.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
                    throw new FlowFormatException($"{path} must be a string that is not empty.");
                return JsonValue.Create(Normalize(spec, text.Trim(), path))!;
        }
    }

    /// <summary>A string field's value as advise compares it, or why it cannot be one.</summary>
    private static string Normalize(FlowFieldSpec spec, string text, string path)
    {
        if (spec.Values is { } values)
        {
            string? known = values.FirstOrDefault(v => v.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
                return known;
            if (spec.Closed)
                throw new FlowFormatException($"{path} must be one of {string.Join(", ", values)}.");
            // En åpen liste (kilde, rammeverk) tar andre verdier, med små bokstaver som de kjente.
            return text.ToLowerInvariant();
        }

        switch (spec.Path)
        {
            case "queue":
                if (QueueyName.Validate(text) is { } reason)
                    throw new FlowFormatException($"{path} is not a queue name Queuey accepts: {reason}");
                return text;

            case "destination.route":
                if (!text.StartsWith("/", StringComparison.Ordinal) || text.Contains("://", StringComparison.Ordinal)
                    || text.IndexOfAny(new[] { '?', '#', ' ' }) >= 0)
                    throw new FlowFormatException($"{path} must be a path that starts with /, without a query.");
                return text.Length > 1 ? text.TrimEnd('/') : text;

            case "destination.baseUrl":
                if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new FlowFormatException($"{path} must be an http or https URL without a query or credentials.");
                return text.TrimEnd('/');

            default:
                return text;
        }
    }

    private static Provenance ReadProvenance(JsonNode? node, string path)
    {
        string? text = node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return text?.Trim().ToLowerInvariant() switch
        {
            "stated" => Provenance.Stated,
            "evidence" => Provenance.Evidence,
            "assumed" => Provenance.Assumed,
            _ => throw new FlowFormatException($"{path} must be stated, evidence or assumed."),
        };
    }

    private static IReadOnlyList<FlowEvidence> ReadEvidence(JsonNode? node, string path)
    {
        if (node is not JsonArray items)
            throw new FlowFormatException($"{path} must be an array.");

        var evidence = new List<FlowEvidence>();
        for (int i = 0; i < items.Count; i++)
        {
            string at = $"{path}[{i}]";
            if (items[i] is not JsonObject item)
                throw new FlowFormatException($"{at} must be an object with a file.");

            string? file = null;
            int? line = null;
            string what = "";
            foreach (KeyValuePair<string, JsonNode?> property in item)
            {
                switch (property.Key)
                {
                    case "file":
                        file = property.Value is JsonValue f && f.TryGetValue(out string? fs) && !string.IsNullOrWhiteSpace(fs)
                            ? fs
                            : throw new FlowFormatException($"{at}.file must be a string that is not empty.");
                        break;
                    case "line":
                        line = property.Value is JsonValue l && l.TryGetValue(out int li) && li >= 1
                            ? li
                            : throw new FlowFormatException($"{at}.line must be a whole number from 1.");
                        break;
                    case "what":
                        what = property.Value is JsonValue w && w.TryGetValue(out string? ws)
                            ? ws ?? ""
                            : throw new FlowFormatException($"{at}.what must be a string.");
                        break;
                    default:
                        throw new FlowFormatException($"{at} has a property the schema does not: {Shown(property.Key)}.");
                }
            }

            evidence.Add(new FlowEvidence(file ?? throw new FlowFormatException($"{at} needs a file."), line, what));
        }

        return evidence;
    }

    private static IReadOnlyList<string> ReadStrings(JsonNode? node, string path, bool allowEmptyItems)
    {
        if (node is not JsonArray items)
            throw new FlowFormatException($"{path} must be an array of strings.");

        var strings = new List<string>();
        foreach (JsonNode? item in items)
        {
            if (item is not JsonValue v || !v.TryGetValue(out string? s) || (!allowEmptyItems && string.IsNullOrWhiteSpace(s)))
                throw new FlowFormatException($"{path} must be an array of strings that are not empty.");
            strings.Add(s!.Trim());
        }
        return strings;
    }

    /// <summary>A property name as an error may show it: the name when it reads as one, otherwise a stand-in.</summary>
    private static string Shown(string name)
        => name.Length is > 0 and <= 40 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '$')
            ? name
            : "a property whose name is not shown";
}

/// <summary>A Desired Flow that cannot be read: the message names the place, never a value.</summary>
public sealed class FlowFormatException : Exception
{
    public FlowFormatException(string message)
        : base(message)
    {
    }
}
