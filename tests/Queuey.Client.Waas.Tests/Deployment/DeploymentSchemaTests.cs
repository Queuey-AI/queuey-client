using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// JSON-skjemaet for queuey.deploy.json genereres fra modellen, så det aldri beskriver et felt
/// parseren avviser, eller mangler et den godtar. Beskrivelsene er XML-dokumentasjonen, og de
/// tillatte verdiene er de samme listene valideringen bruker.
/// Skriv fila på nytt med: QUEUEY_UPDATE_SCHEMA=1 dotnet test --filter DeploymentSchemaTests
/// </summary>
public class DeploymentSchemaTests
{
    private static readonly string SchemaPath = Path.Combine(RepoRoot(), "schema", "queuey.deploy.schema.json");

    [Fact]
    public void The_committed_schema_is_generated_from_the_deployment_model()
    {
        string generated = Generate();

        if (Environment.GetEnvironmentVariable("QUEUEY_UPDATE_SCHEMA") == "1")
            File.WriteAllText(SchemaPath, generated);

        string committed = File.ReadAllText(SchemaPath).Replace("\r\n", "\n");
        Assert.True(committed == generated,
            "schema/queuey.deploy.schema.json is out of date with the deployment model. " +
            "Regenerate it: QUEUEY_UPDATE_SCHEMA=1 dotnet test --filter DeploymentSchemaTests");
    }

    [Fact]
    public void The_library_serves_the_committed_schema()
    {
        Assert.Equal(File.ReadAllText(SchemaPath).Replace("\r\n", "\n"), DeploymentFile.JsonSchema.Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_schema_names_the_values_each_closed_field_accepts()
    {
        JsonNode schema = JsonNode.Parse(Generate())!;
        JsonNode queue = schema["properties"]!["queues"]!["additionalProperties"]!;

        Assert.Equal(new[] { "deliver", "logOnly" }, Values(queue["properties"]!["mode"]!));
        Assert.Equal(new[] { "fifo", "bykey", "besteffort" }, Values(queue["properties"]!["ordering"]!));
        Assert.Equal(new[] { "none", "full" }, Values(Defs(schema, queue["properties"]!["backoff"]!)["properties"]!["jitter"]!));

        JsonNode filter = Defs(schema, queue["properties"]!["filter"]!);
        Assert.Equal(new[] { "all", "any" }, Values(filter["properties"]!["match"]!));
        JsonNode condition = Defs(schema, filter["properties"]!["conditions"]!["items"]!);
        Assert.Equal(new[] { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "exists" }, Values(condition["properties"]!["op"]!));

        // Strengt som parseren: et felt med skrivefeil er en feil, ikke noe som ignoreres.
        Assert.False(queue["additionalProperties"]!.GetValue<bool>());
        Assert.Contains("logs events until it", queue["properties"]!["mode"]!["description"]!.GetValue<string>());
    }

    private static string[] Values(JsonNode node)
        => node["enum"]!.AsArray().Where(v => v is not null).Select(v => v!.GetValue<string>()).ToArray();

    private static JsonNode Defs(JsonNode root, JsonNode node)
    {
        // Gjenbrukte typer kan havne bak en $ref; følg den.
        string? reference = node["$ref"]?.GetValue<string>();
        if (reference is null) return node;
        JsonNode? target = root;
        foreach (string part in reference.TrimStart('#', '/').Split('/'))
            target = target![part];
        return target!;
    }

    // ── generering ───────────────────────────────────────────────────────────

    // De lukkede verdisettene: de samme listene som valideringen i klienten sjekker mot.
    private static readonly Dictionary<(Type, string), IReadOnlyList<string>> ClosedValues = new()
    {
        [(typeof(DeploymentQueue), nameof(DeploymentQueue.Mode))] = DeploymentQueueModes.Values,
        [(typeof(DeploymentQueue), nameof(DeploymentQueue.Ordering))] = QueuePolicy.OrderingValues,
        [(typeof(DeploymentWorkspace), nameof(DeploymentWorkspace.Ordering))] = QueuePolicy.OrderingValues,
        [(typeof(RetryBackoff), nameof(RetryBackoff.Jitter))] = RetryBackoff.JitterValues,
        [(typeof(DeliveryFilter), nameof(DeliveryFilter.Match))] = DeliveryFilter.MatchValues,
        [(typeof(DeliveryFilterCondition), nameof(DeliveryFilterCondition.Op))] = DeliveryFilterCondition.OpValues,
    };

    private static string Generate()
    {
        Dictionary<string, string> docs = LoadXmlDocs();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject obj) return node;

                // Et felt som ikke er deklarert, utelates; null er aldri det man mener å skrive.
                // Uten null blir typene enkle, og enum-listene gjelder uten unntak.
                if (obj["type"] is JsonArray types && types.Count == 2 && types.Any(t => t?.GetValue<string>() == "null"))
                    obj["type"] = types.First(t => t?.GetValue<string>() != "null")!.GetValue<string>();

                if (context.PropertyInfo is { } property && property.AttributeProvider is MemberInfo member)
                {
                    if (docs.TryGetValue($"P:{member.DeclaringType!.FullName}.{member.Name}", out string? text))
                        obj.Insert(0, "description", text);

                    if (ClosedValues.TryGetValue((member.DeclaringType!, member.Name), out IReadOnlyList<string>? values))
                    {
                        var list = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                        obj["enum"] = list;
                    }
                }
                else if (context.PropertyInfo is null && context.TypeInfo.Type is { IsClass: true } type
                         && type != typeof(string) && docs.TryGetValue($"T:{type.FullName}", out string? typeText)
                         && !obj.ContainsKey("description"))
                {
                    obj.Insert(0, "description", typeText);
                }

                return obj;
            },
        };

        var schema = (JsonObject)options.GetJsonSchemaAsNode(typeof(DeploymentFile), exporter);
        schema.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        schema.Insert(1, "$id", DeploymentFile.SchemaUrl);
        schema.Insert(2, "title", "queuey.deploy.json");

        // Kønavnet er nøkkelen, og det valideres som navnet det er.
        var queues = (JsonObject)schema["properties"]!["queues"]!;
        queues["propertyNames"] = new JsonObject
        {
            ["pattern"] = "^[a-z0-9][a-z0-9._-]*$",
            ["maxLength"] = QueueyName.MaxLength,
        };

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    /// <summary>The library's XML documentation, member id → one line of plain text.</summary>
    private static Dictionary<string, string> LoadXmlDocs()
    {
        string path = Path.ChangeExtension(typeof(DeploymentFile).Assembly.Location, ".xml");
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement member in XDocument.Load(path).Descendants("member"))
        {
            if (member.Attribute("name")?.Value is not { } name || member.Element("summary") is not { } summary)
                continue;
            docs[name] = PlainText(summary);
        }
        return docs;
    }

    private static string PlainText(XElement summary)
    {
        string text = string.Concat(summary.Nodes().Select(Render));
        return Regex.Replace(text, @"\s+", " ").Trim();

        static string Render(XNode node) => node switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" } e => e.Attribute("cref")?.Value is { } cref
                ? cref.Substring(cref.LastIndexOfAny(new[] { '.', ':' }) + 1)
                : e.Attribute("langword")?.Value ?? string.Empty,
            XElement { Name.LocalName: "paramref" or "typeparamref" } e => e.Attribute("name")?.Value ?? string.Empty,
            XElement e => string.Concat(e.Nodes().Select(Render)) + (e.Name.LocalName == "para" ? " " : string.Empty),
            _ => string.Empty,
        };
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
