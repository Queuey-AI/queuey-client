using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
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
        using var broker = await TestBroker.StartAsync(port);
        var publisher = new SpoolDouble();
        var options = Options(port, new MqttRoute { TopicFilter = "plant/+/alarms", Queue = "alarms", GroupKeySegment = 1 });
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "plant/press-2/alarms", """{"code":"E42"}""");
        await WaitUntil(() => publisher.Calls.Count == 1);
        await source.StopAsync(CancellationToken.None);

        var call = Assert.Single(publisher.Calls);
        Assert.Equal("alarms", call.Queue);
        Assert.Equal("press-2", call.Options.GroupKey);
        Assert.Equal("plant/press-2/alarms", call.Options.EventType);
        Assert.Equal("mqtt:plant/press-2/alarms", call.Options.Source);
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal("""{"code":"E42"}""", call.Body);
        Assert.Equal(1, source.Snapshot().Counters.Accepted);
    }

    [Fact]
    public async Task Refused_message_is_not_acknowledged_and_returns_on_reconnect()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var publisher = new SpoolDouble { Full = true };
        var options = Options(port, new MqttRoute { TopicFilter = "plant/#", Queue = "alarms" });
        options.ClientId = "queuey-edge-test-refuse";

        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);
        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "plant/x/alarms", "{}");
        await WaitUntil(() => source.Snapshot().Counters.Deferred >= 1);
        Assert.Empty(publisher.Accepted);
        await source.StopAsync(CancellationToken.None);

        // Same client id, persistent session: the un-acked QoS 1 message
        // comes back, and this time the spool has room.
        publisher.Full = false;
        var again = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);
        await again.StartAsync(CancellationToken.None);
        await WaitUntil(() => publisher.Accepted.Count >= 1, TimeSpan.FromSeconds(10));
        await again.StopAsync(CancellationToken.None);

        Assert.Equal("{}", Assert.Single(publisher.Accepted));
    }

    [Fact]
    public async Task A_full_spool_steps_back_and_the_refused_message_returns_without_a_restart()
    {
        // The case a long uplink outage produces: the spool fills while the
        // broker connection stays perfectly healthy. MQTT re-sends an
        // un-acked message only on a reconnect, so the source has to make one.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var health = new HealthDouble { UsageBytes = HealthDouble.Limit };
        var publisher = new SpoolDouble { Full = true };
        var options = Options(port, new MqttRoute { TopicFilter = "site/+/telemetry", Queue = "telemetry", GroupKeySegment = 1 });
        options.ClientId = "queuey-edge-test-step-back";
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance, health, intakes: null);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "site/pole-1/telemetry", "A");
        await WaitUntil(() => source.Snapshot() is { Paused: true, Connected: false });

        // Published while the source stands back: the broker queues it in the session.
        await PublishAsync(port, "site/pole-1/telemetry", "B");
        await Task.Delay(300);
        Assert.Empty(publisher.Accepted);
        Assert.True(source.Snapshot().Paused, "the source must stay back while the spool has no room");

        // The backlog drains: the spool has room again.
        publisher.Full = false;
        health.UsageBytes = 0;
        await WaitUntil(() => publisher.Accepted.Count >= 2, TimeSpan.FromSeconds(10));
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "A", "B" }, publisher.Accepted);
        Assert.False(source.Snapshot().Paused);
    }

    [Fact]
    public async Task Nothing_overtakes_a_refused_message_in_its_lane()
    {
        // Before the step-back, B and C were accepted while A sat un-acked
        // on the broker, so A arrived after them: FIFO broken within the lane.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var publisher = new SpoolDouble { RefuseNext = 1 };
        var options = Options(port, new MqttRoute { TopicFilter = "site/+/telemetry", Queue = "telemetry", GroupKeySegment = 1 });
        options.ClientId = "queuey-edge-test-fifo";
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await using (var producer = await Producer.ConnectAsync(port))
        {
            foreach (var body in new[] { "A", "B", "C" })
                await producer.PublishAsync("site/pole-1/telemetry", body);
        }

        await WaitUntil(() => publisher.Accepted.Count >= 3, TimeSpan.FromSeconds(10));
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "A", "B", "C" }, publisher.Accepted);
    }

    [Fact]
    public async Task A_message_larger_than_the_whole_spool_is_rejected_instead_of_waited_for()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var health = new HealthDouble { LimitBytes = 8 };
        var publisher = new SpoolDouble { Full = true };
        var options = Options(port, new MqttRoute { TopicFilter = "site/#", Queue = "telemetry" });
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance, health, intakes: null);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "site/pole-1/telemetry", "a payload longer than eight bytes");
        await WaitUntil(() => source.Snapshot().Counters.Rejected == 1);
        await Task.Delay(300);
        await source.StopAsync(CancellationToken.None);

        var snapshot = source.Snapshot();
        Assert.False(snapshot.Paused, "waiting for room that can never come would stop the intake for good");
        Assert.Equal(0, snapshot.Counters.Deferred);
    }

    [Fact]
    public async Task A_QoS_0_message_the_spool_cannot_take_is_counted_lost()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var publisher = new SpoolDouble { Full = true };
        var options = Options(port, new MqttRoute { TopicFilter = "site/#", Queue = "telemetry" });
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "site/pole-1/telemetry", "q0", MqttQualityOfServiceLevel.AtMostOnce);
        await WaitUntil(() => source.Snapshot().Counters.Lost == 1);
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.Snapshot().Counters.Deferred);
    }

    [Theory]
    [InlineData(MqttRetainedMessages.FirstSubscribe, 1)]
    [InlineData(MqttRetainedMessages.Skip, 0)]
    [InlineData(MqttRetainedMessages.All, 2)]
    public async Task A_retained_message_across_two_starts(MqttRetainedMessages mode, int expectedEvents)
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        await PublishAsync(port, "site/pole-1/state", "on", retain: true);

        var publisher = new SpoolDouble();
        var options = Options(port, new MqttRoute { TopicFilter = "site/+/state", Queue = "state", GroupKeySegment = 1 });
        options.ClientId = "queuey-edge-test-retained-" + mode;
        options.RetainedMessages = mode;

        for (var start = 1; start <= 2; start++)
        {
            var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);
            await source.StartAsync(CancellationToken.None);
            await broker.WaitForSubscriptionsAsync(start);
            await WaitUntil(() => source.Snapshot().Counters.Received >= 1);
            await source.StopAsync(CancellationToken.None);
        }

        Assert.Equal(expectedEvents, publisher.Accepted.Count);
    }

    [Fact]
    public async Task A_live_message_on_a_retained_topic_is_always_taken()
    {
        // MQTT marks a message retained toward a subscriber only when the
        // subscribe itself caused the send, so Skip must never touch live state.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var publisher = new SpoolDouble();
        var options = Options(port, new MqttRoute { TopicFilter = "site/+/state", Queue = "state" });
        options.RetainedMessages = MqttRetainedMessages.Skip;
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);

        await PublishAsync(port, "site/pole-1/state", "off", retain: true);
        await WaitUntil(() => publisher.Accepted.Count == 1);
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.Snapshot().Counters.RetainedSkipped);
    }

    [Fact]
    public async Task A_retained_copy_left_unacknowledged_is_taken_when_the_broker_sends_it_again()
    {
        // First start, full spool: the retained state is deferred. When the
        // source comes back the session exists, yet the copy is one Edge never
        // took, so it must not be skipped as a repeat.
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        await PublishAsync(port, "site/pole-1/state", "on", retain: true);

        var health = new HealthDouble { UsageBytes = HealthDouble.Limit };
        var publisher = new SpoolDouble { Full = true };
        var options = Options(port, new MqttRoute { TopicFilter = "site/+/state", Queue = "state" });
        options.ClientId = "queuey-edge-test-retained-deferred";
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance, health, intakes: null);

        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => source.Snapshot().Paused);

        publisher.Full = false;
        health.UsageBytes = 0;
        await WaitUntil(() => publisher.Accepted.Count >= 1, TimeSpan.FromSeconds(10));
        await Task.Delay(500); // the re-subscribe's fresh copy follows the re-send: it must not count twice
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "on" }, publisher.Accepted);
    }

    [Fact]
    public async Task The_source_reports_its_side_through_the_intake_registry()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        var registry = new EdgeIntakeRegistry();
        var publisher = new SpoolDouble();
        var options = Options(port, new MqttRoute { TopicFilter = "site/#", Queue = "telemetry" });
        var source = new MqttSource(options, publisher, NullLogger<MqttSource>.Instance, health: null, registry);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);
        await PublishAsync(port, "site/pole-1/telemetry", "x");
        await WaitUntil(() => publisher.Accepted.Count == 1);

        var intake = Assert.Single(registry.Snapshot());
        Assert.Equal($"mqtt:127.0.0.1:{port}", intake.Name);
        Assert.True(intake.Connected);
        Assert.False(intake.Paused);
        Assert.NotNull(intake.LastMessageAtUtc);
        Assert.NotNull(intake.LastConnectedAtUtc);
        Assert.Equal(new EdgeIntakeCounters(1, 1, 0, 0, 0, 0, 0, Duplicates: 0, WithoutId: 0), intake.Counters);

        await source.StopAsync(CancellationToken.None);
        Assert.False(registry.Snapshot()[0].Connected);
    }

    [Fact]
    public async Task Composed_with_Edge_the_source_shows_in_the_nodes_health()
    {
        var dir = Path.Combine(Path.GetTempPath(), "queuey-edge-mqtt-compose", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddQueueyEdge(o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_test";
                o.Storage.Path = Path.Combine(dir, "spool.db");
            });
            services.AddQueueyEdgeMqttSource(m =>
            {
                m.Server = "127.0.0.1";
                m.Port = FreePort(); // nothing listens: the source is constructed, not connected
                m.Routes.Add(new MqttRoute { TopicFilter = "site/#", Queue = "telemetry" });
            });
            await using var provider = services.BuildServiceProvider();

            var hosted = provider.GetServices<IHostedService>().OfType<MqttSource>();
            Assert.Single(hosted);
            var health = provider.GetRequiredService<IQueueyEdgeHealth>().Snapshot();

            var intake = Assert.Single(health.Intakes!);
            Assert.StartsWith("mqtt:127.0.0.1:", intake.Name);
            Assert.False(intake.Connected);
            Assert.NotNull(health.StorageLimitBytes);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── support ──────────────────────────────────────────────────────

    /// <summary><see cref="Options"/> as the configure callback <c>AddQueueyEdgeMqttSource</c> takes.</summary>
    internal static Action<MqttSourceOptions> Configure(int port, MqttRoute route, string server = "127.0.0.1") => m =>
    {
        var o = Options(port, route, server);
        m.Server = o.Server;
        m.Port = o.Port;
        m.ReconnectDelay = o.ReconnectDelay;
        m.ReconnectDelayCap = o.ReconnectDelayCap;
        m.Routes.AddRange(o.Routes);
    };

    internal static MqttSourceOptions Options(int port, MqttRoute route, string server = "127.0.0.1")
    {
        var o = new MqttSourceOptions
        {
            Server = server,
            Port = port,
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            ReconnectDelayCap = TimeSpan.FromMilliseconds(400)
        };
        o.Routes.Add(route);
        return o;
    }

    /// <summary>
    /// The in-process broker plus the one signal these tests actually need:
    /// a SUBSCRIPTION being registered.
    ///
    /// Waiting for a connected client is not enough. <see cref="MqttSource"/>
    /// connects and then subscribes, so a publish that lands in between
    /// reaches a broker with nothing subscribed to the topic, and the message
    /// is dropped with no retry — QoS 1 guarantees delivery to subscribers,
    /// not to a subscriber that has not arrived yet. Isolated, that window is
    /// microseconds; under a full parallel test run it is wide enough to hit,
    /// which is what made these tests fail roughly one run in three.
    /// </summary>
    internal sealed class TestBroker : IDisposable
    {
        private readonly List<string> _clientIds = new();
        private int _subscriptions;
        private int _acknowledged;

        private TestBroker(MqttServer server)
        {
            Server = server;
            server.ClientSubscribedTopicAsync += _ =>
            {
                Interlocked.Increment(ref _subscriptions);
                return Task.CompletedTask;
            };
            server.ClientConnectedAsync += e =>
            {
                if (!e.ClientId.StartsWith("test-producer-", StringComparison.Ordinal))
                    lock (_clientIds) _clientIds.Add(e.ClientId);
                return Task.CompletedTask;
            };
            server.ClientAcknowledgedPublishPacketAsync += _ =>
            {
                Interlocked.Increment(ref _acknowledged);
                return Task.CompletedTask;
            };
        }

        public MqttServer Server { get; }

        /// <summary>The client ids subscribers connected with, in order; the test producers are left out.</summary>
        public IReadOnlyList<string> ClientIds { get { lock (_clientIds) return _clientIds.ToArray(); } }

        public static async Task<TestBroker> StartAsync(int port)
        {
            var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).WithPersistentSessions().Build();
            var broker = new TestBroker(new MqttServerFactory().CreateMqttServer(options));
            await broker.Server.StartAsync();
            return broker;
        }

        public Task WaitForSubscriptionsAsync(int atLeast, TimeSpan? timeout = null)
            => WaitUntil(() => Volatile.Read(ref _subscriptions) >= atLeast, timeout);

        /// <summary>
        /// Waits for the subscriber's acknowledgements. The source acknowledges a message only after it counted the
        /// outcome, so after this its counters are final: a stop can no longer catch a message between the spool and
        /// the ack, and count it deferred as well. Waiting for <c>Received</c> instead leaves that window open, and
        /// the I/O of tests running alongside widens it.
        /// </summary>
        public Task WaitForAcknowledgedAsync(int atLeast, TimeSpan? timeout = null)
            => WaitUntil(() => Volatile.Read(ref _acknowledged) >= atLeast, timeout);

        public void Dispose() => Server.Dispose();
    }

    internal sealed class Producer : IAsyncDisposable
    {
        private readonly IMqttClient _client;

        private Producer(IMqttClient client) => _client = client;

        public static async Task<Producer> ConnectAsync(int port, string server = "127.0.0.1")
        {
            var client = new MqttClientFactory().CreateMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(server, port)
                .WithClientId("test-producer-" + Guid.NewGuid().ToString("N")[..6]).Build());
            return new Producer(client);
        }

        public Task PublishAsync(string topic, string body,
            MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce, bool retain = false)
            => _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(body)
                .WithQualityOfServiceLevel(qos).WithRetainFlag(retain).Build());

        public async ValueTask DisposeAsync()
        {
            await _client.DisconnectAsync();
            _client.Dispose();
        }
    }

    internal static async Task PublishAsync(int port, string topic, string body,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce, bool retain = false)
    {
        await using var producer = await Producer.ConnectAsync(port);
        await producer.PublishAsync(topic, body, qos, retain);
    }

    internal static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }

    internal static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    internal sealed record PublishCall(string Queue, string Body, string ContentType, PublishOptions Options);

    /// <summary>
    /// Stands in for the spool: accepts in order, or refuses the way a full
    /// spool does — while <see cref="Full"/>, or for the next <see cref="RefuseNext"/> calls.
    /// </summary>
    internal sealed class SpoolDouble : IQueueyPublisher
    {
        private readonly object _gate = new();
        private readonly List<PublishCall> _calls = new();
        private int _refuseNext;

        public volatile bool Full;

        public int RefuseNext
        {
            get { lock (_gate) return _refuseNext; }
            set { lock (_gate) _refuseNext = value; }
        }

        public IReadOnlyList<PublishCall> Calls { get { lock (_gate) return _calls.ToArray(); } }

        public IReadOnlyList<string> Accepted => Calls.Select(c => c.Body).ToArray();

        public Task<PublishReceipt> PublishAsync<T>(string queue, T payload, PublishOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PublishReceipt> PublishAsync(string queue, byte[] payload, string contentType, PublishOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (Full) throw new QueueySpoolFullException("test: spool full");
                if (_refuseNext > 0)
                {
                    _refuseNext--;
                    throw new QueueySpoolFullException("test: spool full");
                }
                _calls.Add(new PublishCall(queue, Encoding.UTF8.GetString(payload), contentType, options ?? new PublishOptions()));
            }
            return Task.FromResult(new PublishReceipt
            {
                TransferId = Guid.NewGuid().ToString("N"),
                Queue = queue,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                OccurredAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>The spool's fill level as the source reads it when deciding whether to come back.</summary>
    internal sealed class HealthDouble : IQueueyEdgeHealth
    {
        public const long Limit = 1_000_000;

        public volatile bool Faulted;
        private long _usageBytes;
        private long _limitBytes = Limit;

        public long UsageBytes
        {
            get => Interlocked.Read(ref _usageBytes);
            set => Interlocked.Exchange(ref _usageBytes, value);
        }

        public long LimitBytes
        {
            get => Interlocked.Read(ref _limitBytes);
            set => Interlocked.Exchange(ref _limitBytes, value);
        }

        public EdgeHealth Snapshot() => new(
            State: Faulted ? EdgeState.StorageFaulted : UsageBytes >= LimitBytes ? EdgeState.StorageFull : EdgeState.Healthy,
            PendingCount: 0,
            OldestPendingAge: null,
            StorageUsageBytes: UsageBytes,
            LastSuccessfulCloudContact: null,
            LastTransferFailure: null,
            QuarantinedCount: 0,
            StorageDurabilityWarning: false,
            StorageLimitBytes: LimitBytes);
    }
}
