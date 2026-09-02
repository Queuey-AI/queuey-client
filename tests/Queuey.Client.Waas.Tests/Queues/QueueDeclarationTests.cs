using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class QueueDeclarationTests
{
    [Fact]
    public void Attribute_maps_name_description_and_declared_policy_only()
    {
        QueueDefinition def = QueueDefinitionFactory.FromType(typeof(OrderQueue), null);

        Assert.Equal("orders", def.Name);
        Assert.Equal("bykey", def.Policy.Ordering);
        Assert.Equal(8, def.Policy.DlqAfterAttempts);

        // Everything the attribute never set stays null = inherit from the workspace. This is the
        // whole contract: declaring a field takes ownership of it, leaving it does not.
        Assert.Null(def.Policy.DlqEnabled);
        Assert.Null(def.Policy.RetentionDays);
        Assert.Null(def.Policy.Idempotent);
    }

    [Fact]
    public void Bare_attribute_derives_the_name_and_inherits_everything()
    {
        QueueDefinition def = QueueDefinitionFactory.FromType(typeof(ShipmentUpdates), null);

        Assert.Equal("shipment-updates", def.Name);
        Assert.True(def.Policy.IsEmpty);
    }

    [Fact]
    public void Inline_options_are_laid_over_the_attribute_per_field()
    {
        QueueDefinition def = QueueDefinitionFactory.FromType(typeof(OrderQueue), new QueueOptions
        {
            Policy = { DlqAfterAttempts = 3, RetentionDays = 14 },
        });

        Assert.Equal(3, def.Policy.DlqAfterAttempts);     // inline wins
        Assert.Equal(14, def.Policy.RetentionDays);       // inline adds
        Assert.Equal("bykey", def.Policy.Ordering);       // attribute survives
    }

    [Fact]
    public void Explicit_queue_name_is_validated_not_rewritten()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => QueueDefinitionFactory.FromName("Order Events", null));

        Assert.Contains("queue name", ex.Message);
        Assert.Contains("Did you mean 'order-events'?", ex.Message);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("FIFO")]
    public void An_unknown_ordering_fails_locally(string ordering)
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => QueueDefinitionFactory.FromName("orders", new QueueOptions { Policy = { Ordering = ordering } }));

        Assert.Contains("Ordering must be one of", ex.Message);
    }

    [Fact]
    public void A_nonsense_attempt_count_fails_locally()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => QueueDefinitionFactory.FromName("orders", new QueueOptions { Policy = { DlqAfterAttempts = 0 } }));

        Assert.Contains("DlqAfterAttempts must be at least 1", ex.Message);
    }

    [Fact]
    public void Duplicate_queue_names_fail_at_registration()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() => new QueueRegistry(new[]
        {
            QueueDefinitionFactory.FromName("orders", null),
            QueueDefinitionFactory.FromName("orders", null),
        }));

        Assert.Contains("Duplicate Queuey queue name 'orders'", ex.Message);
    }

    [Fact]
    public void AddQueue_registers_through_DI_and_is_inspectable()
    {
        var services = new ServiceCollection();
        services.AddQueuey(
            o => o.ApiKey = "qak_kid.secret",
            b => b.AddQueue<OrderQueue>().AddQueue("audit-log"));

        using ServiceProvider sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<IQueueyService>();

        Assert.Equal(new[] { "orders", "audit-log" }, service.Queues.Queues.Select(q => q.Name).ToArray());
        Assert.True(service.Queues.TryGetByModel(typeof(OrderQueue), out QueueDefinition def));
        Assert.Equal("bykey", def.Policy.Ordering);
    }

    [Fact]
    public void Assembly_scan_finds_decorated_types_only()
    {
        var types = QueueDiscovery.TypesWithQueueyQueue(typeof(OrderQueue).Assembly);

        Assert.Contains(typeof(OrderQueue), types);
        Assert.Contains(typeof(ShipmentUpdates), types);
        Assert.DoesNotContain(typeof(OrderCreated), types);   // that one is a [QueueyModel] stream
    }
}
