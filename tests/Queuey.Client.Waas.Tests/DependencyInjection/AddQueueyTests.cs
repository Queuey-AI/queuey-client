using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class AddQueueyTests
{
    [Fact]
    public void AddQueuey_resolves_service_with_populated_registry()
    {
        var services = new ServiceCollection();
        services.AddQueuey(
            o =>
            {
                o.ApiKey = "qak_kid.secret";
                o.TenantPublicId = "ten_abc";
                o.LicensePublicId = "lic_1";
            },
            b => b
                .AddStream<OrderCreated>()
                .AddStream("invoice-events", c => c.EventTypes.Add("invoice.issued")));

        using ServiceProvider sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<IQueueyService>();

        Assert.Equal(2, service.Registry.Streams.Count);
        Assert.True(service.Registry.TryGetByModel(typeof(OrderCreated), out _));
        Assert.True(service.Registry.TryGetByName("invoice-events", out _));
        Assert.NotNull(service.Client);
    }

    [Fact]
    public void AddQueuey_registers_the_stored_plans_as_their_own_interface_on_the_same_service()
    {
        // KAN 1 fra reviewen av #64: planene (Queuey F3.11) er et eget grensesnitt, som IQueueyManagement, så IQueueyService
        // ikke får nye medlemmer.
        var services = new ServiceCollection();
        services.AddQueuey(o => o.ApiKey = "qak_kid.secret");

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<IQueueyService>(), sp.GetRequiredService<IQueueyPlans>());
        Assert.DoesNotContain(typeof(IQueueyService).GetMethods(), m => m.Name.Contains("StoredPlan") || m.Name == "StorePlanAsync");
    }

    [Fact]
    public void AddQueuey_with_duplicate_stream_names_throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<QueueyConfigurationException>(() =>
            services.AddQueuey(
                o => o.ApiKey = "qak_kid.secret",
                b => b.AddStream<OrderCreated>().AddStream("order-events")));
    }
}
