using System;
using System.Net.Http;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Tests;

public class BaseAddressTests
{
    private static StubHttpMessageHandler Ok202() => new(_ =>
        StubHttpMessageHandler.Accepted(new
        {
            queuePublicId = "que_1",
            eventId = "evt_1",
            receivedAtUtc = DateTimeOffset.UnixEpoch,
            mode = "Deliver",
            replayed = false,
        }));

    private static QueueyOptions LocalOptions(Uri ingressBase) => new()
    {
        ApiKey = "qak_kid.secret",
        TenantPublicId = "ten_abc",
        IngressBaseAddress = ingressBase,
    };

    [Fact]
    public async Task Local_http_ingress_override_is_honored()
    {
        var handler = Ok202();
        using var client = new QueueyClient(LocalOptions(new Uri("http://localhost:5084")), new HttpClient(handler));

        await client.Ingress.PublishAsync("orders", new { x = 1 });

        Assert.Equal("http://localhost:5084/events/ten_abc/orders", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Self_hosted_base_path_prefix_is_preserved()
    {
        var handler = Ok202();
        using var client = new QueueyClient(LocalOptions(new Uri("https://queuey.acme.io/gw/")), new HttpClient(handler));

        await client.Ingress.PublishAsync("orders", new { x = 1 });

        Assert.Equal("https://queuey.acme.io/gw/events/ten_abc/orders", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public void Relative_base_address_throws_configuration_exception()
    {
        var options = LocalOptions(new Uri("/nope", UriKind.Relative));
        Assert.Throws<QueueyConfigurationException>(() => new QueueyClient(options, new HttpClient(Ok202())));
    }

    [Fact]
    public void Non_http_scheme_base_address_throws_configuration_exception()
    {
        var options = LocalOptions(new Uri("ftp://queuey.acme.io"));
        Assert.Throws<QueueyConfigurationException>(() => new QueueyClient(options, new HttpClient(Ok202())));
    }
}
