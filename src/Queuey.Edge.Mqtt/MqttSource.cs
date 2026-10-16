using System;
using System.Buffers;
using System.Collections.Generic;
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
/// us can be lost between broker and Edge.
///
/// <para>When the spool cannot take a message (full, faulted, or any other
/// refusal), the source <b>steps back from the broker</b> instead of moving
/// on: it hands nothing more to the spool, leaves every message
/// unacknowledged, disconnects, and reconnects only once the spool has room
/// for the refused message. MQTT re-sends an unacknowledged message only on
/// a reconnect, so staying connected would strand the refused one until
/// something else broke the connection, and let later messages overtake it.
/// The persistent session keeps the unacknowledged messages and everything
/// published meanwhile on the broker, within the broker's own queue
/// limits.</para>
///
/// <para>Semantics are at-least-once from broker to spool: a redelivery
/// after a crash between fsync and ack becomes a second event. MQTT has no
/// message identity to dedupe on, so this is documented, not hidden.</para>
///
/// <para>Ordering: messages are handled one at a time in arrival order (the
/// client invokes the handler sequentially), a topic segment can become the
/// lane (<see cref="MqttRoute.GroupKeySegment"/>) so per-machine order
/// survives all the way to Cloud, and after a refusal nothing is accepted
/// before the refused message comes back.</para>
/// </summary>
public sealed class MqttSource : BackgroundService, IEdgeIntake
{
    private static readonly TimeSpan PauseCheckInterval = TimeSpan.FromSeconds(1);

    private readonly MqttSourceOptions _options;
    private readonly IQueueyPublisher _publisher;
    private readonly IQueueyEdgeHealth? _health;
    private readonly ILogger<MqttSource> _logger;
    private readonly string _name;

    // Wakes the loop the moment the handler asks it to step back.
    private readonly SemaphoreSlim _stepBack = new(0, 1);

    // Retained copies Edge meant to take but left unacknowledged, by topic.
    // The broker re-sends them with RETAIN set on a session it already has,
    // and not every broker marks the re-send DUP, so without this they
    // would be skipped as repeats although Edge never took them.
    private readonly Dictionary<string, byte[]> _deferredRetained = new(StringComparer.Ordinal);

    // Shared by the handler (one message at a time) and the loop.
    private volatile bool _paused;
    private volatile bool _connected;
    private volatile bool _sessionPresent;
    private volatile bool _lossWarned;
    private volatile string? _lastError;
    private long _refusedBytes;
    private long _resumeDelayTicks;
    private long _lastMessageAtTicks;
    private long _lastConnectedAtTicks;

    private long _received;
    private long _accepted;
    private long _deferred;
    private long _rejected;
    private long _unrouted;
    private long _retainedSkipped;
    private long _lost;

    /// <summary>A source without health access: after a refusal it resumes on the reconnect ladder alone.</summary>
    public MqttSource(MqttSourceOptions options, IQueueyPublisher publisher, ILogger<MqttSource> logger)
        : this(options, publisher, logger, health: null, intakes: null)
    {
    }

    /// <summary>
    /// The source as <c>AddQueueyEdgeMqttSource</c> composes it. With
    /// <paramref name="health"/>, a source that stepped back resumes when the
    /// spool has room for the refused message; with <paramref name="intakes"/>,
    /// its side shows in the node's health, meter and health report.
    /// </summary>
    public MqttSource(
        MqttSourceOptions options,
        IQueueyPublisher publisher,
        ILogger<MqttSource> logger,
        IQueueyEdgeHealth? health,
        EdgeIntakeRegistry? intakes)
    {
        _options = options;
        _publisher = publisher;
        _logger = logger;
        _health = health;
        _name = $"mqtt:{options.Server}:{options.Port}";
        _resumeDelayTicks = options.ReconnectDelay.Ticks;
        intakes?.Register(this);
    }

    /// <inheritdoc />
    public EdgeIntakeHealth Snapshot() => new(
        Name: _name,
        Connected: _connected,
        Paused: _paused,
        LastMessageAtUtc: AtUtc(ref _lastMessageAtTicks),
        LastConnectedAtUtc: AtUtc(ref _lastConnectedAtTicks),
        LastError: _lastError,
        Counters: new EdgeIntakeCounters(
            Received: Interlocked.Read(ref _received),
            Accepted: Interlocked.Read(ref _accepted),
            Deferred: Interlocked.Read(ref _deferred),
            Rejected: Interlocked.Read(ref _rejected),
            Unrouted: Interlocked.Read(ref _unrouted),
            RetainedSkipped: Interlocked.Read(ref _retainedSkipped),
            Lost: Interlocked.Read(ref _lost)));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e => OnMessageAsync(e, stoppingToken);
        client.DisconnectedAsync += _ =>
        {
            _connected = false;
            return Task.CompletedTask;
        };

