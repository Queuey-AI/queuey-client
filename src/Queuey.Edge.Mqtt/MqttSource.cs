using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using Queuey.Client;

namespace Queuey.Edge.Mqtt;

/// <summary>
/// The MQTT intake loop. One rule makes it honest: <b>a message is
/// acknowledged to the broker only after the spool has fsync'd it</b> —
/// so with QoS 1 (the default we subscribe with) nothing the broker handed
/// us can be lost between broker and Edge. A publish that Edge refuses
/// (spool full, storage faulted) is NOT acknowledged; the broker redelivers
/// it after reconnect, which is the correct backpressure.
///
/// <para>Semantics are at-least-once from broker to spool: a redelivery
/// after a crash between fsync and ack becomes a second event. MQTT has no
/// message identity to dedupe on, so this is documented, not hidden.</para>
///
/// <para>Ordering: messages are handled one at a time in arrival order (the
/// client invokes the handler sequentially), and a topic segment can become
/// the lane (<see cref="MqttRoute.GroupKeySegment"/>) so per-machine order
/// survives all the way to Cloud.</para>
/// </summary>
public sealed class MqttSource : BackgroundService
{
    private readonly MqttSourceOptions _options;
    private readonly IQueueyPublisher _publisher;
    private readonly ILogger<MqttSource> _logger;

    private long _received;
    private long _accepted;
    private long _unrouted;
    private long _refused;

    public MqttSource(MqttSourceOptions options, IQueueyPublisher publisher, ILogger<MqttSource> logger)
    {
        _options = options;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Counters for the host's own logs/health; not persisted.</summary>
    public (long Received, long Accepted, long Unrouted, long Refused) Counters
        => (Interlocked.Read(ref _received), Interlocked.Read(ref _accepted), Interlocked.Read(ref _unrouted), Interlocked.Read(ref _refused));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e => OnMessageAsync(e, stoppingToken);

        var delay = _options.ReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(BuildClientOptions(), stoppingToken).ConfigureAwait(false);
                    await client.SubscribeAsync(BuildSubscribeOptions(), stoppingToken).ConfigureAwait(false);
                    delay = _options.ReconnectDelay;
                    _logger.LogInformation(MqttLogEvents.Connected,
                        "MQTT source connected to {Server}:{Port}; {Routes} route(s) subscribed at QoS 1.",
                        _options.Server, _options.Port, _options.Routes.Count);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(MqttLogEvents.Disconnected, ex,
                    "MQTT source not connected to {Server}:{Port}; retrying in {Delay}.", _options.Server, _options.Port, delay);
                try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectDelayCap.Ticks));
            }
        }

        try { if (client.IsConnected) await client.DisconnectAsync().ConfigureAwait(false); }
        catch { /* shutdown */ }
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e, CancellationToken ct)
    {
        Interlocked.Increment(ref _received);
        e.AutoAcknowledge = false;

        var topic = e.ApplicationMessage.Topic ?? string.Empty;
        var route = MqttTopics.Resolve(_options, topic);
        if (route is null)
        {
            // A subscription we asked for that no route claims (overlapping
            // filters). Ack it — it is not ours, and holding it would only
            // block the broker's queue.
            Interlocked.Increment(ref _unrouted);
            await e.AcknowledgeAsync(ct).ConfigureAwait(false);
            return;
        }

        var payload = e.ApplicationMessage.Payload.IsEmpty
            ? Array.Empty<byte>()
            : e.ApplicationMessage.Payload.ToArray();
        var contentType = string.IsNullOrWhiteSpace(e.ApplicationMessage.ContentType)
            ? route.DefaultContentType
            : e.ApplicationMessage.ContentType;

        try
        {
            await _publisher.PublishAsync(route.Queue, payload, contentType, new PublishOptions
            {
                GroupKey = MqttTopics.GroupKey(route, topic),
                EventType = route.EventType ?? topic,
                Source = "mqtt:" + topic
            }, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _accepted);
            await e.AcknowledgeAsync(ct).ConfigureAwait(false); // AFTER the durable commit — the whole point
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Not acked: the broker redelivers after we come back.
        }
        catch (QueueyPayloadRejectedException ex)
        {
            // Edge itself will never accept this payload (too large for the
            // local cap). Acking is right: redelivery cannot fix it, and one
            // bad message must not freeze the topic. Logged loudly instead.
            Interlocked.Increment(ref _refused);
            _logger.LogError(MqttLogEvents.Rejected, ex,
                "MQTT message on {Topic} rejected by Edge and dropped (payload {Bytes} B).", topic, payload.Length);
            await e.AcknowledgeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Spool full / storage faulted / anything transient: NOT acked.
            // Backpressure flows to the broker, which is exactly where it belongs.
            Interlocked.Increment(ref _refused);
            _logger.LogWarning(MqttLogEvents.Refused, ex,
                "MQTT message on {Topic} not accepted by Edge; left unacknowledged for redelivery.", topic);
        }
    }

    private MqttClientOptions BuildClientOptions()
    {
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Server, _options.Port)
            .WithClientId(string.IsNullOrWhiteSpace(_options.ClientId) ? $"queuey-edge-{Environment.MachineName}" : _options.ClientId)
            .WithCleanSession(false)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311);
        if (!string.IsNullOrEmpty(_options.Username))
            b = b.WithCredentials(_options.Username, _options.Password);
        if (_options.UseTls)
            b = b.WithTlsOptions(o => o.UseTls());
        return b.Build();
    }

    private MqttClientSubscribeOptions BuildSubscribeOptions()
    {
        var b = new MqttClientSubscribeOptionsBuilder();
        foreach (var r in _options.Routes)
            b = b.WithTopicFilter(r.TopicFilter, MqttQualityOfServiceLevel.AtLeastOnce);
        return b.Build();
    }
}

internal static class MqttLogEvents
{
    public static readonly EventId Connected = new(7401, "queuey.edge.mqtt.connected");
    public static readonly EventId Disconnected = new(7402, "queuey.edge.mqtt.disconnected");
    public static readonly EventId Rejected = new(7403, "queuey.edge.mqtt.rejected");
    public static readonly EventId Refused = new(7404, "queuey.edge.mqtt.refused");
}
