using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Server;
using Queuey.Client;
using Queuey.Edge.Mqtt;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// The intake against a real in-process broker: a published message lands
/// in the publisher (durable spool in production) with the routed queue,
/// the lane from the topic, the topic as event type — and the broker is
/// acked only after that. A refused publish is left unacknowledged.
/// </summary>
public class MqttSourceTests
{
    [Fact]
    public async Task Message_is_routed_laned_and_acked_after_accept()
    {
        var port = FreePort();
        using var server = await StartBrokerAsync(port);
        var publisher = new RecordingPublisher();
        var options = Options(port, new MqttRoute { TopicFilter = "plant/+/alarms", Queue = "alarms", GroupKeySegment = 1 });
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => source.Counters.Received >= 0 && server.GetClientsAsync().Result.Count > 0);

        await PublishAsync(port, "plant/press-2/alarms", """{"code":"E42"}""");
        await WaitUntil(() => publisher.Calls.Count == 1);
        await source.StopAsync(CancellationToken.None);

        var call = Assert.Single(publisher.Calls);
        Assert.Equal("alarms", call.Queue);
        Assert.Equal("press-2", call.Options.GroupKey);
        Assert.Equal("plant/press-2/alarms", call.Options.EventType);
        Assert.Equal("mqtt:plant/press-2/alarms", call.Options.Source);
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal("""{"code":"E42"}""", Encoding.UTF8.GetString(call.Payload));
        Assert.Equal(1, source.Counters.Accepted);
    }

    [Fact]
    public async Task Refused_message_is_not_acknowledged_and_returns_on_reconnect()
    {
        var port = FreePort();
        using var server = await StartBrokerAsync(port);
        var publisher = new RecordingPublisher { Refuse = true };
        var options = Options(port, new MqttRoute { TopicFilter = "plant/#", Queue = "alarms" });
        options.ClientId = "queuey-edge-test-refuse";

        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);
        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => server.GetClientsAsync().Result.Count > 0);

        await PublishAsync(port, "plant/x/alarms", "{}");
        await WaitUntil(() => source.Counters.Refused >= 1);
        Assert.Empty(publisher.Calls.Where(c => c.Accepted));
        await source.StopAsync(CancellationToken.None);

        // Same client id, persistent session: the un-acked QoS 1 message
        // comes back, and this time the spool has room.
        publisher.Refuse = false;
        var again = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);
        await again.StartAsync(CancellationToken.None);
        await WaitUntil(() => publisher.Calls.Any(c => c.Accepted), TimeSpan.FromSeconds(10));
        await again.StopAsync(CancellationToken.None);

        Assert.Contains(publisher.Calls, c => c.Accepted && c.Queue == "alarms");
    }

    // ── support ──────────────────────────────────────────────────────

    private static MqttSourceOptions Options(int port, MqttRoute route)
    {
        var o = new MqttSourceOptions { Server = "127.0.0.1", Port = port, ReconnectDelay = TimeSpan.FromMilliseconds(100) };
        o.Routes.Add(route);
        return o;
    }

    private static async Task<MqttServer> StartBrokerAsync(int port)
    {
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).WithPersistentSessions().Build();
        var server = new MqttServerFactory().CreateMqttServer(options);
        await server.StartAsync();
        return server;
    }

    private static async Task PublishAsync(int port, string topic, string json)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId("test-producer-" + Guid.NewGuid().ToString("N")[..6]).Build());
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(json)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build());
        await client.DisconnectAsync();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class RecordingPublisher : IQueueyPublisher
    {
        public bool Refuse { get; set; }
        public List<(string Queue, byte[] Payload, string ContentType, PublishOptions Options, bool Accepted)> Calls { get; } = new();

        public Task<PublishReceipt> PublishAsync<T>(string queue, T payload, PublishOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PublishReceipt> PublishAsync(string queue, byte[] payload, string contentType, PublishOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Refuse)
            {
                Calls.Add((queue, payload, contentType, options ?? new PublishOptions(), false));
                throw new QueueySpoolFullException("test: spool full");
            }
            Calls.Add((queue, payload, contentType, options ?? new PublishOptions(), true));
            return Task.FromResult(new PublishReceipt { TransferId = Guid.NewGuid().ToString("N"), Queue = queue, AcceptedAtUtc = DateTimeOffset.UtcNow, OccurredAtUtc = DateTimeOffset.UtcNow });
        }
    }
}