        var delay = _options.ReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_paused)
                    await StepBackAsync(client, stoppingToken).ConfigureAwait(false);

                if (!client.IsConnected)
                {
                    var connected = await client.ConnectAsync(BuildClientOptions(), stoppingToken).ConfigureAwait(false);
                    _sessionPresent = connected.IsSessionPresent;
                    await client.SubscribeAsync(BuildSubscribeOptions(), stoppingToken).ConfigureAwait(false);
                    _connected = true;
                    _lastError = null;
                    Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    delay = _options.ReconnectDelay;
                    _logger.LogInformation(MqttLogEvents.Connected,
                        "MQTT source connected to {Server}:{Port}; {Routes} route(s) subscribed at QoS 1 (broker session {Session}).",
                        _options.Server, _options.Port, _options.Routes.Count, _sessionPresent ? "resumed" : "new");
                }

                await _stepBack.WaitAsync(PauseCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _connected = false;
                _lastError = ex.Message;
                _logger.LogWarning(MqttLogEvents.Disconnected, ex,
                    "MQTT source not connected to {Server}:{Port}; retrying in {Delay}.", _options.Server, _options.Port, delay);
                try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectDelayCap.Ticks));
            }
        }

        _connected = false;
        try { if (client.IsConnected) await client.DisconnectAsync().ConfigureAwait(false); }
        catch { /* shutdown */ }
    }

    /// <summary>
    /// Disconnects and waits until the spool has room for the refused message,
    /// then lets the loop reconnect, which makes the broker re-send everything
    /// left unacknowledged, refused message first. Back-to-back step-backs
    /// wait longer each time (the reconnect ladder), so a spool that keeps
    /// refusing does not turn into a reconnect storm.
    /// </summary>
    private async Task StepBackAsync(IMqttClient client, CancellationToken ct)
    {
        if (client.IsConnected)
        {
            try { await client.DisconnectAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "MQTT disconnect while stepping back failed; the connection is gone either way.");
            }
        }
        _connected = false;

        var refusedBytes = Interlocked.Read(ref _refusedBytes);
        var minimumWait = TimeSpan.FromTicks(Interlocked.Read(ref _resumeDelayTicks));
        var step = minimumWait < PauseCheckInterval ? minimumWait : PauseCheckInterval;
        if (step <= TimeSpan.Zero) step = PauseCheckInterval;

        var waited = TimeSpan.Zero;
        while (waited < minimumWait || !SpoolHasRoomFor(refusedBytes))
        {
            await Task.Delay(step, ct).ConfigureAwait(false);
            waited += step;
        }

        Interlocked.Exchange(ref _resumeDelayTicks, Math.Min(minimumWait.Ticks * 2, _options.ReconnectDelayCap.Ticks));
        _lastError = null;
        _lossWarned = false;
        _paused = false;
        _logger.LogInformation(MqttLogEvents.Resumed,
            "MQTT source resuming after {Waited}: the spool has room again, and the broker re-sends what was left unacknowledged.",
            waited);
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e, CancellationToken ct)
    {
        e.AutoAcknowledge = false;
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastMessageAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        var message = e.ApplicationMessage;
        var topic = message.Topic ?? string.Empty;
        var atMostOnce = message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce;
        var payload = message.Payload.IsEmpty
            ? Array.Empty<byte>()
            : message.Payload.ToArray();
        var takesRetained = message.Retain && TakesRetained(message, topic, payload);

        if (_paused)
        {
            // Stepping back: nothing may overtake the refused message, so this
            // one waits for the redelivery as well.
            if (takesRetained && !atMostOnce) RememberDeferredRetained(topic, payload);
            Defer(atMostOnce, topic);
            return;
        }

        if (message.Retain && !takesRetained)
        {
            // A copy the broker re-sent because we subscribed, not a new
            // message. Acked so the broker stops holding it for us.
            Interlocked.Increment(ref _retainedSkipped);
            await e.AcknowledgeAsync(ct).ConfigureAwait(false);
            return;
        }

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

        var contentType = string.IsNullOrWhiteSpace(message.ContentType)
            ? route.DefaultContentType
            : message.ContentType;

        try
        {
            await _publisher.PublishAsync(route.Queue, payload, contentType, new PublishOptions
            {
                GroupKey = MqttTopics.GroupKey(route, topic),
                EventType = route.EventType ?? topic,
                Source = "mqtt:" + topic
            }, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _accepted);
            Interlocked.Exchange(ref _resumeDelayTicks, _options.ReconnectDelay.Ticks);
            await e.AcknowledgeAsync(ct).ConfigureAwait(false); // AFTER the durable commit — the whole point
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Not acked: the broker redelivers after we come back.
            if (takesRetained && !atMostOnce) RememberDeferredRetained(topic, payload);
            Defer(atMostOnce, topic);
        }
        catch (QueueyPayloadRejectedException ex)
        {
            // Edge itself will never accept this payload (too large for the
            // local cap). Acking is right: redelivery cannot fix it, and one
            // bad message must not freeze the topic. Logged loudly instead.
            await RejectAsync(e, topic, payload.Length, ex, "is over the local MaxPayloadBytes", ct).ConfigureAwait(false);
        }
        catch (QueueySpoolFullException ex) when (NeverFits(payload.Length))
        {
            // Bigger than the whole spool: no amount of draining makes room,
            // and stepping back for it would stop the intake for good.
            await RejectAsync(e, topic, payload.Length, ex, "is larger than the whole spool (MaxSpoolBytes)", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Spool full / storage faulted / anything else: NOT acked, and the
            // source steps back so the broker re-sends it once there is room.
            if (takesRetained && !atMostOnce) RememberDeferredRetained(topic, payload);
            Defer(atMostOnce, topic);
            RequestStepBack(payload.Length, topic, ex);
        }
    }

    private bool TakesRetained(MqttApplicationMessage message, string topic, byte[] payload) => _options.RetainedMessages switch
    {
        MqttRetainedMessages.All => true,
        MqttRetainedMessages.Skip => false,
        // A retained copy on a session the broker already had is a repeat of
        // state Edge has seen, except when it is a re-send of a copy Edge
        // meant to take and left unacknowledged: marked DUP by brokers that
        // follow the spec, and remembered here for the ones that do not.
        _ => !_sessionPresent || message.Dup || TakeDeferredRetained(topic, payload)
    };

    private void RememberDeferredRetained(string topic, byte[] payload)
    {
        lock (_deferredRetained) _deferredRetained[topic] = payload;
    }

    private bool TakeDeferredRetained(string topic, byte[] payload)
    {
        lock (_deferredRetained)
        {
            if (!_deferredRetained.TryGetValue(topic, out var deferred) || !deferred.AsSpan().SequenceEqual(payload))
                return false;
            _deferredRetained.Remove(topic);
            return true;
        }
    }

    private void Defer(bool atMostOnce, string topic)
    {
        if (!atMostOnce)
        {
            Interlocked.Increment(ref _deferred);
            return;
        }

        // QoS 0 has no acknowledgement and no redelivery: what Edge cannot
        // take now is gone. Counted every time, and logged once per
        // step-back so a chatty QoS 0 publisher cannot flood the log.
        Interlocked.Increment(ref _lost);
        if (_lossWarned) return;
        _lossWarned = true;
        _logger.LogWarning(MqttLogEvents.Lost,
            "MQTT message on {Topic} was published at QoS 0 and Edge could not take it; the broker will not send it again, " +
            "and further QoS 0 losses until the source resumes are only counted (queuey.edge.intake.messages, outcome=lost). " +
            "Publish at QoS 1 for Edge's guarantee.",
            topic);
    }

    private void RequestStepBack(long payloadBytes, string topic, Exception ex)
    {
        Interlocked.Exchange(ref _refusedBytes, payloadBytes);
        _lastError = ex.Message;
        _paused = true;
        _logger.LogWarning(MqttLogEvents.Refused, ex,
            "MQTT message on {Topic} not accepted by Edge; left unacknowledged, and the source steps back from the broker until the spool has room.",
            topic);
        try { _stepBack.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    private async Task RejectAsync(
        MqttApplicationMessageReceivedEventArgs e, string topic, int payloadBytes, Exception ex, string why, CancellationToken ct)
    {
        Interlocked.Increment(ref _rejected);
        _logger.LogError(MqttLogEvents.Rejected, ex,
            "MQTT message on {Topic} rejected by Edge and dropped: its payload ({Bytes} B) {Why}.", topic, payloadBytes, why);
        await e.AcknowledgeAsync(ct).ConfigureAwait(false);
    }

    private bool SpoolHasRoomFor(long payloadBytes)
    {
        if (_health is null) return true; // resume on the ladder; a refusal just steps back again
        try
        {
            return _health.Snapshot().HasRoomFor(payloadBytes);
        }
        catch (Exception)
        {
            return true; // trying is safe: a spool that still cannot take it refuses again
        }
    }

    private bool NeverFits(long payloadBytes)
    {
        try
        {
            return _health?.Snapshot().StorageLimitBytes is { } limit && payloadBytes > limit;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DateTimeOffset? AtUtc(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
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

    /// <inheritdoc />
    public override void Dispose()
    {
        _stepBack.Dispose();
        base.Dispose();
    }
}

internal static class MqttLogEvents
{
    public static readonly EventId Connected = new(7401, "queuey.edge.mqtt.connected");
    public static readonly EventId Disconnected = new(7402, "queuey.edge.mqtt.disconnected");
    public static readonly EventId Rejected = new(7403, "queuey.edge.mqtt.rejected");
    public static readonly EventId Refused = new(7404, "queuey.edge.mqtt.refused");
    public static readonly EventId Resumed = new(7405, "queuey.edge.mqtt.resumed");
    public static readonly EventId Lost = new(7406, "queuey.edge.mqtt.lost");
}
