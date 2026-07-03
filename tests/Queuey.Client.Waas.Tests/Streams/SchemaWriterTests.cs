using System.Linq;
using System.Text.Json;

namespace Queuey.Client.Waas.Tests;

public class SchemaWriterTests
{
    [Fact]
    public void Generates_schema_honoring_types_ignore_and_rename()
    {
        string json = SchemaWriter.ForType(typeof(SchemaModel));
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement props = root.GetProperty("properties");

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("count").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("amount").GetProperty("type").GetString());
        Assert.Equal("boolean", props.GetProperty("active").GetProperty("type").GetString());
        Assert.Equal("date-time", props.GetProperty("at").GetProperty("format").GetString());
        Assert.Equal("array", props.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());

        // [JsonPropertyName] rename, and both ignore attributes drop the property.
        Assert.True(props.TryGetProperty("renamed", out _));
        Assert.False(props.TryGetProperty("original", out _));
        Assert.False(props.TryGetProperty("secret", out _));
        Assert.False(props.TryGetProperty("internal", out _));

        // Non-nullable value types are required; strings/arrays are not.
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("count", required);
        Assert.Contains("active", required);
        Assert.DoesNotContain("id", required);
        Assert.DoesNotContain("tags", required);
    }

    [Fact]
    public void Factory_generates_schema_when_attribute_opts_in()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(SchemaModel), null);
        Assert.NotNull(def.PayloadSchema);
        Assert.Contains("\"type\":\"object\"", def.PayloadSchema);
    }

    [Fact]
    public void Factory_leaves_schema_null_by_default()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(OrderCreated), null); // no GenerateSchema
        Assert.Null(def.PayloadSchema);
    }

    [Fact]
    public void Builder_GenerateSchemas_enables_generation_globally()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(OrderCreated), null, generateSchemasDefault: true);
        Assert.NotNull(def.PayloadSchema);
    }
}
