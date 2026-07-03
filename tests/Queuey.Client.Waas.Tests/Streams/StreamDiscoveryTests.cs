using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Queuey.Client.Waas.Tests;

public class StreamDiscoveryTests
{
    [Fact]
    public void Finds_decorated_types_and_excludes_undecorated()
    {
        var types = StreamDiscovery.TypesWithQueueyModel(typeof(OrderCreated).Assembly);

        Assert.Contains(typeof(OrderCreated), types);
        Assert.DoesNotContain(typeof(InvoiceIssued), types); // no [QueueyModel]
    }

    [Fact]
    public void AddStreamsFromAssembly_registers_discovered_streams()
    {
        var services = new ServiceCollection();
        services.AddQueuey(
            o => o.ApiKey = "qak_kid.secret",
            b => b.AddStreamsFromAssemblyContaining<OrderCreated>());

        using ServiceProvider sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<IQueueyService>();

        Assert.True(service.Registry.TryGetByModel(typeof(OrderCreated), out StreamDefinition def));
        Assert.Equal("order-events", def.Name);
    }
}
