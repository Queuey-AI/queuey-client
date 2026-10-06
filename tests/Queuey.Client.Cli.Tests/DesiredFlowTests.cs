using System.Text.Json.Nodes;
using Queuey.Client.Cli.Advise;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Desired Flow som fil (F2.10): det agenten skriver, det advise leser, og skjemaet som beskriver begge. En feil i fila
/// skal peke på stedet uten å gjenta verdien, og skjemaet skal aldri beskrive et felt parseren avviser.
/// </summary>
public sealed class DesiredFlowTests
{
    // Eksempelet fra agent-user-stories (Desired Flow, T18), som det står.
    private const string StoryExample = """
        {
          "source": {
            "kind": { "value": "stripe", "provenance": "stated" },
            "authentication": {
              "value": "stripe-signature",
              "provenance": "evidence",
              "evidence": [{ "file": "src/Api/StripeWebhookController.cs", "line": 41 }]
            }
          },
          "destination": {
            "route": { "value": "/api/stripe", "provenance": "stated" },
            "framework": { "value": "aspnet", "provenance": "evidence" },
            "expectsRawBody": { "value": true, "provenance": "evidence" }
          },
          "assumptions": ["Handleren dedupliserer på Stripe-event-id"],
          "conflicts": []
        }
        """;

    [Fact]
    public void The_example_in_the_user_stories_reads_as_it_is()
    {
        DesiredFlow flow = DesiredFlow.Parse(StoryExample);

        Assert.Equal("stripe", flow.String("source.kind"));
        Assert.Equal(Provenance.Stated, flow["source.kind"]!.Provenance);
        Assert.Equal(Provenance.Evidence, flow["source.authentication"]!.Provenance);
        Assert.Equal(new FlowEvidence("src/Api/StripeWebhookController.cs", 41, ""), Assert.Single(flow["source.authentication"]!.Evidence));
        Assert.Equal("/api/stripe", flow.String("destination.route"));
        Assert.True(flow.Bool("destination.expectsRawBody"));
        Assert.Equal("Handleren dedupliserer på Stripe-event-id", Assert.Single(flow.Assumptions));
    }

    [Fact]
    public void Only_the_stated_fields_are_the_intent()
    {
        // advise regner ut evidens og antakelser på nytt hver gang. En flyt den returnerte, kan derfor sendes inn igjen.
        DesiredFlow intent = DesiredFlow.Parse(StoryExample).Intent();

        Assert.Equal(new[] { "source.kind", "destination.route" }, intent.Fields.Select(f => f.Spec.Path).ToArray());
        Assert.Single(intent.Assumptions);
    }

    [Fact]
    public void What_it_writes_reads_back_the_same()
    {
        DesiredFlow flow = DesiredFlow.Parse(StoryExample);
        flow.Conflicts.Add(new FlowConflict("contradiction", "destination.route", JsonValue.Create("/api/stripe"), null,
            new[] { new FlowEvidence("src/server.ts", 12, "POST /webhooks/stripe") }, "No handler takes it.", "Which route?"));

        JsonObject written = flow.ToJson(FlowSchema.Url);
        DesiredFlow again = DesiredFlow.Parse(written.ToJsonString());

        Assert.Equal(FlowSchema.Url, written["$schema"]!.GetValue<string>());
        Assert.Equal(flow.Fields.Select(f => (f.Spec.Path, f.Value.Value.ToJsonString(), f.Value.Provenance)),
                     again.Fields.Select(f => (f.Spec.Path, f.Value.Value.ToJsonString(), f.Value.Provenance)));
        Assert.Empty(again.Conflicts);   // advise regner dem ut på nytt
        Assert.Equal(new[] { "$schema", "source", "destination", "assumptions", "conflicts" }, written.Select(p => p.Key).ToArray());
    }

