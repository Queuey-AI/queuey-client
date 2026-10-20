using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Edge.Mqtt;
using static Queuey.Edge.Mqtt.Tests.MqttSourceTests;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// The same promises against a real Mosquitto, which the in-process broker
/// cannot stand in for: Mosquitto stops sending to a client with 20
/// unacknowledged messages in flight, marks re-sends DUP, and keeps sessions
/// and retained messages between runs. Opt-in, because CI has no broker:
/// <code>
/// docker run --rm -d -p 1883:1883 --name queuey-mosquitto eclipse-mosquitto:2 \
///   sh -c 'printf "listener 1883\nallow_anonymous true\n" > /tmp/m.conf; mosquitto -c /tmp/m.conf'
/// QUEUEY_TEST_MOSQUITTO=localhost:1883 dotnet test tests/Queuey.Edge.Mqtt.Tests
/// </code>
/// Every run uses its own topics and client ids, and clears the retained
/// messages it made, so a long-lived broker stays clean.
/// </summary>
public sealed class MosquittoTests : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _prefix = $"queuey-test/{Guid.NewGuid():N}";
    private readonly List<string> _retainedTopics = new();

    public MosquittoTests()
    {
        var address = (Environment.GetEnvironmentVariable(MosquittoFactAttribute.Variable) ?? "localhost:1883").Split(':', 2);
        _host = address[0];
        _port = address.Length == 2 ? int.Parse(address[1]) : 1883;
    }

    [MosquittoFact]
    public async Task A_backlog_past_the_brokers_in_flight_window_arrives_whole_and_in_order()
    {
        // 50 un-acked messages: more than Mosquitto's 20 in flight. Without the
        // step-back, the broker would stop sending after 20 and hold the rest.
        var health = new HealthDouble { UsageBytes = HealthDouble.Limit };
        var publisher = new SpoolDouble { Full = true };
        var source = Source(publisher, health, $"{_prefix}/+/telemetry", "step-back");
        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => source.Snapshot().Connected, TimeSpan.FromSeconds(10));

        var expected = Enumerable.Range(1, 50).Select(i => $"m{i:00}").ToArray();
        await using (var producer = await Producer.ConnectAsync(_port, _host))
        {
            foreach (var body in expected)
                await producer.PublishAsync($"{_prefix}/pole-1/telemetry", body);
        }
        await WaitUntil(() => source.Snapshot().Paused, TimeSpan.FromSeconds(10));

        publisher.Full = false;
        health.UsageBytes = 0;
        await WaitUntil(() => publisher.Accepted.Count >= expected.Length, TimeSpan.FromSeconds(30));
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(expected, publisher.Accepted);
    }

    [MosquittoFact]
    public async Task Retained_state_is_taken_once_across_reconnects()
    {
        await PublishRetainedAsync($"{_prefix}/pole-1/state", "on");
        var publisher = new SpoolDouble();

        for (var start = 1; start <= 2; start++)
        {
            var source = Source(publisher, health: null, $"{_prefix}/+/state", "retained-once");
            await source.StartAsync(CancellationToken.None);
            await WaitUntil(() => source.Snapshot().Counters.Received >= 1, TimeSpan.FromSeconds(10));
            await source.StopAsync(CancellationToken.None);
        }

        Assert.Equal(new[] { "on" }, publisher.Accepted);
    }

    [MosquittoFact]
    public async Task A_live_message_on_a_retained_topic_is_taken_even_when_retained_copies_are_skipped()
    {
        var publisher = new SpoolDouble();
        var source = Source(publisher, health: null, $"{_prefix}/+/state", "live-retained", MqttRetainedMessages.Skip);
        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => source.Snapshot().Connected, TimeSpan.FromSeconds(10));
        await Task.Delay(300); // the subscribe follows the connect

        await PublishRetainedAsync($"{_prefix}/pole-1/state", "off");
        await WaitUntil(() => publisher.Accepted.Count >= 1, TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "off" }, publisher.Accepted);
    }

    [MosquittoFact]
    public async Task A_retained_copy_left_unacknowledged_is_taken_when_Mosquitto_sends_it_again()
    {
        await PublishRetainedAsync($"{_prefix}/pole-1/state", "on");
        var health = new HealthDouble { UsageBytes = HealthDouble.Limit };
        var publisher = new SpoolDouble { Full = true };
        var source = Source(publisher, health, $"{_prefix}/+/state", "retained-deferred");

        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => source.Snapshot().Paused, TimeSpan.FromSeconds(10));
        publisher.Full = false;
        health.UsageBytes = 0;
        await WaitUntil(() => publisher.Accepted.Count >= 1, TimeSpan.FromSeconds(10));
        await Task.Delay(500); // Mosquitto sends the DUP re-send, then the re-subscribe's fresh copy: one event
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "on" }, publisher.Accepted);
    }

    [MosquittoFact]
    public async Task A_message_the_producer_publishes_twice_is_one_event()
    {
        // The device re-sends at QoS 1 because its PUBACK was lost: Mosquitto delivers both, and the event id makes them one.
        await using var edge = RealEdge.Create();
        var options = Options(_port, new MqttRoute
        {
            TopicFilter = $"{_prefix}/+/telemetry", Queue = "telemetry", GroupKeySegment = 2, IdempotencyKeyField = "eventId"
        }, _host);
        options.ClientId = $"queuey-edge-test-id-dedup-{_prefix[^8..]}";
        var source = new MqttSource(options, edge.Publisher, NullLogger<MqttSource>.Instance);
        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => source.Snapshot().Connected, TimeSpan.FromSeconds(10));
        await Task.Delay(300); // the subscribe follows the connect

        await using (var producer = await Producer.ConnectAsync(_port, _host))
        {
            await producer.PublishAsync($"{_prefix}/node-07/telemetry", """{"eventId":"node-07-000123","temp":21.5}""");
            await producer.PublishAsync($"{_prefix}/node-07/telemetry", """{"eventId":"node-07-000123","temp":21.5}""");
        }
        // The outcome, not the receipt: a message is received before the spool has it.
        await WaitUntil(() => source.Snapshot().Counters is { Accepted: >= 1, Duplicates: >= 1 }, TimeSpan.FromSeconds(10));
        await source.StopAsync(CancellationToken.None);

        var counters = source.Snapshot().Counters;
        Assert.Equal(1, counters.Accepted);
        Assert.Equal(1, counters.Duplicates);
        Assert.Equal(1, await edge.PendingAsync());
    }

    [MosquittoFact]
    public async Task A_recreated_container_resumes_its_Mosquitto_session()
    {
        // The case found with samples/edge-docker: Edge stopped while the nodes kept publishing, then came back as a new
        // container. Mosquitto kept the readings in the old session; with the hostname in the client id, nothing came back.
        using var folder = new SpoolFolder();
        var mqtt = Configure(_port, new MqttRoute { TopicFilter = $"{_prefix}/+/telemetry", Queue = "telemetry", GroupKeySegment = 2 }, _host);
        var expected = Enumerable.Range(1, 40).Select(i => $"r{i:00}").ToArray();

        await using (var first = await EdgeGateway.StartAsync(folder.SpoolPath, $"c1-{_prefix[^8..]}", mqtt))
        {
            await WaitUntil(() => first.Source.Snapshot().Connected, TimeSpan.FromSeconds(10));
            await Task.Delay(300); // the subscribe follows the connect
        }

        await using (var producer = await Producer.ConnectAsync(_port, _host))
        {
            foreach (var body in expected)
                await producer.PublishAsync($"{_prefix}/node-{body[^1]}/telemetry", body);
        }

        await using var recreated = await EdgeGateway.StartAsync(folder.SpoolPath, $"c2-{_prefix[^8..]}", mqtt);
        await WaitUntil(() => recreated.Source.Snapshot().Counters.Accepted >= expected.Length, TimeSpan.FromSeconds(20));

        Assert.Equal(expected.Length, await recreated.PendingAsync());
    }

    [MosquittoFact]
    public async Task An_upgrade_keeps_the_Mosquitto_session_the_hostname_named()
    {
        using var folder = new SpoolFolder();
        await EdgeGateway.WriteSpoolFromBeforeAsync(folder.SpoolPath);
        var route = new MqttRoute { TopicFilter = $"{_prefix}/+/telemetry", Queue = "telemetry", GroupKeySegment = 2 };
        var hostname = $"gw-{_prefix[^8..]}";

        var before = new MqttSource(Options(_port, route, _host), new SpoolDouble(), NullLogger<MqttSource>.Instance)
        {
            MachineName = () => hostname
        };
        await before.StartAsync(CancellationToken.None);
        await WaitUntil(() => before.Snapshot().Connected, TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        await before.StopAsync(CancellationToken.None);

        await using (var producer = await Producer.ConnectAsync(_port, _host))
            await producer.PublishAsync($"{_prefix}/node-1/telemetry", "published during the upgrade");

        await using var upgraded = await EdgeGateway.StartAsync(folder.SpoolPath, hostname, Configure(_port, route, _host));
        await WaitUntil(() => upgraded.Source.Snapshot().Counters.Accepted >= 1, TimeSpan.FromSeconds(10));

        Assert.Equal($"queuey-edge-{hostname}", await upgraded.Spool.GetMetaAsync(EdgeMetaKeys.MqttClientId, CancellationToken.None));
    }

    private MqttSource Source(SpoolDouble publisher, HealthDouble? health, string filter, string name,
        MqttRetainedMessages retained = MqttRetainedMessages.FirstSubscribe)
    {
        var options = Options(_port, new MqttRoute { TopicFilter = filter, Queue = "telemetry", GroupKeySegment = 2 }, _host);
        options.ClientId = $"queuey-edge-test-{name}-{_prefix[^8..]}";
        options.RetainedMessages = retained;
        return new MqttSource(options, publisher, NullLogger<MqttSource>.Instance, health, intakes: null);
    }

    private async Task PublishRetainedAsync(string topic, string body)
    {
        _retainedTopics.Add(topic);
        await using var producer = await Producer.ConnectAsync(_port, _host);
        await producer.PublishAsync(topic, body, retain: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_retainedTopics.Count == 0) return;
        // An empty retained message clears the topic on the broker.
        await using var producer = await Producer.ConnectAsync(_port, _host);
        foreach (var topic in _retainedTopics)
            await producer.PublishAsync(topic, string.Empty, retain: true);
    }
}

/// <summary>Runs only when <c>QUEUEY_TEST_MOSQUITTO=host:port</c> points at a Mosquitto.</summary>
public sealed class MosquittoFactAttribute : FactAttribute
{
    public const string Variable = "QUEUEY_TEST_MOSQUITTO";

    public MosquittoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable}=host:port to run against a real Mosquitto.";
    }
}
