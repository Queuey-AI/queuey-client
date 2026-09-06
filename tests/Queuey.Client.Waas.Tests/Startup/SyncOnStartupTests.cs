using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class SyncOnStartupTests
{
    private static IServiceCollection Services(HttpMessageHandler api, bool syncOnStartup)
    {
        var services = new ServiceCollection();
        services.AddQueuey(
            o =>
            {
                o.ApiKey = "qak_kid.secret";
                o.TenantPublicId = "ten_abc";
                o.LicensePublicId = "lic_1";
                o.ApiBaseAddress = new Uri("https://api.example");
                o.IngressBaseAddress = new Uri("https://ingress.example");
            },
            b =>
            {
                b.AddStream<OrderCreated>();
                if (syncOnStartup) b.SyncOnStartup();
            });

        // Swap in the stubbed control plane the same way the DI registration composes the real one.
        services.AddSingleton<IQueueyService>(sp => WaasTestHost.Build(
            apiStub: api,
            streams: sp.GetRequiredService<StreamRegistry>().Streams));

        return services;
    }

    [Fact]
    public async Task Registers_a_hosted_service_that_applies_streams_on_start()
    {
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody()));

        using ServiceProvider sp = Services(api, syncOnStartup: true).BuildServiceProvider();
        IHostedService hosted = sp.GetServices<IHostedService>().Single();

        await hosted.StartAsync(CancellationToken.None);

        Assert.Single(api.Requests);
        Assert.EndsWith("/waas/streams", api.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_failed_sync_propagates_so_the_host_aborts_startup()
    {
        // The .NET host treats an exception out of StartAsync as fatal — that is the whole point:
        // a producer that could not converge its streams must not come up serving traffic.
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.Forbidden, new { error = new { code = "forbidden", message = "nope" } }));

        using ServiceProvider sp = Services(api, syncOnStartup: true).BuildServiceProvider();
        IHostedService hosted = sp.GetServices<IHostedService>().Single();

        await Assert.ThrowsAsync<QueueySyncException>(() => hosted.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void Is_off_unless_asked_for()
    {
        var api = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, WaasTestHost.DefaultApplyBody()));

        using ServiceProvider sp = Services(api, syncOnStartup: false).BuildServiceProvider();

        Assert.Empty(sp.GetServices<IHostedService>());
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void An_invalid_name_fails_at_registration_before_any_host_exists()
    {
        // The one name check runs as AddQueuey builds the registry — no host, no network, no startup.
        var services = new ServiceCollection();

        var ex = Assert.Throws<QueueyConfigurationException>(() => services.AddQueuey(
            o => o.ApiKey = "qak_kid.secret",
            b => b.AddStream("Order Events")));

        Assert.Contains("Did you mean 'order-events'?", ex.Message);
    }
}
