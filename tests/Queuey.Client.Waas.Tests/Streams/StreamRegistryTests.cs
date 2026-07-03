using System;

namespace Queuey.Client.Waas.Tests;

public class StreamRegistryTests
{
    [Fact]
    public void Indexes_by_name_and_model()
    {
        var registry = new StreamRegistry(new[]
        {
            StreamDefinitionFactory.FromType(typeof(OrderCreated), null),
            StreamDefinitionFactory.FromName("invoice-events", null),
        });

        Assert.True(registry.TryGetByName("order-events", out StreamDefinition byName));
        Assert.Equal(typeof(OrderCreated), byName.ModelType);
        Assert.True(registry.TryGetByModel(typeof(OrderCreated), out _));
        Assert.True(registry.TryGetByName("invoice-events", out _));
        Assert.False(registry.TryGetByName("nope", out _));
    }

    [Fact]
    public void Duplicate_stream_name_throws()
    {
        Assert.Throws<QueueyConfigurationException>(() => new StreamRegistry(new[]
        {
            StreamDefinitionFactory.FromName("order-events", null),
            StreamDefinitionFactory.FromType(typeof(OrderCreated), null), // also "order-events"
        }));
    }
}
