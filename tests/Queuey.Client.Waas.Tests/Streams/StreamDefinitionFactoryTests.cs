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
    public void Missing_attribute_falls_back_to_type_name_and_defaults()
    {
        StreamDefinition def = StreamDefinitionFactory.FromType(typeof(InvoiceIssued), null);

        Assert.Equal(nameof(InvoiceIssued), def.Name);
        Assert.True(def.IsPublic);
        Assert.Empty(def.EventTypes);
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
