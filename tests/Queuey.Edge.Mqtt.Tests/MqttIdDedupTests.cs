using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Edge.Mqtt;
using static Queuey.Edge.Mqtt.Tests.MqttSourceTests;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// Deduplication on the producer's event id, end to end through the in-process broker, the real Edge publisher and
/// the real spool: a message that reaches Edge twice is one event, and a reused id with new content is a new one.
/// </summary>
public class MqttIdDedupTests
{
    private const string Filter = "site/+/telemetry";

    [Fact]
    public async Task The_same_event_id_and_payload_twice_is_one_event_and_one_duplicate()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        await using var edge = RealEdge.Create();
        var source = Source(port, edge);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);
        await using (var producer = await Producer.ConnectAsync(port))
        {
            await producer.PublishAsync("site/node-07/telemetry", """{"eventId":"node-07-000123","temp":21.5}""");
            await producer.PublishAsync("site/node-07/telemetry", """{"eventId":"node-07-000123","temp":21.5}""");
        }
        await WaitUntil(() => source.Snapshot().Counters.Received == 2);
        await source.StopAsync(CancellationToken.None);

        var counters = source.Snapshot().Counters;
        Assert.Equal(1, counters.Accepted);
        Assert.Equal(1, counters.Duplicates);
        Assert.Equal(0, counters.WithoutId);
        Assert.Equal(1, await edge.PendingAsync());
    }

    [Fact]
    public async Task The_same_event_id_with_other_content_is_two_events()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        await using var edge = RealEdge.Create();
        var source = Source(port, edge);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);
        await using (var producer = await Producer.ConnectAsync(port))
        {
            // A device whose counter restarted: the id comes again, with new content.
            await producer.PublishAsync("site/node-07/telemetry", """{"eventId":1,"temp":21.5}""");
            await producer.PublishAsync("site/node-07/telemetry", """{"eventId":1,"temp":22.0}""");
        }
        await WaitUntil(() => source.Snapshot().Counters.Received == 2);
        await source.StopAsync(CancellationToken.None);

        var counters = source.Snapshot().Counters;
        Assert.Equal(2, counters.Accepted);
        Assert.Equal(0, counters.Duplicates);
        Assert.Equal(2, await edge.PendingAsync());
    }

    [Fact]
    public async Task A_message_without_an_id_is_still_taken_and_counted()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        await using var edge = RealEdge.Create();
        var logger = new WarningLog();
        var source = Source(port, edge, logger: logger);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);
        await using (var producer = await Producer.ConnectAsync(port))
        {
            // Without an id there is nothing to recognise a repeat by: both are events, as before.
            await producer.PublishAsync("site/node-07/telemetry", """{"temp":21.5}""");
            await producer.PublishAsync("site/node-07/telemetry", """{"temp":21.5}""");
            await producer.PublishAsync("site/node-07/telemetry", "not json");
        }
        await WaitUntil(() => source.Snapshot().Counters.Received == 3);
        await source.StopAsync(CancellationToken.None);

        var counters = source.Snapshot().Counters;
        Assert.Equal(3, counters.Accepted);
        Assert.Equal(3, counters.WithoutId);
        Assert.Equal(3, await edge.PendingAsync());
        Assert.Equal(1, logger.Count(7407)); // once per source, not once per message
    }

    [Fact]
    public async Task A_route_without_an_id_field_counts_nothing_as_without_id()
    {
        var port = FreePort();
        using var broker = await TestBroker.StartAsync(port);
        await using var edge = RealEdge.Create();
        var source = Source(port, edge, idField: null);

        await source.StartAsync(CancellationToken.None);
        await broker.WaitForSubscriptionsAsync(1);
        await using (var producer = await Producer.ConnectAsync(port))
        {
            await producer.PublishAsync("site/node-07/telemetry", """{"eventId":"a"}""");
            await producer.PublishAsync("site/node-07/telemetry", """{"eventId":"a"}""");
        }
        await WaitUntil(() => source.Snapshot().Counters.Received == 2);
        await source.StopAsync(CancellationToken.None);

        Assert.Equal(new EdgeIntakeCounters(2, 2, 0, 0, 0, 0, 0, Duplicates: 0, WithoutId: 0), source.Snapshot().Counters);
    }

    private static MqttSource Source(int port, RealEdge edge, string? idField = "eventId", ILogger<MqttSource>? logger = null)
    {
        var options = Options(port, new MqttRoute { TopicFilter = Filter, Queue = "telemetry", GroupKeySegment = 1, IdempotencyKeyField = idField });
        return new MqttSource(options, edge.Publisher, logger ?? NullLogger<MqttSource>.Instance);
    }

    /// <summary>Counts the warnings the source logs, by event id.</summary>
    private sealed class WarningLog : ILogger<MqttSource>
    {
        private readonly List<int> _warnings = new();

        public int Count(int eventId) { lock (_warnings) return _warnings.Count(id => id == eventId); }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (_warnings) _warnings.Add(eventId.Id);
        }
    }
}
