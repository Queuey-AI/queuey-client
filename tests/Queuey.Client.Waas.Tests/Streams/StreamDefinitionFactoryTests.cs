using System.Linq;

namespace Queuey.Client.Waas.Tests;

public class StreamDefinitionFactoryTests
{
    [Fact]
    public void Attribute_maps_name_description_eventtypes_and_public()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(OrderCreated), null);

        Assert.Equal("order-events", def.Name);
        Assert.Equal("Order lifecycle events", def.Description);
        Assert.Equal(new[] { "order.created", "order.paid" }, def.EventTypes.ToArray());
        Assert.True(def.IsPublic);
        Assert.Equal(typeof(OrderCreated), def.ModelType);
        Assert.Null(def.PayloadSchema); // schema generation is off in Phase 1
    }

    [Fact]
    public void Missing_attribute_falls_back_to_the_normalized_type_name_and_defaults()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(InvoiceIssued), null);

        // The CLR type name is a C# identifier, not a name anyone chose — so convention normalizes it
        // to something the server accepts. "InvoiceIssued" verbatim is rejected by the queue-name rules.
        Assert.Equal("invoice-issued", def.Name);
        Assert.True(def.IsPublic);
        Assert.Empty(def.EventTypes);
    }

    [Fact]
    public void Explicit_name_is_validated_not_rewritten()
    {
        // Silently lowercasing would leave PushEvent("Order-Events", ...) pointing at nothing.
        // The attribute and inline paths share one resolver; inline is the one an assembly scan
        // can't trip over, so the branch is covered here.
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => StreamDefinitionFactory.FromType(typeof(OrderCreated), new StreamOptions { Name = "Order-Events" }));

        Assert.Contains("stream name", ex.Message);
        Assert.Contains("Order-Events", ex.Message);
        Assert.Contains("Did you mean 'order-events'?", ex.Message);
    }

    [Fact]
    public void Name_only_stream_is_validated()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => StreamDefinitionFactory.FromName("Invoice Events", null));

        Assert.Contains("Did you mean 'invoice-events'?", ex.Message);
    }

    [Fact]
    public void Undeducible_type_name_names_the_type_and_says_how_to_fix_it()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => StreamDefinitionFactory.FromType(typeof(Sandbox), null));

        Assert.Contains("Sandbox", ex.Message);
        Assert.Contains("reserved", ex.Message);
        Assert.Contains("[QueueyModel(", ex.Message);
    }

    [Fact]
    public void Inline_options_take_precedence_over_attribute()
    {
        var overrides = new StreamOptions { Name = "custom-name", IsPublic = false, Description = "override" };
        overrides.EventTypes.Add("only.this");

        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(OrderCreated), overrides);

        Assert.Equal("custom-name", def.Name);
        Assert.False(def.IsPublic);
        Assert.Equal("override", def.Description);
        Assert.Equal(new[] { "only.this" }, def.EventTypes.ToArray());
    }

    [Fact]
    public void Name_only_stream_has_no_model_type()
    {
        StreamDefinition def = StreamDefinitionFactory.FromName("invoice-events", null);

        Assert.Equal("invoice-events", def.Name);
        Assert.Null(def.ModelType);
        Assert.True(def.IsPublic);
    }
}
