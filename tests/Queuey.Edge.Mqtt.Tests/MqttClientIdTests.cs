using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Client;
using Queuey.Edge.Mqtt;
using static Queuey.Edge.Mqtt.Tests.MqttSourceTests;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// The client id names Edge's broker session: the broker queues for Edge under it while Edge is away, and a new id is a
/// new, empty session whose predecessor's messages are never delivered. These tests hold the id still across what
/// changes on a gateway (its hostname, its container, its spool) and let it change only when the operator sets one.
/// </summary>
public class MqttClientIdTests
{
    private static readonly MqttRoute Telemetry = new() { TopicFilter = "site/+/telemetry", Queue = "telemetry", GroupKeySegment = 1 };

    [Fact]
    public async Task A_recreated_container_on_the_same_spool_resumes_the_broker_session()
    {
        // A container's hostname is its id, new each time Compose recreates it. With the hostname in the client id, the
        // broker opened a new session for the new container, and what it had queued for Edge while the container was
        // being replaced stayed with the old session, never delivered.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        using var folder = new SpoolFolder();
        var mqtt = Configure(port, Telemetry);

        await using (await EdgeGateway.StartAsync(folder.SpoolPath, "3f2a9c1b7e4d", mqtt))
            await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "site/pole-1/telemetry", "published while the container was replaced");

        await using var recreated = await EdgeGateway.StartAsync(folder.SpoolPath, "9b8c7d6e5f4a", mqtt);
        await WaitUntil(() => recreated.Source.Snapshot().Counters.Accepted >= 1, TimeSpan.FromSeconds(10));

