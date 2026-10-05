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
/// <c>$id</c> peker på release-taggen for pakkens versjon (2026-10-05), så fila skrives på nytt når versjonen bumpes.
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
    public void The_schema_id_names_the_release_tag_of_this_version_not_main()
    {
        // Review 2026-10-05: main er integrasjonsbranchen og kan beskrive felt ingen sluppet CLI godtar.
        string version = typeof(DeploymentFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

        Assert.Equal($"https://raw.githubusercontent.com/Queuey-AI/queuey-client/v{version}/schema/queuey.deploy.schema.json", DeploymentFile.SchemaUrl);
        Assert.DoesNotContain("/main/", DeploymentFile.SchemaUrl);
    }

    [Fact]
    public void The_committed_schema_id_names_the_version_this_is_built_as()
    {
        // Re-review 2026-10-05: $id pekte på v0.1.0-preview.8, en tag som finnes og aldri får fila. release.yml kjører testene
        // med taggens versjon, så en tag som ikke er den committede versjonen, stopper her før noe pakkes.
        string committedId = JsonNode.Parse(File.ReadAllText(SchemaPath))!["$id"]!.GetValue<string>();

        Assert.True(committedId == DeploymentFile.SchemaUrl,
            $"schema/queuey.deploy.schema.json names {committedId}, but this build is {DeploymentFile.SchemaUrl}. " +
            "A release tag must be the committed version: set <Version> in Directory.Build.props to it, regenerate the schema " +
            "(QUEUEY_UPDATE_SCHEMA=1 dotnet test --filter DeploymentSchemaTests), and tag that commit.");
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

        // Et filter uten conditions avvises, som parseren gjør (review 2026-10-05): en tom liste fjerner filteret, så en
        // manglende liste kan ikke leses som tom.
        Assert.Equal(new[] { "conditions" }, filter["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        JsonNode condition = Defs(schema, filter["properties"]!["conditions"]!["items"]!);
        Assert.Equal(new[] { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "exists" }, Values(condition["properties"]!["op"]!));

        // Ingress og levering har de samme lukkede verdiene som valideringen (2026-10-05).
        JsonNode workspace = schema["properties"]!["workspace"]!;
        JsonNode ingress = Defs(schema, workspace["properties"]!["ingress"]!);
        Assert.Equal(new[] { "None", "ApiKey", "SignedRequest", "ApiKeyAndSignedRequest" }, Values(ingress["properties"]!["authMode"]!));
        JsonNode source = Defs(schema, ingress["properties"]!["eventType"]!);
        Assert.Equal(new[] { "header", "query", "body" }, Values(source["properties"]!["from"]!));
        Assert.Equal(new[] { "name" }, source["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "from" }, source["then"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        JsonNode delivery = Defs(schema, workspace["properties"]!["delivery"]!);
        Assert.Equal(new[] { "None", "Bearer", "ApiKey", "Basic", "OAuth2ClientCredentials" }, Values(delivery["properties"]!["authMode"]!));
        Assert.Equal(new[] { "POST", "PUT", "PATCH" }, Values(delivery["properties"]!["method"]!));
        Assert.Equal(new[] { "None", "Bearer", "ApiKey", "Basic", "OAuth2ClientCredentials" },
            Values(Defs(schema, queue["properties"]!["delivery"]!)["properties"]!["authMode"]!));

        // Miljø-merket er en av de fire, eller en ${VAR} som skiller workspacene en fil brukes mot (Queuey F2.2, 2026-10-05).
        JsonNode environment = workspace["properties"]!["environment"]!;
        Assert.Equal(new[] { "dev", "test", "staging", "prod" }, Values(environment["anyOf"]![0]!));
        var variable = new Regex(environment["anyOf"]![1]!["pattern"]!.GetValue<string>());
        Assert.Matches(variable, "${QUEUEY_WORKSPACE_ENVIRONMENT}");
        Assert.Matches(variable, "${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}");
        Assert.DoesNotMatch(variable, "production");
        Assert.DoesNotMatch(variable, "pro${SUFFIX}");
        Assert.DoesNotMatch(variable, "${ENV}-eu");
        Assert.Contains("only a person lowers it", environment["description"]!.GetValue<string>());

        // idempotent er mottakerens løfte om duplikater, ikke deduplisering av publiseringer (review 2026-10-05).
        Assert.Contains("handles the same event twice", queue["properties"]!["idempotent"]!["description"]!.GetValue<string>());
        Assert.DoesNotContain("idempotency key", schema.ToJsonString());

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
        [(typeof(DeploymentIngress), nameof(DeploymentIngress.AuthMode))] = DeploymentIngress.AuthModeValues,
        [(typeof(ContextSource), nameof(ContextSource.From))] = ContextSource.FromValues,
        [(typeof(WorkspaceDelivery), nameof(WorkspaceDelivery.AuthMode))] = WorkspaceDelivery.AuthModeValues,
        [(typeof(WorkspaceDelivery), nameof(WorkspaceDelivery.Method))] = WorkspaceDelivery.MethodValues,
        [(typeof(QueueDelivery), nameof(QueueDelivery.AuthMode))] = WorkspaceDelivery.AuthModeValues,
    };

    // Felt som tar en av de lukkede verdiene eller en ${VAR} som utvides før apply: miljø-merket (Queuey F2.2, 2026-10-05), som
    // skiller workspacene en fil brukes mot. Valideringen sjekker verdien når variabelen er utvidet.
    private static readonly Dictionary<(Type, string), IReadOnlyList<string>> ClosedValuesOrVariable = new()
    {
        [(typeof(DeploymentWorkspace), nameof(DeploymentWorkspace.Environment))] = DeploymentWorkspace.EnvironmentValues,
    };

    /// <summary>
    /// The whole value is one <c>${VAR}</c> or <c>${VAR:-default}</c>, as DeploymentVariables expands it. Anchored (review
    /// of #45, 2026-10-06): a value with text around the variable is not an environment.
    /// </summary>
    internal const string VariablePattern = @"^\$\{[A-Za-z_][A-Za-z0-9_]*(:-[^}]*)?\}$";

    // Felt som må stå når typen er deklarert: de samme som valideringen krever (DeliveryFilter.Validate).
    private static readonly Dictionary<Type, string[]> RequiredWhenDeclared = new()
    {
        [typeof(DeliveryFilter)] = new[] { "conditions" },
    };

    /// <summary>
    /// A source needs its name, and a name that names something needs its <c>from</c> (2026-10-05): the same rules as
    /// <c>ContextSource.Validate</c>. An empty name removes the source, so it needs no <c>from</c>.
    /// </summary>
    private static void SourceRules(JsonObject source)
    {
        source["required"] = new JsonArray("name");
        source["if"] = new JsonObject { ["properties"] = new JsonObject { ["name"] = new JsonObject { ["minLength"] = 1 } } };
        source["then"] = new JsonObject { ["required"] = new JsonArray("from") };
    }

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

                if (RequiredWhenDeclared.TryGetValue(context.TypeInfo.Type, out string[]? required) && obj.ContainsKey("properties"))
                    obj["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());

                if (context.TypeInfo.Type == typeof(ContextSource) && obj.ContainsKey("properties"))
                    SourceRules(obj);

                if (context.PropertyInfo is { } property && property.AttributeProvider is MemberInfo member)
                {
                    if (docs.TryGetValue($"P:{member.DeclaringType!.FullName}.{member.Name}", out string? text))
                        obj.Insert(0, "description", text);

                    if (ClosedValues.TryGetValue((member.DeclaringType!, member.Name), out IReadOnlyList<string>? values))
                    {
                        var list = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                        obj["enum"] = list;
                    }

                    if (ClosedValuesOrVariable.TryGetValue((member.DeclaringType!, member.Name), out IReadOnlyList<string>? orVariable))
                    {
                        obj["anyOf"] = new JsonArray(
                            new JsonObject { ["enum"] = new JsonArray(orVariable.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) },
                            new JsonObject { ["pattern"] = VariablePattern });
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
