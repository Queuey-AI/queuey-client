using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Queuey.Client.Cli.Advise;

/// <summary>
/// The JSON Schema for a Desired Flow, generated from <see cref="FlowFields"/>, so it cannot describe a field the parser
/// refuses or leave out one it reads. <c>queuey schema --flow</c> prints it, and <c>schema/queuey.flow.schema.json</c> is the
/// committed copy, published with each release at its tag.
/// </summary>
// Ingen $id i fila (F2.10): innholdet avhenger ikke av versjonen, så en versjonsbump rører den ikke. En flyt navngir skjemaet
// med $schema, som peker på kopien ved release-taggen til CLI-en som skrev den.
public static class FlowSchema
{
    public const string FileName = "queuey.flow.schema.json";

    /// <summary>The copy published with this CLI's release, at its tag: what a flow advise writes names in $schema.</summary>
    public static string Url => $"https://raw.githubusercontent.com/Queuey-AI/queuey-client/v{CliVersion.Current}/schema/{FileName}";

    public static string Json => LazyJson.Value;

    private static readonly Lazy<string> LazyJson = new(Generate);

    internal static string Generate()
    {
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Queuey Desired Flow",
            ["description"] =
                "What a Queuey flow is for, and why its deployment file looks as it does: the source, the destination and the " +
                "requirements, each field marked with where it came from. An agent writes the intent, with its fields marked " +
                "stated; queuey advise --intent fills in the rest from the repository (evidence, with file and line) or as " +
                "assumptions, and proposes the deployment file. The flow explains the configuration and is not it: apply reads " +
                "only queuey.deploy.json.",
            ["type"] = "object",
        };

        var properties = new JsonObject
        {
            ["$schema"] = new JsonObject
            {
                ["description"] = "The JSON Schema this flow follows: queuey schema --flow prints it, and each release publishes it at " +
                                  "https://raw.githubusercontent.com/Queuey-AI/queuey-client/v<version>/schema/queuey.flow.schema.json.",
                ["type"] = "string",
            },
        };

        foreach (FlowFieldSpec spec in FlowFields.All.Where(s => !s.Path.Contains('.')))
            properties[spec.Path] = Field(spec);

        foreach (string group in FlowFields.Groups)
        {
            var groupProperties = new JsonObject();
            foreach (FlowFieldSpec spec in FlowFields.All.Where(s => s.Path.StartsWith(group + ".", StringComparison.Ordinal)))
                groupProperties[spec.Path[(group.Length + 1)..]] = Field(spec);

            properties[group] = new JsonObject
            {
                ["description"] = group switch
                {
                    "source" => "Who sends the events, and how they prove it.",
                    "destination" => "Where Queuey delivers the events: the handler, what serves it, and what it needs.",
                    _ => "What the receiver needs from the delivery: what it verifies, the order, and whether a repeat is safe.",
                },
                ["type"] = "object",
                ["properties"] = groupProperties,
                ["additionalProperties"] = false,
            };
        }

        properties["assumptions"] = new JsonObject
        {
            ["description"] = "What the flow takes for granted that no field says, in words. The fields marked assumed are " +
                              "assumptions too.",
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
        };
        properties["conflicts"] = new JsonObject
        {
            ["description"] = "What advise will not decide: where the repository contradicts the intent, points several ways, or " +
                              "the intent asks for what Queuey cannot do. While there is one, advise proposes nothing. Advise " +
                              "fills this in; in an intent it is ignored.",
            ["type"] = "array",
            ["items"] = new JsonObject { ["$ref"] = "#/$defs/conflict" },
        };

        root["properties"] = properties;
        root["additionalProperties"] = false;
        root["$defs"] = new JsonObject
        {
            ["provenance"] = new JsonObject
            {
                ["description"] = "Where the value came from: stated (the intent says so), evidence (the repository shows it, at " +
                                  "the file and line in evidence) or assumed (advise filled it in, an assumption to put to the " +
                                  "person). Only stated values are intent: advise works the others out again each time.",
                ["enum"] = new JsonArray("stated", "evidence", "assumed"),
            },
            ["evidence"] = new JsonObject
            {
                ["description"] = "Where in the repository a value was read.",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["file"] = new JsonObject { ["description"] = "Repository-relative path.", ["type"] = "string", ["minLength"] = 1 },
                    ["line"] = new JsonObject { ["description"] = "The line, from 1.", ["type"] = "integer", ["minimum"] = 1 },
                    ["what"] = new JsonObject { ["description"] = "What is there, in words.", ["type"] = "string" },
                },
                ["required"] = new JsonArray("file"),
                ["additionalProperties"] = false,
            },
            ["conflict"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject
                    {
                        ["description"] = "contradiction: the repository contradicts the intent. ambiguous: it points several " +
                                          "ways, and the intent does not choose. unsupported: Queuey cannot do what the intent " +
                                          "asks. missing: the design needs what neither says.",
                        ["enum"] = new JsonArray("contradiction", "ambiguous", "unsupported", "missing"),
                    },
                    ["field"] = new JsonObject { ["description"] = "The flow field it is about, such as destination.route.", ["type"] = "string" },
                    ["stated"] = new JsonObject { ["description"] = "What the intent says, or null." },
                    ["found"] = new JsonObject { ["description"] = "What the repository shows, or null." },
                    ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/evidence" } },
                    ["message"] = new JsonObject { ["description"] = "What does not fit.", ["type"] = "string" },
                    ["question"] = new JsonObject { ["description"] = "What to ask the person.", ["type"] = "string" },
                },
                ["required"] = new JsonArray("kind", "field", "question"),
                ["additionalProperties"] = false,
            },
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
    }

    private static JsonObject Field(FlowFieldSpec spec) => new()
    {
        ["description"] = spec.Description,
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["value"] = Value(spec),
            ["provenance"] = new JsonObject { ["$ref"] = "#/$defs/provenance" },
            ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/evidence" } },
        },
        ["required"] = new JsonArray("value", "provenance"),
        ["additionalProperties"] = false,
    };

    private static JsonObject Value(FlowFieldSpec spec)
    {
        switch (spec.Type)
        {
            case FlowValueType.Boolean:
                return new JsonObject { ["type"] = "boolean" };
            case FlowValueType.Integer:
                return new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65535 };
            case FlowValueType.StringList:
                return new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } };
        }

        var value = new JsonObject { ["type"] = "string" };
        if (spec.Values is { } values)
        {
            var list = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            value[spec.Closed ? "enum" : "examples"] = list;
            if (!spec.Closed)
                value["minLength"] = 1;
            return value;
        }

        switch (spec.Path)
        {
            case "queue":
                value["pattern"] = "^[a-z0-9][a-z0-9._-]*$";
                value["maxLength"] = Queuey.Client.QueueyName.MaxLength;
                break;
            case "destination.route":
                value["pattern"] = "^/[^?#\\s]*$";
                break;
            case "destination.baseUrl":
                value["pattern"] = "^https?://[^?#\\s]+$";
                break;
            default:
                value["minLength"] = 1;
                break;
        }
        return value;
    }
}