        Assert.Equal(1, await recreated.PendingAsync());
        Assert.Single(broker.ClientIds.Distinct());
    }

    [Fact]
    public async Task The_first_client_id_comes_from_the_node_id_and_is_one_every_broker_must_accept()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        using var folder = new SpoolFolder();

        await using var gateway = await EdgeGateway.StartAsync(folder.SpoolPath, "3f2a9c1b7e4d", Configure(port, Telemetry), nodeId: "gw-SN-48213");
        await broker.WaitForSubscriptionsAsync(1);

        // SHA-256("gw-SN-48213") begins aa08433e724a7 (shasum -a 256).
        var clientId = Assert.Single(broker.ClientIds);
        Assert.Equal("queueyedgeaa08433e724a7", clientId);
        Assert.Matches("^[0-9a-zA-Z]{1,23}$", clientId); // MQTT 3.1.1, 3.1.3.1: what every broker must accept
        await WaitForKeptAsync(gateway.Spool, clientId);
    }

    [Fact]
    public async Task Without_a_fixed_node_id_the_client_id_comes_from_the_id_minted_into_the_spool()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        using var folder = new SpoolFolder();

        await using var gateway = await EdgeGateway.StartAsync(folder.SpoolPath, "3f2a9c1b7e4d", Configure(port, Telemetry));
        await broker.WaitForSubscriptionsAsync(1);

        var nodeId = await gateway.Spool.GetMetaAsync(EdgeMetaKeys.NodeId, CancellationToken.None);
        Assert.True(Guid.TryParse(nodeId, out _), "the node mints its id when the intake first needs it");
        Assert.Equal(MqttClientId.ForNode(nodeId!), Assert.Single(broker.ClientIds));
    }

    [Fact]
    public async Task A_new_spool_with_the_same_fixed_node_id_finds_the_broker_session()
    {
        // A lost volume loses the kept id, but not the configured node id it was derived from.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        using var lost = new SpoolFolder();
        using var replacement = new SpoolFolder();
        var mqtt = Configure(port, Telemetry);

        await using (await EdgeGateway.StartAsync(lost.SpoolPath, "3f2a9c1b7e4d", mqtt, nodeId: "gw-SN-48213"))
            await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "site/pole-1/telemetry", "published while the volume was replaced");

        await using var gateway = await EdgeGateway.StartAsync(replacement.SpoolPath, "9b8c7d6e5f4a", mqtt, nodeId: "gw-SN-48213");
        await WaitUntil(() => gateway.Source.Snapshot().Counters.Accepted >= 1, TimeSpan.FromSeconds(10));

        Assert.Single(broker.ClientIds.Distinct());
    }

    [Fact]
    public async Task A_spool_from_before_Edge_kept_the_id_keeps_the_session_the_hostname_named()
    {
        // Edge 0.1.0-preview.13 connected as queuey-edge-<machine> and kept nothing. Its spool upgrades in place, and
        // whatever the broker queued during the upgrade waits in that session.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        using var folder = new SpoolFolder();
        await EdgeGateway.WriteSpoolFromBeforeAsync(folder.SpoolPath);

        var before = new MqttSource(Options(port, Telemetry), new SpoolDouble(), NullLogger<MqttSource>.Instance)
        {
            MachineName = () => "gateway-7"
        };
        await before.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);
        await before.StopAsync(CancellationToken.None);

        await PublishAsync(port, "site/pole-1/telemetry", "published during the upgrade");

        var mqtt = Configure(port, Telemetry);
        await using (var upgraded = await EdgeGateway.StartAsync(folder.SpoolPath, "gateway-7", mqtt))
        {
            await WaitUntil(() => upgraded.Source.Snapshot().Counters.Accepted >= 1, TimeSpan.FromSeconds(10));
            await WaitForKeptAsync(upgraded.Spool, "queuey-edge-gateway-7");
        }

        // From now on the spool holds the id, so a new hostname no longer changes it.
        await using var recreated = await EdgeGateway.StartAsync(folder.SpoolPath, "c0ffee123456", mqtt);
        await broker.WaitForSubscriptionsAsync(3);

        Assert.Equal(new[] { "queuey-edge-gateway-7" }, broker.ClientIds.Distinct());
    }

    [Fact]
    public async Task A_configured_client_id_is_used_kept_and_replaced_only_by_another_one()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        using var folder = new SpoolFolder();
        var route = Configure(port, Telemetry);
        Action<MqttSourceOptions> configured(string clientId) => m =>
        {
            route(m);
            m.ClientId = clientId;
        };

        await using (await EdgeGateway.StartAsync(folder.SpoolPath, "3f2a9c1b7e4d", configured("plant-7-edge")))
            await broker.WaitForSubscriptionsAsync(1);

        // The setting is gone and the hostname is new: the kept id still finds the session.
        await PublishAsync(port, "site/pole-1/telemetry", "published while Edge was away");
        await using (var unset = await EdgeGateway.StartAsync(folder.SpoolPath, "9b8c7d6e5f4a", route))
            await WaitUntil(() => unset.Source.Snapshot().Counters.Accepted >= 1, TimeSpan.FromSeconds(10));

        // Another configured id is a deliberate new session, and it is the one kept from then on.
        await using var renamed = await EdgeGateway.StartAsync(folder.SpoolPath, "9b8c7d6e5f4a", configured("plant-7-edge-b"));
        await broker.WaitForSubscriptionsAsync(3);
        await WaitForKeptAsync(renamed.Spool, "plant-7-edge-b");

        Assert.Equal(new[] { "plant-7-edge", "plant-7-edge", "plant-7-edge-b" }, broker.ClientIds);
    }

    [Fact]
    public void A_client_id_MQTT_cannot_carry_stops_the_source_at_composition()
    {
        var options = Options(1883, Telemetry);

        options.ClientId = "plant\u00007";
        Assert.Contains("U+0000", Assert.Throws<QueueyConfigurationException>(options.Validate).Message);

        options.ClientId = new string('x', MqttClientId.MaxBytes + 1);
        Assert.Contains("65535 bytes", Assert.Throws<QueueyConfigurationException>(options.Validate).Message);

        // Multibyte characters count as the bytes they take on the wire.
        options.ClientId = new string('é', MqttClientId.MaxBytes / 2 + 1);
        Assert.Throws<QueueyConfigurationException>(options.Validate);

        options.ClientId = "   "; // unset, as before: Edge chooses
        options.Validate();
    }

    [Fact]
    public void An_empty_configured_client_id_is_refused_rather_than_taken_as_unset()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() => MqttClientId.Validate("", "--mqtt-client-id"));

        Assert.StartsWith("--mqtt-client-id is empty", ex.Message);
    }

    [Fact]
    public void Node_ids_that_differ_give_client_ids_that_differ()
    {
        Assert.Equal("queueyedge06c60f21afe91", MqttClientId.ForNode("lighting-gw-demo-01"));
        Assert.NotEqual(MqttClientId.ForNode("lighting-gw-demo-01"), MqttClientId.ForNode("lighting-gw-demo-02"));
        Assert.Equal(MqttClientId.GuaranteedBytes, Encoding.UTF8.GetByteCount(MqttClientId.ForNode(new string('n', 64))));
    }

    /// <summary>The id is written once the broker accepted it, which is just after the connect the broker saw.</summary>
    private static async Task WaitForKeptAsync(IEventSpool spool, string clientId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (await spool.GetMetaAsync(EdgeMetaKeys.MqttClientId, CancellationToken.None) != clientId)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"the spool never kept {clientId}");
            await Task.Delay(25);
        }
    }
}
