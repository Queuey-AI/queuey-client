using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class PushEventTests
{
    [Fact]
    public async Task PushEvent_maps_stream_eventtype_key_and_data_to_ingress()
    {
        var ingress = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(WaasTestHost.DefaultPublishBody()));
        QueueyService service = WaasTestHost.Build(ingressStub: ingress, streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(OrderCreated), null),
        });

        await service.PushEventAsync("order-events", "order.created", "cust_42", new { orderId = "ord_1" });

        HttpRequestMessage req = ingress.LastRequest!;
        Assert.Equal("https://ingress.example/events/ten_abc/order-events", req.RequestUri!.ToString());
        Assert.Equal("order.created", req.Headers.GetValues(QueueyHeaders.EventType).Single());
        Assert.Equal("cust_42", req.Headers.GetValues(QueueyHeaders.GroupKey).Single());
        Assert.Equal("{\"orderId\":\"ord_1\"}", Encoding.UTF8.GetString(ingress.LastBody!));
    }

    [Fact]
    public async Task PushEvent_without_key_omits_the_group_key_header()
    {
        var ingress = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(WaasTestHost.DefaultPublishBody()));
        QueueyService service = WaasTestHost.Build(ingressStub: ingress);

        await service.PushEventAsync("order-events", "order.created", key: null, new { x = 1 });

        Assert.False(ingress.LastRequest!.Headers.Contains(QueueyHeaders.GroupKey));
    }

    [Fact]
    public async Task PushEvent_by_model_resolves_stream_from_registry()
    {
        var ingress = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(WaasTestHost.DefaultPublishBody()));
        QueueyService service = WaasTestHost.Build(ingressStub: ingress, streams: new[]
        {
            StreamDefinitionFactory.FromType(typeof(OrderCreated), null),
        });

        await service.PushEventAsync<OrderCreated>("order.created", "cust_42", new OrderCreated { OrderId = "ord_1" });

        Assert.Equal("https://ingress.example/events/ten_abc/order-events", ingress.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task PushEvent_by_model_throws_when_model_not_registered()
    {
        QueueyService service = WaasTestHost.Build(); // empty registry

        await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => service.PushEventAsync<OrderCreated>("order.created", "cust_42", new OrderCreated()));
    }

    [Fact]
    public async Task PushEvent_with_options_carries_idempotency_and_source()
    {
        var ingress = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(WaasTestHost.DefaultPublishBody()));
        QueueyService service = WaasTestHost.Build(ingressStub: ingress);

        await service.PushEventAsync("order-events", "order.created", "cust_42", new { x = 1 },
            new PublishOptions { IdempotencyKey = "idem-1", Source = "orders-api" });

        HttpRequestMessage req = ingress.LastRequest!;
        Assert.Equal("idem-1", req.Headers.GetValues(QueueyHeaders.IdempotencyKey).Single());
        Assert.Equal("orders-api", req.Headers.GetValues(QueueyHeaders.Source).Single());
        // eventType/key still applied even though options was provided
        Assert.Equal("order.created", req.Headers.GetValues(QueueyHeaders.EventType).Single());
        Assert.Equal("cust_42", req.Headers.GetValues(QueueyHeaders.GroupKey).Single());
    }
}