    [Theory]
    [InlineData("""{ "sauce": {} }""", "a property the schema does not: sauce")]
    [InlineData("""{ "source": { "kind": "stripe" } }""", "source.kind must be an object with value and provenance")]
    [InlineData("""{ "source": { "kind": { "value": "stripe" } } }""", "source.kind needs a provenance")]
    [InlineData("""{ "source": { "kind": { "provenance": "stated" } } }""", "source.kind needs a value")]
    [InlineData("""{ "source": { "kind": { "value": "stripe", "provenance": "guessed" } } }""", "source.kind.provenance must be stated, evidence or assumed")]
    [InlineData("""{ "source": { "kind": { "value": "stripe", "provenance": "stated", "why": "x" } } }""", "source.kind has a property the schema does not: why")]
    [InlineData("""{ "destination": { "route": { "value": "api/stripe", "provenance": "stated" } } }""", "destination.route.value must be a path that starts with /")]
    [InlineData("""{ "destination": { "port": { "value": 70000, "provenance": "stated" } } }""", "destination.port.value must be a whole number from 1 to 65535")]
    [InlineData("""{ "destination": { "expectsRawBody": { "value": "yes", "provenance": "stated" } } }""", "destination.expectsRawBody.value must be true or false")]
    [InlineData("""{ "requirements": { "ordering": { "value": "sorted", "provenance": "stated" } } }""", "requirements.ordering.value must be one of none, per-key, fifo")]
    [InlineData("""{ "environment": { "value": "qa", "provenance": "stated" } }""", "environment.value must be one of dev, test, staging, prod")]
    [InlineData("""{ "queue": { "value": "Orders Queue", "provenance": "stated" } }""", "queue.value is not a queue name Queuey accepts")]
    [InlineData("""{ "source": { "kind": { "value": "stripe", "provenance": "stated", "evidence": [{ "line": 3 }] } } }""", "source.kind.evidence[0] needs a file")]
    [InlineData("""[1, 2]""", "The intent must be a JSON object")]
    [InlineData("""{ "source": """, "The intent is not valid JSON")]
    public void A_flow_that_cannot_be_read_says_where(string json, string expected)
    {
        FlowFormatException ex = Assert.Throws<FlowFormatException>(() => DesiredFlow.Parse(json));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    // Hemmelighetene her er laget for testen, uten en leverandørs prefiks: GitHub avviser en push med noe som ligner en ekte
    // nøkkel, også en falsk (2026-10-06).
    [Theory]
    [InlineData("""{ "destination": { "baseUrl": { "value": "https://user:pasted-secret-0042@api.example.com", "provenance": "stated" } } }""", "pasted-secret-0042")]
    [InlineData("""{ "destination": { "route": { "value": "pasted-secret-0042", "provenance": "stated" } } }""", "pasted-secret-0042")]
    [InlineData("""{ "source": { "kind": { "value": "stripe", "provenance": "pasted-secret-0042" } } }""", "pasted-secret-0042")]
    [InlineData("""{ "pasted-secret-0042-that-is-longer-than-a-property-name": 1 }""", "pasted-secret-0042")]
    public void An_error_never_repeats_a_value_from_the_file(string json, string secret)
    {
        // Et agent-transkript tar vare på feilmeldingen. En hemmelighet limt inn på feil sted skal ikke stå der.
        FlowFormatException ex = Assert.Throws<FlowFormatException>(() => DesiredFlow.Parse(json));

        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_value_reads_in_any_casing_and_is_kept_as_the_schema_spells_it()
    {
        DesiredFlow flow = DesiredFlow.Parse("""
            {
              "environment": { "value": "DEV", "provenance": "Stated" },
              "source": { "kind": { "value": "Stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/api/stripe/", "provenance": "stated" } }
            }
            """);

        Assert.Equal("dev", flow.String("environment"));
        Assert.Equal("stripe", flow.String("source.kind"));
        Assert.Equal("/api/stripe", flow.String("destination.route"));
        Assert.Equal(Provenance.Stated, flow["environment"]!.Provenance);
    }

    [Fact]
    public void A_source_advise_does_not_know_is_kept_rather_than_refused()
    {
        // Lista over kilder er åpen: flyten er et artefakt som også kan beskrive en kilde advise ikke designer. advise
        // svarer med en konflikt, ikke med en lesefeil.
        DesiredFlow flow = DesiredFlow.Parse("""{ "source": { "kind": { "value": "GitHub", "provenance": "stated" } } }""");

        Assert.Equal("github", flow.String("source.kind"));
    }

    // ── skjemaet ─────────────────────────────────────────────────────────

    private static readonly string SchemaPath = Path.Combine(RepoRoot(), "schema", FlowSchema.FileName);

    [Fact]
    public void The_committed_schema_is_generated_from_the_fields()
    {
        string generated = FlowSchema.Generate();

        if (Environment.GetEnvironmentVariable("QUEUEY_UPDATE_SCHEMA") == "1")
            File.WriteAllText(SchemaPath, generated);

        Assert.True(File.Exists(SchemaPath), $"schema/{FlowSchema.FileName} is missing. Write it: QUEUEY_UPDATE_SCHEMA=1 dotnet test --filter DesiredFlowTests");
        string committed = File.ReadAllText(SchemaPath).Replace("\r\n", "\n");
        Assert.True(committed == generated,
            $"schema/{FlowSchema.FileName} is out of date with the Desired Flow's fields. " +
            "Regenerate it: QUEUEY_UPDATE_SCHEMA=1 dotnet test --filter DesiredFlowTests");
    }

    [Fact]
    public void The_schema_has_every_field_the_parser_reads_and_no_other()
    {
        JsonObject schema = JsonNode.Parse(FlowSchema.Json)!.AsObject();
        JsonObject properties = schema["properties"]!.AsObject();

        var described = new List<string>();
        foreach ((string name, JsonNode? node) in properties)
        {
            if (name is "$schema" or "assumptions" or "conflicts")
                continue;
            if (FlowFields.Groups.Contains(name))
                described.AddRange(node!["properties"]!.AsObject().Select(p => name + "." + p.Key));
            else
                described.Add(name);
        }

        Assert.Equal(FlowFields.All.Select(f => f.Path).OrderBy(p => p), described.OrderBy(p => p));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Null(schema["$id"]);   // en versjonsbump skal ikke kreve en ny fil
    }

    [Fact]
    public void Each_closed_field_names_the_values_the_parser_takes()
    {
        JsonObject properties = JsonNode.Parse(FlowSchema.Json)!["properties"]!.AsObject();

        foreach (FlowFieldSpec spec in FlowFields.All.Where(s => s.Closed))
        {
            JsonNode field = spec.Path.Contains('.')
                ? properties[spec.Path.Split('.')[0]]!["properties"]![spec.Path.Split('.')[1]]!
                : properties[spec.Path]!;
            string[] values = field["properties"]!["value"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
            Assert.Equal(spec.Values, values);
        }
    }

    [Fact]
    public void A_flow_advise_writes_names_the_schema_at_this_release()
    {
        Assert.Equal($"https://raw.githubusercontent.com/Queuey-AI/queuey-client/v{CliVersion.Current}/schema/queuey.flow.schema.json",
            FlowSchema.Url);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
